using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Provenance;

/// <summary>What the device's own provenance backend does beyond its defaults.</summary>
public sealed record LocalProvenanceOptions
{
    /// <summary>
    /// The RFC 3161 time-stamp authority a record's signature is sent to when the machine policy says <c>on</c> (only the
    /// signature's SHA-256 leaves the device). A policy address replaces it; <c>off</c>, or null here, sends nothing.
    /// </summary>
    public Uri? TimestampAuthority { get; init; } = DefaultTimestampAuthority;

    /// <summary>DigiCert's public service (owner, 14 Nov). Plain http is fine: the token is signed, and only a hash leaves.</summary>
    public static Uri DefaultTimestampAuthority { get; } = new("http://timestamp.digicert.com");

    /// <summary>Whether records are time-stamped at all (the app's own switch; the machine policy has its own).</summary>
    public bool Timestamping { get; init; } = true;

    /// <summary>How long the authority may take before the record goes without a token and waits in the queue.</summary>
    public TimeSpan TimestampTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Roots trusted for the authority's certificate besides this computer's own store: an organisation's own time-stamp
    /// server. A token whose signer chains to neither does not count.
    /// </summary>
    public X509Certificate2Collection? TimestampRoots { get; init; }

    /// <summary>How many queued records are stamped at a time, once the authority answers again.</summary>
    public int QueueBatch { get; init; } = 20;

    /// <summary>Where the backend logs what it skips (event ids only, never a value); the agent's logger once it signs a record, else none.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The handler the time-stamp request goes through (tests, a proxy); the SDK's own when null.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>
    /// Who provides the AI system, as the record names it: <c>standalone</c> (owner, 14 Nov); a server names its organisation
    /// there, and an app may set the organisation's name.
    /// </summary>
    public string Provider { get; init; } = "standalone";

    /// <summary>The signer; the installation's (a key per OS user, kept by the OS key store) when null.</summary>
    public DeviceSigner? Signer { get; init; }

    /// <summary>Where the records, the queue and the placement log go; the standalone user's <c>provenance</c> folder when null.</summary>
    public string? Folder { get; init; }

    /// <summary>The largest the placement log grows before it rotates.</summary>
    public long MaxLogBytes { get; init; } = 1024 * 1024;

    /// <summary>How many rotated logs are kept besides the current one.</summary>
    public int KeepLogs { get; init; } = 3;

    /// <summary>The time, for the record and the queue; tests set it.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }
}

/// <summary>
/// Provenance with no server (sdk/51 F10): the device's own policy defaults, records signed on the device in the server's
/// format (<see cref="DeviceSigner"/>) with an RFC 3161 time-stamp from DigiCert, and placements in a local log that rotates.
/// Nothing leaves the device but the signature's hash to the time-stamp authority, and that only when the policy allows.
/// </summary>
/// <remarks>
/// <para>
/// The local backup (owner, 14 Nov): every signed record is kept on the device with its token. When the authority cannot be
/// reached, the record keeps the device's clock, labelled as such (<c>time_source: device-clock</c>), and waits in the queue
/// (it survives a restart: the queue is the kept records still waiting); it is stamped when the authority answers again,
/// so it then also proves it existed no later than that time.
/// </para>
/// <para>
/// The record's <c>content_hash</c> is the plain SHA-256 of the content (<c>hash_key_version</c> 0): a server's is a keyed
/// HMAC under its own key version, which a device does not have.
/// </para>
/// </remarks>
public sealed class LocalProvenanceBackend : IProvenanceBackend, IDisposable
{
    /// <summary>The <c>hash_key_version</c> of a device's record: the content's plain SHA-256.</summary>
    public const int PlainHashKeyVersion = 0;

    /// <summary>What dated the record: the authority's token, or the device's clock (labelled, until a token comes).</summary>
    public const string AuthorityTime = "authority";

    public const string DeviceClockTime = "device-clock";

    private const string SourceTypes = "http://cv.iptc.org/newscodes/digitalsourcetype/";
    private const string StandaloneId = "standalone";

    private readonly LocalProvenanceOptions _options;
    private readonly Lazy<DeviceSigner> _signer;
    private readonly Lazy<HttpClient> _http;
    private readonly object _logLock = new();
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private ILogger _logger;

    public LocalProvenanceBackend(LocalProvenanceOptions? options = null)
    {
        _options = options ?? new LocalProvenanceOptions();
        _logger = _options.Logger ?? NullLogger.Instance;
        _signer = new Lazy<DeviceSigner>(() => _options.Signer ?? DeviceSigner.ForInstallation(
            Path.Combine(FadenstackPaths.User(StandaloneId, "keys"), "provenance-signing.key"), SecretProtection.Default));
        _http = new Lazy<HttpClient>(() => _options.Handler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
    }

    public LocalProvenanceOptions Options => _options;

    /// <summary>The device's signer (made, or read from the OS key store, on first use).</summary>
    public DeviceSigner Signer => _signer.Value;

    private DateTimeOffset Now => _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    private string Folder => _options.Folder ?? FadenstackPaths.User(StandaloneId, "provenance");

    private string RecordsFolder => Path.Combine(Folder, "records");

    /// <summary>The local defaults: mark everything but standard editing (the default exempt kinds), record what is missing.</summary>
    public Task<ProvenancePolicy> GetPolicyAsync(FadenstackAgent agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ProvenancePolicy());

    public async Task<SignedRecord> SignRecordAsync(FadenstackAgent agent, RecordRequest request, CancellationToken cancellationToken)
    {
        UseLogger(agent);
        var app = agent.App;
        var record = new ProvenanceRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            DigitalSourceType = SourceTypes + (request.EditKind == EditKind.Generated
                ? "trainedAlgorithmicMedia" : "compositeWithTrainedAlgorithmicMedia"),
            EditKind = request.EditKind,
            System = new ProvenanceSystem
            {
                Name = app?.Name is { Length: > 0 } name ? name : "Fadenstack SDK",
                Version = app?.Version is { Length: > 0 } version ? version : SdkInfo.Version,
            },
            Provider = _options.Provider,
            CreatedAt = Truncated(Now),
            MediaType = request.MediaType,
            ContentHash = request.ContentSha256,
            HashKeyVersion = PlainHashKeyVersion,
            TextHash = request.TextSha256,
            Model = request.Model is { Length: > 0 } model ? new ProvenanceModel { Id = model } : null,
            ReceiptId = request.ReceiptId,
        };
        var payload = JsonNode.Parse(JsonSerializer.Serialize(record, FadenstackJson.Options))!;
        var signature = Signer.Sign(payload);
        // The machine policy may name its own server, or switch time-stamps off (F09): then nothing leaves the device.
        var authority = AuthorityFor(agent.MachinePolicy.Timestamping);
        var timestamp = authority is null ? null : await TimestampAsync(signature, authority, cancellationToken).ConfigureAwait(false);
        var signed = new SignedRecord { Record = record, Signature = signature, Timestamp = timestamp };
        Keep(new KeptRecord
        {
            Signed = signed,
            TimeSource = timestamp is null ? DeviceClockTime : AuthorityTime,
            Queued = timestamp is null && authority is not null,
        });
        if (timestamp is not null)
        {
            // The authority answers: what waited while it did not is stamped now.
            await StampQueuedAsync(authority!, cancellationToken).ConfigureAwait(false);
        }
        return signed;
    }

    /// <summary>
    /// Stamps the records that wait in the queue, when the authority the policy names answers (an app may call it at start;
    /// signing a record does it too, once the authority answers). The number stamped.
    /// </summary>
    public Task<int> FlushTimestampQueueAsync(FadenstackAgent agent, CancellationToken cancellationToken = default)
    {
        UseLogger(agent);
        return AuthorityFor(agent.MachinePolicy.Timestamping) is { } authority
            ? StampQueuedAsync(authority, cancellationToken)
            : Task.FromResult(0);
    }

    /// <summary>The records kept on the device, oldest first: each with its token, or labelled with the device's clock.</summary>
    public IReadOnlyList<KeptRecord> KeptRecords() =>
        Directory.Exists(RecordsFolder)
            ? Directory.GetFiles(RecordsFolder, "*.json").Select(Read).Where(r => r is not null).Select(r => r!)
                .OrderBy(r => r.Signed.Record.CreatedAt).ToList()
            : new List<KeptRecord>();

    /// <summary>The authority for the policy: its own server, DigiCert (the app's) for <c>on</c>, none for <c>off</c>.</summary>
    internal Uri? AuthorityFor(TimestampingPolicy policy) =>
        !_options.Timestamping ? null
        : policy.Mode switch
        {
            TimestampingMode.Off => null,
            TimestampingMode.OwnServer => policy.Server,
            _ => _options.TimestampAuthority,
        };

    private async Task<int> StampQueuedAsync(Uri authority, CancellationToken cancellationToken)
    {
        if (!await _queueGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0; // another stamping round is on its way
        }
        try
        {
            var stamped = 0;
            foreach (var waiting in KeptRecords().Where(r => r.Queued).Take(Math.Max(1, _options.QueueBatch)))
            {
                var token = await TimestampAsync(waiting.Signed.Signature, authority, cancellationToken).ConfigureAwait(false);
                if (token is null)
                {
                    break; // the authority stopped answering: the rest waits on
                }
                Keep(waiting with
                {
                    Signed = waiting.Signed with { Timestamp = token },
                    Queued = false,
                    StampedLaterAt = Now,
                });
                stamped++;
            }
            return stamped;
        }
        finally
        {
            _queueGate.Release();
        }
    }

    /// <summary>The agent's logger, unless the app passed its own.</summary>
    private void UseLogger(FadenstackAgent agent)
    {
        if (_options.Logger is null)
        {
            _logger = Log.For(agent);
        }
    }

    /// <summary>
    /// A record's id as a file name: parsed as a GUID (format <c>D</c>) and written back, so nothing else -- <c>..</c>, a separator,
    /// a drive -- ever reaches a path. An id read back from disk is not trusted (ASVS 5.0 V5 file handling, V1 injection).
    /// </summary>
    private static bool TryFileName(string? id, out string fileName)
    {
        if (id is not null && Guid.TryParseExact(id, "D", out var guid))
        {
            fileName = guid.ToString("D") + ".json";
            return true;
        }
        fileName = "";
        return false;
    }

    private void Keep(KeptRecord kept)
    {
        if (!TryFileName(kept.Signed.Record.Id, out var fileName))
        {
            Log.RecordIdRejected(_logger);
            return;
        }
        Directory.CreateDirectory(RecordsFolder);
        var path = Path.Combine(RecordsFolder, fileName);
        SecretProtection.WriteOwnerOnly(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(kept, FadenstackJson.Options)));
    }

    private KeptRecord? Read(string path)
    {
        try
        {
            var kept = JsonSerializer.Deserialize<KeptRecord>(File.ReadAllText(path), FadenstackJson.Options);
            if (kept is not { Signed.Record.Id.Length: > 0 })
            {
                return null;
            }
            if (!TryFileName(kept.Signed.Record.Id, out _))
            {
                Log.RecordIdRejected(_logger); // an id that is no GUID is never turned into a path
                return null;
            }
            return kept;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a damaged entry costs only that entry
        }
    }

    /// <summary>Appends the report to the local log; nothing leaves the device.</summary>
    public Task ReportPlacementAsync(FadenstackAgent agent, PlacementReport report, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(report, FadenstackJson.Options) + "\n";
        lock (_logLock)
        {
            Directory.CreateDirectory(Folder);
            var log = Path.Combine(Folder, "placements.jsonl");
            if (File.Exists(log) && new FileInfo(log).Length + Encoding.UTF8.GetByteCount(line) > _options.MaxLogBytes)
            {
                Rotate(log, Math.Max(0, _options.KeepLogs));
            }
            File.AppendAllText(log, line, new UTF8Encoding(false));
        }
        return Task.CompletedTask;
    }

    /// <summary>placements.jsonl becomes .1, .1 becomes .2, ...; the oldest beyond <paramref name="keep"/> goes.</summary>
    private static void Rotate(string log, int keep)
    {
        if (keep == 0)
        {
            File.Delete(log);
            return;
        }
        var oldest = log + "." + keep;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var i = keep - 1; i >= 1; i--)
        {
            var from = log + "." + i;
            if (File.Exists(from))
            {
                File.Move(from, log + "." + (i + 1));
            }
        }
        File.Move(log, log + ".1");
    }

    /// <summary>
    /// An RFC 3161 token over the signature's bytes (as the engine's), base64 of the authority's whole answer; null when the
    /// authority does not answer, or answers with a token whose signature, certificate or nonce does not check out.
    /// </summary>
    private async Task<string?> TimestampAsync(string signature, Uri authority, CancellationToken cancellationToken)
    {
        var nonce = new byte[8];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(nonce);
        }
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x40); // positive, and never a leading zero
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(DeviceSigner.SignatureBytes(signature));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TimestampTimeout);
        try
        {
            using var content = new ByteArrayContent(Rfc3161.Request(hash, nonce));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            using var response = await _http.Value.PostAsync(authority, content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content is null)
            {
                return null;
            }
            var answer = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return Rfc3161.Grants(answer, hash, nonce, _options.TimestampRoots) ? Convert.ToBase64String(answer) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null; // offline or slow: the labelled clock, and the queue
        }
    }

    /// <summary>To the millisecond, so the record's time reads back as it was signed.</summary>
    private static DateTimeOffset Truncated(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    public void Dispose()
    {
        if (_http.IsValueCreated)
        {
            _http.Value.Dispose();
        }
        if (_signer.IsValueCreated && _options.Signer is null)
        {
            _signer.Value.Dispose();
        }
        _queueGate.Dispose();
    }
}

/// <summary>A record kept on the device with its token (sdk/51 F10, the local backup).</summary>
public sealed record KeptRecord
{
    /// <summary>The record, its signature and its token (null while it waits).</summary>
    public SignedRecord Signed { get; init; } = new();

    /// <summary><c>authority</c>: the token dates it; <c>device-clock</c>: the device's clock does, labelled as such.</summary>
    public string TimeSource { get; init; } = LocalProvenanceBackend.DeviceClockTime;

    /// <summary>Waiting for a token, because the authority could not be reached when it was signed.</summary>
    public bool Queued { get; init; }

    /// <summary>When a waiting record got its token: it then proves it existed no later than that.</summary>
    public DateTimeOffset? StampedLaterAt { get; init; }
}

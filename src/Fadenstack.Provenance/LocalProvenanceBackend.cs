using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>What the device's own provenance backend does beyond its defaults.</summary>
public sealed record LocalProvenanceOptions
{
    /// <summary>
    /// The RFC 3161 time-stamp authority a record's signature is sent to (only the signature's SHA-256 leaves the device);
    /// null: none, and the record's time is the device's clock.
    /// </summary>
    public Uri? TimestampAuthority { get; init; } = DefaultTimestampAuthority;

    /// <summary>The default authority: DigiCert's free service, which answers over http only (its tokens are signed).</summary>
    public static Uri DefaultTimestampAuthority { get; } = new("http://timestamp.digicert.com");

    /// <summary>Whether records are time-stamped at all; a machine policy can switch it off (F09).</summary>
    public bool Timestamping { get; init; } = true;

    /// <summary>How long the authority may take before the record goes without a token.</summary>
    public TimeSpan TimestampTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The handler the time-stamp request goes through (tests, a proxy); the SDK's own when null.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Who provides the AI system, as the record names it (a server names its organisation).</summary>
    public string Provider { get; init; } = "standalone";

    /// <summary>The signer; the installation's (a key per OS user, kept by the OS key store) when null.</summary>
    public DeviceSigner? Signer { get; init; }

    /// <summary>Where the placement log goes; the standalone user's <c>provenance</c> folder when null.</summary>
    public string? Folder { get; init; }

    /// <summary>The largest the placement log grows before it rotates.</summary>
    public long MaxLogBytes { get; init; } = 1024 * 1024;

    /// <summary>How many rotated logs are kept besides the current one.</summary>
    public int KeepLogs { get; init; } = 3;
}

/// <summary>
/// Provenance with no server (sdk/51 F10): the device's own policy defaults, records signed on the device in the server's
/// format (<see cref="DeviceSigner"/>) with an RFC 3161 time-stamp when one can be had, and placements in a local log that
/// rotates. Nothing leaves the device but the signature's hash to the time-stamp authority, and that only when allowed.
/// </summary>
/// <remarks>
/// The record's <c>content_hash</c> is the plain SHA-256 of the content (<c>hash_key_version</c> 0): a server's is a keyed
/// HMAC under its own key version, which a device does not have. Without a token, the signed <c>created_at</c> is the
/// device's clock, as on a server without an authority.
/// </remarks>
public sealed class LocalProvenanceBackend : IProvenanceBackend, IDisposable
{
    /// <summary>The <c>hash_key_version</c> of a device's record: the content's plain SHA-256.</summary>
    public const int PlainHashKeyVersion = 0;

    private const string SourceTypes = "http://cv.iptc.org/newscodes/digitalsourcetype/";

    private readonly LocalProvenanceOptions _options;
    private readonly Lazy<DeviceSigner> _signer;
    private readonly Lazy<HttpClient> _http;
    private readonly object _logLock = new();

    public LocalProvenanceBackend(LocalProvenanceOptions? options = null)
    {
        _options = options ?? new LocalProvenanceOptions();
        _signer = new Lazy<DeviceSigner>(() => _options.Signer ?? DeviceSigner.ForInstallation(
            Path.Combine(FadenstackPaths.User(LocalAgentBackendId, "keys"), "provenance-signing.key"), SecretProtection.Default));
        _http = new Lazy<HttpClient>(() => _options.Handler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
    }

    /// <summary>The standalone principal's folder name (the agent's own, <c>users/standalone/</c>).</summary>
    private const string LocalAgentBackendId = "standalone";

    public LocalProvenanceOptions Options => _options;

    /// <summary>The device's signer (made, or read from the OS key store, on first use).</summary>
    public DeviceSigner Signer => _signer.Value;

    /// <summary>The local defaults: mark everything but standard editing (the default exempt kinds), record what is missing.</summary>
    public Task<ProvenancePolicy> GetPolicyAsync(FadenstackAgent agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ProvenancePolicy());

    public async Task<SignedRecord> SignRecordAsync(FadenstackAgent agent, RecordRequest request, CancellationToken cancellationToken)
    {
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
            CreatedAt = TruncatedNow(),
            MediaType = request.MediaType,
            ContentHash = request.ContentSha256,
            HashKeyVersion = PlainHashKeyVersion,
            TextHash = request.TextSha256,
            Model = request.Model is { Length: > 0 } model ? new ProvenanceModel { Id = model } : null,
            ReceiptId = request.ReceiptId,
        };
        var payload = JsonNode.Parse(JsonSerializer.Serialize(record, FadenstackJson.Options))!;
        var signature = Signer.Sign(payload);
        // The machine policy may switch time-stamps off (F09): then nothing leaves the device at all.
        var timestamp = agent.MachinePolicy.Timestamping == PolicyAccess.Allowed
            ? await TimestampAsync(signature, cancellationToken).ConfigureAwait(false)
            : null;
        return new SignedRecord { Record = record, Signature = signature, Timestamp = timestamp };
    }

    /// <summary>Appends the report to the local log; nothing leaves the device.</summary>
    public Task ReportPlacementAsync(FadenstackAgent agent, PlacementReport report, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(report, FadenstackJson.Options) + "\n";
        var folder = _options.Folder ?? FadenstackPaths.User(LocalAgentBackendId, "provenance");
        lock (_logLock)
        {
            Directory.CreateDirectory(folder);
            var log = Path.Combine(folder, "placements.jsonl");
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
    /// An RFC 3161 token over the signature's bytes (as the engine's), base64 of the authority's whole answer; null offline,
    /// when switched off, or when the authority does not answer usably.
    /// </summary>
    private async Task<string?> TimestampAsync(string signature, CancellationToken cancellationToken)
    {
        if (!_options.Timestamping || _options.TimestampAuthority is not { } authority)
        {
            return null;
        }
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
            // The token must be granted over our hash with our nonce; anything else is no token.
            return Rfc3161.Grants(answer, hash, nonce) ? Convert.ToBase64String(answer) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or CryptographicException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null; // offline, slow or a bad answer: the device's clock, as without an authority
        }
    }

    /// <summary>Now, to the millisecond, so the record's time reads back as it was signed.</summary>
    private static DateTimeOffset TruncatedNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }

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
    }
}

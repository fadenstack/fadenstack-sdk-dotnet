using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Agents.Audit;

/// <summary>
/// The person's own tamper-evident record of what the agent's tools did on this device (sdk/51 8.10): one entry per tool call
/// decision and outcome, content-free, every app of the person writing into the same log. Hash-chained and sealed, so a changed,
/// deleted, swapped or forged entry is found by <see cref="VerifyAsync"/>. For the person, and for the defence file (EU product
/// liability, the AI Act's logging duty). OWASP ASVS 5.0 V16 (security logging: integrity, protection, retention). TS: <c>AuditLog</c>.
/// </summary>
public interface IAuditLog
{
    /// <summary>Adds an entry after the last one. Never forks the chain, whatever other apps write at the same time.</summary>
    Task<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>Checks the whole chain; see <see cref="AuditVerification"/>.</summary>
    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes every entry readable, with the verification result at the top: a CSV, or one self-contained HTML page (a table, escaped
    /// text, inline CSS, no script, no outside resource). The destination stays open.
    /// </summary>
    Task ExportAsync(AuditExportFormat format, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entries older than <paramref name="retentionDays"/> days from the start of the log and says how many went. The last
    /// removed entry's seq and hash stay in the sealed anchor, so what is left still verifies.
    /// </summary>
    Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default);
}

/// <summary>How long entries are kept: 180 days unless the machine policy's <c>auditRetentionDays</c> says otherwise (30 to 3650).</summary>
public static class AuditRetention
{
    /// <summary>The default.</summary>
    public const int DefaultDays = 180;

    /// <summary>The shortest the policy may set.</summary>
    public const int MinDays = 30;

    /// <summary>The longest the policy may set.</summary>
    public const int MaxDays = 3650;

    /// <summary>Whether <paramref name="days"/> is within what the policy may set.</summary>
    public static bool IsValid(int days) => days >= MinDays && days <= MaxDays;
}

/// <summary>The log kept in memory only: gone with the process (tests, a host that keeps nothing). Same chain, same checks.</summary>
public sealed class MemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = new();
    private readonly byte[] _key = RandomKey();
    private readonly Func<DateTimeOffset> _clock;
    private AuditAnchor? _anchor;
    private AuditHead? _head;

    /// <param name="clock">The time of an entry that names none; now by default.</param>
    public MemoryAuditLog(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The entries now, for a test.</summary>
    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public Task<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var last = _entries.Count > 0 ? _entries[_entries.Count - 1] : null;
            (long, string)? prev = last is not null ? (last.Seq, last.Hash) : _anchor is { } a ? (a.Seq, a.Hash) : null;
            var entry = AuditChain.Next(record, prev, _key, _clock());
            _entries.Add(entry);
            _head = new AuditHead(entry.Seq, entry.Hash, AuditChain.HeadSeal(_key, entry.Seq, entry.Hash));
            return Task.FromResult(entry);
        }
    }

    public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(AuditChain.Verify(_entries.Cast<AuditEntry?>().ToList(), _key, _anchor, _head));
        }
    }

    public async Task ExportAsync(AuditExportFormat format, Stream destination, CancellationToken cancellationToken = default)
    {
        AuditVerification verification;
        List<AuditEntry> entries;
        lock (_gate)
        {
            entries = _entries.ToList();
            verification = AuditChain.Verify(entries.Cast<AuditEntry?>().ToList(), _key, _anchor, _head);
        }
        await AuditExport.WriteAsync(format, destination, verification, entries, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var cutoff = _clock() - TimeSpan.FromDays(retentionDays);
            var gone = _entries.TakeWhile(e => e.Time < cutoff).Count();
            if (gone > 0)
            {
                var last = _entries[gone - 1];
                _anchor = new AuditAnchor(last.Seq, last.Hash, _clock(), AuditChain.AnchorSeal(_key, last.Seq, last.Hash));
                _entries.RemoveRange(0, gone);
            }
            return Task.FromResult(gone);
        }
    }

    internal static byte[] RandomKey()
    {
        var key = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(key);
        return key;
    }
}

/// <summary>
/// The log on disk, in the person's own data folder (<c>users/&lt;user&gt;/audit/</c> under <see cref="FadenstackPaths.DataRoot"/>; the data
/// root's <c>audit/</c> when the server names no user): <c>audit.jsonl</c>, one entry per line, each line the entry's JSON encrypted with
/// the same <see cref="ISecretProtector"/> as the approvals (DPAPI on Windows; owner-only files elsewhere) and written in base64;
/// <c>audit-seal.key</c> (32 random bytes, protected, made at the first write); <c>audit-anchor.json</c> (sealed; what a prune leaves);
/// <c>audit-head.json</c> (sealed; the last entry's number and hash, written with every append, so that a cut-off end is found).
/// <para>
/// Other apps of the person (Word and Excel add-ins, the desktop) append to the same file: an exclusive lock on <c>audit.lock</c> is held while
/// the last entry is read and the next appended, so the chain never forks. Writing never breaks a turn: the agent logs a failed
/// append (the kind of failure only), the call goes on, and the next verify reports the gap.
/// </para>
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    /// <summary>The log's file.</summary>
    public const string FileName = "audit.jsonl";

    /// <summary>The seal key's file.</summary>
    public const string SealKeyName = "audit-seal.key";

    /// <summary>The anchor's file.</summary>
    public const string AnchorName = "audit-anchor.json";

    /// <summary>The sealed head's file: the last entry's number and hash.</summary>
    public const string HeadName = "audit-head.json";

    /// <summary>The lock file the writers of all apps take.</summary>
    public const string LockName = "audit.lock";

    private const int LockAttempts = 200;

    private readonly string _root;
    private readonly string _file;
    private readonly string _lockFile;
    private readonly string _keyFile;
    private readonly string _anchorFile;
    private readonly string _headFile;
    private readonly ISecretProtector _protector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">The folder; the data root's <c>audit</c> by default.</param>
    /// <param name="protector">How the lines and the key are encrypted; <see cref="SecretProtection.Default"/> by default.</param>
    /// <param name="clock">The time of an entry that names none; now by default.</param>
    /// <param name="logger">Where a line that cannot be read is logged (the kind only); nowhere if not given.</param>
    public FileAuditLog(string? directory = null, ISecretProtector? protector = null, Func<DateTimeOffset>? clock = null, ILogger? logger = null)
    {
        _root = directory ?? FadenstackPaths.Data("audit");
        _file = Path.Combine(_root, FileName);
        _lockFile = Path.Combine(_root, LockName);
        _keyFile = Path.Combine(_root, SealKeyName);
        _anchorFile = Path.Combine(_root, AnchorName);
        _headFile = Path.Combine(_root, HeadName);
        _protector = protector ?? SecretProtection.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ = logger ?? NullLogger.Instance;
    }

    public async Task<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var held = await LockAsync(create: true, cancellationToken).ConfigureAwait(false);
            var key = SealKey(create: true) ?? throw new IOException("The audit log's seal key cannot be read.");
            var last = LastKnown();
            var entry = AuditChain.Next(record, last, key, _clock());
            var line = Encoding.ASCII.GetBytes(Convert.ToBase64String(_protector.Protect(AuditChain.ToJson(entry))) + "\n");
            if (!File.Exists(_file))
            {
                SecretProtection.WriteOwnerOnly(_file, Array.Empty<byte>());
            }
            using (var stream = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var length = stream.Seek(0, SeekOrigin.End);
                var partial = length > 0 && LastByte(stream, length) != (byte)'\n';
                var bytes = partial ? new[] { (byte)'\n' }.Concat(line).ToArray() : line;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            WriteHead(key, entry); // in the same lock: a log from before the head takes its head with its first append here
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var held = await LockAsync(create: false, cancellationToken).ConfigureAwait(false);
            return AuditChain.Verify(ReadAll(), SealKey(create: false), ReadAnchor(), ReadHead());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(AuditExportFormat format, Stream destination, CancellationToken cancellationToken = default)
    {
        AuditVerification verification;
        List<AuditEntry> entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var held = await LockAsync(create: false, cancellationToken).ConfigureAwait(false);
            var all = ReadAll();
            var key = SealKey(create: false);
            verification = AuditChain.Verify(all, key, ReadAnchor(), ReadHead());
            entries = all.Where(e => e is not null).Select(e => e!).ToList();
        }
        finally
        {
            _gate.Release();
        }
        await AuditExport.WriteAsync(format, destination, verification, entries, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var held = await LockAsync(create: false, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(_file) || SealKey(create: false) is not { } key)
            {
                return 0;
            }
            var cutoff = _clock() - TimeSpan.FromDays(retentionDays);
            var lines = ReadLines();
            var gone = 0;
            AuditEntry? lastGone = null;
            foreach (var line in lines)
            {
                var entry = Decode(line);
                if (entry is null || entry.Time >= cutoff)
                {
                    break; // a damaged line is kept for verify to name; so is everything after the first entry that is not old
                }
                lastGone = entry;
                gone++;
            }
            if (lastGone is null)
            {
                return 0;
            }
            // The anchor first: if the rewrite does not finish, the old file still verifies (the anchor names an entry it holds).
            WriteAnchor(key, lastGone, _clock());
            var kept = lines.Skip(gone).Select(l => l + "\n");
            SecretProtection.WriteOwnerOnly(_file, Encoding.ASCII.GetBytes(string.Concat(kept)));
            return gone;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- the lock ----

    /// <summary>Takes the exclusive lock the writers of every app share, trying again for a few seconds while another holds it.</summary>
    private async Task<IDisposable> LockAsync(bool create, CancellationToken cancellationToken)
    {
        if (!create && !Directory.Exists(_root))
        {
            return NoLock.Instance;
        }
        Directory.CreateDirectory(_root);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                await Task.Delay(10 + attempt % 7 * 5, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A lock on nothing, for a read of a log that does not exist yet.</summary>
    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }

    // ---- reading ----

    /// <summary>The last entry's seq and hash: the file's last readable line, else the anchor, else none (the first entry comes next).</summary>
    private (long Seq, string Hash)? LastKnown()
    {
        if (File.Exists(_file))
        {
            if (TailLine() is { } tail && Decode(tail) is { } entry)
            {
                return (entry.Seq, entry.Hash);
            }
            var readable = ReadLines().Select(Decode).LastOrDefault(e => e is not null);
            if (readable is not null)
            {
                return (readable.Seq, readable.Hash);
            }
        }
        return ReadAnchor() is { } anchor ? (anchor.Seq, anchor.Hash) : null;
    }

    private string? TailLine()
    {
        using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var end = stream.Length;
        while (end > 0 && ByteAt(stream, end - 1) is (byte)'\n' or (byte)'\r')
        {
            end--;
        }
        if (end == 0)
        {
            return null;
        }
        var start = end;
        while (start > 0 && ByteAt(stream, start - 1) != (byte)'\n')
        {
            start--;
            if (end - start > 1 << 20)
            {
                return null; // not a line of ours
            }
        }
        var bytes = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        var read = 0;
        while (read < bytes.Length)
        {
            var n = stream.Read(bytes, read, bytes.Length - read);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return Encoding.ASCII.GetString(bytes, 0, read);
    }

    private static byte ByteAt(FileStream stream, long position)
    {
        stream.Seek(position, SeekOrigin.Begin);
        return (byte)stream.ReadByte();
    }

    private static byte LastByte(FileStream stream, long length) => ByteAt(stream, length - 1);

    private List<string> ReadLines()
    {
        if (!File.Exists(_file))
        {
            return new List<string>();
        }
        return Encoding.ASCII.GetString(SecretProtection.ReadShared(_file)).Split('\n')
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    private List<AuditEntry?> ReadAll() => ReadLines().Select(Decode).ToList();

    /// <summary>A line as an entry; null when it cannot be decrypted or parsed.</summary>
    private AuditEntry? Decode(string line)
    {
        try
        {
            return AuditChain.FromJson(_protector.Unprotect(Convert.FromBase64String(line)));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // ---- the key and the anchor ----

    private byte[]? SealKey(bool create)
    {
        try
        {
            if (File.Exists(_keyFile))
            {
                return _protector.Unprotect(SecretProtection.ReadShared(_keyFile)) is { Length: 32 } kept ? kept : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
        if (!create)
        {
            return null;
        }
        var key = MemoryAuditLog.RandomKey();
        Directory.CreateDirectory(_root);
        SecretProtection.WriteOwnerOnly(_keyFile, _protector.Protect(key));
        return key;
    }

    private AuditAnchor? ReadAnchor()
    {
        try
        {
            if (!File.Exists(_anchorFile))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(SecretProtection.ReadShared(_anchorFile));
            var root = doc.RootElement;
            return new AuditAnchor(root.GetProperty("seq").GetInt64(), root.GetProperty("hash").GetString() ?? "",
                DateTimeOffset.Parse(root.GetProperty("pruned_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal), root.GetProperty("seal").GetString() ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return new AuditAnchor(0, "", DateTimeOffset.MinValue, ""); // present but unreadable: never verifies
        }
    }

    private AuditHead? ReadHead()
    {
        try
        {
            if (!File.Exists(_headFile))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(SecretProtection.ReadShared(_headFile));
            var root = doc.RootElement;
            return new AuditHead(root.GetProperty("seq").GetInt64(), root.GetProperty("hash").GetString() ?? "", root.GetProperty("seal").GetString() ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return new AuditHead(0, "", ""); // present but unreadable: never verifies
        }
    }

    private void WriteHead(byte[] key, AuditEntry last)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", last.Seq);
            w.WriteString("hash", last.Hash);
            w.WriteString("seal", AuditChain.HeadSeal(key, last.Seq, last.Hash));
            w.WriteEndObject();
        }
        SecretProtection.WriteOwnerOnly(_headFile, stream.ToArray());
    }

    private void WriteAnchor(byte[] key, AuditEntry last, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", last.Seq);
            w.WriteString("hash", last.Hash);
            w.WriteString("pruned_at", AuditChain.Iso(now));
            w.WriteString("seal", AuditChain.AnchorSeal(key, last.Seq, last.Hash));
            w.WriteEndObject();
        }
        SecretProtection.WriteOwnerOnly(_anchorFile, stream.ToArray());
    }
}

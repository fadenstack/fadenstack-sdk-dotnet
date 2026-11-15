using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Auth;
using Fadenstack.Agents;
using Fadenstack.Storage;
using Fadenstack.Tools;

namespace Fadenstack.Mcp;

/// <summary>
/// The person's MCP servers on this computer (desktop-agent/31 §4.4; sdk/51 F05, D14), kept in the signed-in Fadenstack user's own folder of the
/// OS user's app-data folder and encrypted with the SDK's <see cref="ISecretProtector"/> (DPAPI on Windows, the OS key store
/// elsewhere): environment values and headers often hold tokens, so the list never goes into <c>preferences.json</c>. Where no key
/// store encrypts (<see cref="ISecretProtector.Encrypts"/> false) the list is kept in memory only, as the SDK does for
/// transcripts, and <see cref="KeepsOnDisk"/> says so for the page to tell the person. A file that cannot be read (another key,
/// damaged) leaves an empty list and <see cref="Unreadable"/> set; it is moved aside, not overwritten, by the next save.
/// <para>
/// The file's format is the desktop's (version 1, <c>servers.dat</c> in <c>users/&lt;id&gt;/mcp</c>); trust marks and the person's
/// per-tool raises (H4) are two optional keys of an entry, written only where set, so a file without them is the desktop's file
/// as it always was and an older reader that ignores unknown keys still reads one with them. Standalone, the list is the
/// <c>standalone</c> principal's (<see cref="ForStandalone"/>): one list for each OS user, which the desktop and Office share.
/// </para>
/// <para>
/// Several apps keep one list (Word and Excel, Office and the desktop): every change reads the file again first, under a lock on
/// <c>servers.dat.lock</c> beside it that other processes respect, and is made to the list as it is then, so no app's change is
/// lost to another's save. <see cref="Reload"/> reads a list another app changed (the <see cref="McpHost"/> calls it when the file
/// changes).
/// </para>
/// </summary>
public sealed class McpServerStore
{
    /// <summary>The file in the user's <c>mcp</c> folder.</summary>
    public const string FileName = "servers.dat";

    /// <summary>The empty file beside it whose lock keeps two apps' changes apart; it never holds data.</summary>
    public const string LockFileName = FileName + ".lock";

    /// <summary>How long a change waits for another app's change to finish; after that it goes ahead without the lock.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly ISecretProtector _protector;
    private List<McpServerEntry> _entries = [];

    /// <summary>The SHA-256 of the file as this store last read or wrote it; null while it has none.</summary>
    private byte[]? _seen;

    /// <param name="folder">The folder of the file; created when something is saved.</param>
    /// <param name="protector">What encrypts the file; the SDK's default if not given.</param>
    public McpServerStore(string folder, ISecretProtector? protector = null)
    {
        _file = Path.Combine(folder, FileName);
        _protector = protector ?? SecretProtection.Default;
        Load();
    }

    /// <summary>The store of one Fadenstack user: <c>users/&lt;id&gt;/mcp</c> in the OS user's data folder.</summary>
    public static McpServerStore ForUser(string userId, ISecretProtector? protector = null) =>
        new(FadenstackPaths.User(userId, "mcp"), protector);

    /// <summary>The store of the standalone principal (no Fadenstack server): <c>users/standalone/mcp</c> in the OS user's data folder.</summary>
    public static McpServerStore ForStandalone(ISecretProtector? protector = null) => ForUser(LocalAgentBackend.StandaloneId, protector);

    /// <summary>A store that never writes (a server that names no user): the list lives until the app closes.</summary>
    public static McpServerStore InMemory() => new(Path.GetTempPath(), new OwnerOnlyFileProtector());

    /// <summary>True when the list is written to disk (encrypted); false when it lives in memory until the app closes.</summary>
    public bool KeepsOnDisk => _protector.Encrypts;

    /// <summary>The folder of the device's audit log of this list's switches (<see cref="McpAuditLog"/>); null: the log lives in memory too.</summary>
    public string? AuditFolder => KeepsOnDisk ? Path.GetDirectoryName(_file) : null;

    /// <summary>A saved file could not be read: the list starts empty, and the file is kept aside at the next save.</summary>
    public bool Unreadable { get; private set; }

    /// <summary>The file, when the list is kept on disk (for the host to follow).</summary>
    internal string? FileOnDisk => KeepsOnDisk ? _file : null;

    /// <summary>The list changed: by this store, or read again after another app changed it. From any thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<McpServerEntry> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    public McpServerEntry? Find(string label)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Label == label);
    }

    /// <summary>Adds a server; the problem when it is not valid or its label is taken.</summary>
    public McpValidation Add(McpServerEntry entry) => Change(() =>
    {
        var check = McpServerValidator.Validate(entry, _entries.Select(e => e.Label));
        if (!check.IsValid)
            return (check, false);
        _entries.Add(entry);
        return (McpValidation.Ok, true);
    });

    /// <summary>Replaces the server <paramref name="label"/> by <paramref name="entry"/> (which may rename it), in the same place of the list.</summary>
    public McpValidation Update(string label, McpServerEntry entry) => Change(() =>
    {
        var at = _entries.FindIndex(e => e.Label == label);
        if (at < 0)
            return (new McpValidation(McpProblem.LabelMissing, "label"), false);
        var check = McpServerValidator.Validate(entry, _entries.Where((_, i) => i != at).Select(e => e.Label));
        if (!check.IsValid)
            return (check, false);
        _entries[at] = entry;
        return (McpValidation.Ok, true);
    });

    public bool Remove(string label) => Change(() =>
    {
        var removed = _entries.RemoveAll(e => e.Label == label) > 0;
        return (removed, removed);
    });

    public bool SetEnabled(string label, bool enabled) => Change(() =>
    {
        var at = _entries.FindIndex(e => e.Label == label);
        if (at < 0)
            return (false, false);
        if (_entries[at].Enabled == enabled)
            return (true, false);
        _entries[at] = _entries[at] with { Enabled = enabled };
        return (true, true);
    });

    /// <summary>Marks the server trusted or not (H4): only a trusted server's read-only hints count. False when there is no such server.</summary>
    public bool SetTrusted(string label, bool trusted) => Change(() =>
    {
        var at = _entries.FindIndex(e => e.Label == label);
        if (at < 0)
            return (false, false);
        if (_entries[at].Trusted == trusted)
            return (true, false);
        _entries[at] = _entries[at] with { Trusted = trusted };
        return (true, true);
    });

    /// <summary>
    /// The person's raise of one tool of a server (by the server's own tool name) to <see cref="ToolClass.Read"/> or
    /// <see cref="ToolClass.Write"/>; null takes it back. False when there is no such server.
    /// </summary>
    /// <exception cref="ArgumentException">A class other than Read, Write or null: a person can only raise a tool to those.</exception>
    public bool SetToolClass(string label, string tool, ToolClass? toolClass)
    {
        if (string.IsNullOrEmpty(tool))
        {
            throw new ArgumentException("A tool name is required.", nameof(tool));
        }
        if (toolClass is not (null or ToolClass.Read or ToolClass.Write))
        {
            throw new ArgumentException("A tool can be raised to read or write, nothing else.", nameof(toolClass));
        }
        return Change(() =>
        {
            var at = _entries.FindIndex(e => e.Label == label);
            if (at < 0)
                return (false, false);
            var raised = _entries[at].ToolClasses.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (toolClass is { } value)
                raised[tool] = value;
            else
                raised.Remove(tool);
            _entries[at] = _entries[at] with { ToolClasses = raised };
            return (true, true);
        });
    }

    /// <summary>
    /// Reads the list again when another app changed the file since this store last read or wrote it (Word and Excel, Office and
    /// the desktop keep one list). True, and <see cref="Changed"/>, when the list is different now. A file that cannot be read for
    /// the moment leaves the list as it is.
    /// </summary>
    public bool Reload()
    {
        if (!KeepsOnDisk || !Directory.Exists(Path.GetDirectoryName(_file)))
            return false;
        bool changed;
        lock (_gate)
        {
            using (Hold())
                changed = Refresh();
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// One change of the list: under the gate and the file's lock, the list is read again first (another app may have changed it),
    /// then <paramref name="change"/> runs on it and says its result and whether to write.
    /// </summary>
    private T Change<T>(Func<(T Result, bool Write)> change)
    {
        bool reloaded;
        (T Result, bool Write) outcome;
        lock (_gate)
        {
            using (Hold())
            {
                reloaded = KeepsOnDisk && Refresh();
                outcome = change();
                if (outcome.Write)
                    Save();
            }
        }
        if (reloaded || outcome.Write)
            Changed?.Invoke(this, EventArgs.Empty);
        return outcome.Result;
    }

    /// <summary>
    /// The lock on <see cref="LockFileName"/> that keeps two apps' changes apart (an exclusive open: Windows refuses a second, .NET
    /// takes an advisory <c>flock</c> elsewhere); null in memory, or when it cannot be had in <see cref="LockWait"/>: the change
    /// goes ahead then, as it did before there was a lock.
    /// </summary>
    private FileStream? Hold()
    {
        if (!KeepsOnDisk)
            return null;
        var path = Path.Combine(Path.GetDirectoryName(_file)!, LockFileName);
        var until = DateTime.UtcNow + LockWait;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                return OpenLock(path);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(Math.Min(5 * attempt, 50));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static FileStream OpenLock(string path)
    {
#if NET
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
#endif
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    // Under the gate and the lock: reads the file when it is not the one this store last read or wrote. True when the list differs.
    private bool Refresh()
    {
        byte[] bytes;
        try
        {
            bytes = SecretProtection.ReadShared(_file);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Gone: another app deleted the person's data. A list this store never had on disk stays (a save that failed).
            if (_seen is null)
                return false;
            _seen = null;
            var had = _entries.Count > 0;
            _entries = [];
            return had;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // held for the moment; the next change or reload reads it
        }
        var hash = Hash(bytes);
        if (_seen is not null && hash.AsSpan().SequenceEqual(_seen))
            return false;
        List<McpServerEntry> fresh;
        try
        {
            fresh = Parse(_protector.Unprotect(bytes));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidOperationException)
        {
            // Another key, or damaged: this store's list stays, and the next save keeps the file aside, as at the start.
            _seen = hash;
            Unreadable = true;
            return false;
        }
        _seen = hash;
        Unreadable = false;
        if (Canonical(fresh) == Canonical(_entries))
            return false;
        _entries = fresh;
        return true;
    }

    private static byte[] Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    private static List<McpServerEntry> Parse(byte[] json)
    {
        var file = JsonSerializer.Deserialize<FileDto>(json, Json);
        return [.. (file?.Servers ?? []).Select(s => s.ToEntry()).Where(e => McpServerValidator.IsLabel(e.Label))];
    }

    private static string Canonical(IEnumerable<McpServerEntry> entries) =>
        JsonSerializer.Serialize(new FileDto { Servers = [.. entries.Select(EntryDto.From)] }, Json);

    /// <summary>The entry as the file keeps it: two entries with the same text are the same server, set the same way.</summary>
    internal static string Canonical(McpServerEntry entry) => JsonSerializer.Serialize(EntryDto.From(entry), Json);

    // Under the gate. A file that cannot be written is not fatal: the list stays in memory for this run.
    private void Save()
    {
        if (!KeepsOnDisk)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            if (Unreadable && File.Exists(_file))
            {
                var aside = _file + ".unreadable";
                if (File.Exists(aside))
                {
                    File.Delete(aside);
                }
                File.Move(_file, aside);
                Unreadable = false;
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(new FileDto { Servers = [.. _entries.Select(EntryDto.From)] }, Json);
            var bytes = _protector.Protect(json);
            SecretProtection.WriteOwnerOnly(_file, bytes);
            _seen = Hash(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // kept in memory; the next save tries again
        }
    }

    private void Load()
    {
        if (!KeepsOnDisk || !File.Exists(_file))
            return;
        try
        {
            var bytes = SecretProtection.ReadShared(_file);
            _seen = Hash(bytes);
            _entries = Parse(_protector.Unprotect(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException)
        {
            Unreadable = true;
        }
    }

    // The file's keys are pinned with their names, here and in the types below: the release build is obfuscated
    // (desktop-agent/27), which renames these members, and the person's servers must load in every build.
    private sealed class FileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("servers")]
        public List<EntryDto> Servers { get; set; } = [];
    }

    private sealed class PairDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("value")]
        public string Value { get; set; } = "";
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("arguments")]
        public List<string>? Arguments { get; set; }

        [JsonPropertyName("workingFolder")]
        public string? WorkingFolder { get; set; }

        [JsonPropertyName("environment")]
        public List<PairDto>? Environment { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("headers")]
        public List<PairDto>? Headers { get; set; }

        [JsonPropertyName("callTimeoutSeconds")]
        public int CallTimeoutSeconds { get; set; } = 120;

        [JsonPropertyName("origin")]
        public string Origin { get; set; } = McpServerEntry.TypedOrigin;

        // Optional and written only where set: the desktop's file has neither (H4).
        [JsonPropertyName("trusted")]
        public bool? Trusted { get; set; }

        [JsonPropertyName("toolClasses")]
        public Dictionary<string, string>? ToolClasses { get; set; }

        // Written only when on (sdk/51 F05), so a file without it is byte for byte the desktop's.
        [JsonPropertyName("plainHttpInLan")]
        public bool? PlainHttpInLan { get; set; }

        public static EntryDto From(McpServerEntry e) => new()
        {
            Label = e.Label,
            Enabled = e.Enabled,
            Command = e.Command,
            Arguments = [.. e.Arguments],
            WorkingFolder = e.WorkingFolder,
            Environment = [.. e.Environment.Select(p => new PairDto { Name = p.Name, Value = p.Value })],
            Url = e.Url,
            Headers = [.. e.Headers.Select(p => new PairDto { Name = p.Name, Value = p.Value })],
            CallTimeoutSeconds = e.CallTimeoutSeconds,
            Origin = e.Origin,
            Trusted = e.Trusted ? true : null,
            PlainHttpInLan = e.PlainHttpInLan ? true : null,
            ToolClasses = e.ToolClasses.Count == 0 ? null : e.ToolClasses
                .Where(p => p.Value is ToolClass.Read or ToolClass.Write)
                .ToDictionary(p => p.Key, p => p.Value == ToolClass.Read ? "read" : "write", StringComparer.Ordinal),
        };

        public McpServerEntry ToEntry() => new()
        {
            Label = Label,
            Enabled = Enabled,
            Command = Command,
            Arguments = Arguments ?? [],
            WorkingFolder = WorkingFolder,
            Environment = [.. (Environment ?? []).Select(p => new McpSecretPair(p.Name, p.Value))],
            Url = Url,
            Headers = [.. (Headers ?? []).Select(p => new McpSecretPair(p.Name, p.Value))],
            CallTimeoutSeconds = CallTimeoutSeconds,
            Origin = string.IsNullOrEmpty(Origin) ? McpServerEntry.TypedOrigin : Origin,
            Trusted = Trusted == true,
            PlainHttpInLan = PlainHttpInLan == true,
            ToolClasses = RaisesOf(ToolClasses),
        };

        // Only "read" and "write" are kept; anything else in the file (a hand edit, a later version) leaves the tool as it arrives.
        private static IReadOnlyDictionary<string, ToolClass> RaisesOf(Dictionary<string, string>? raises)
        {
            var result = new Dictionary<string, ToolClass>(StringComparer.Ordinal);
            foreach (var pair in raises ?? new Dictionary<string, string>())
            {
                if (string.Equals(pair.Value, "read", StringComparison.OrdinalIgnoreCase))
                {
                    result[pair.Key] = ToolClass.Read;
                }
                else if (string.Equals(pair.Value, "write", StringComparison.OrdinalIgnoreCase))
                {
                    result[pair.Key] = ToolClass.Write;
                }
            }
            return result;
        }
    }
}

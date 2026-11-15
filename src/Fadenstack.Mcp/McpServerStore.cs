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
/// </summary>
public sealed class McpServerStore
{
    /// <summary>The file in the user's <c>mcp</c> folder.</summary>
    public const string FileName = "servers.dat";

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

    /// <summary>A saved file could not be read: the list starts empty, and the file is kept aside at the next save.</summary>
    public bool Unreadable { get; private set; }

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
    public McpValidation Add(McpServerEntry entry)
    {
        lock (_gate)
        {
            var check = McpServerValidator.Validate(entry, _entries.Select(e => e.Label));
            if (!check.IsValid)
                return check;
            _entries.Add(entry);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return McpValidation.Ok;
    }

    /// <summary>Replaces the server <paramref name="label"/> by <paramref name="entry"/> (which may rename it), in the same place of the list.</summary>
    public McpValidation Update(string label, McpServerEntry entry)
    {
        lock (_gate)
        {
            var at = _entries.FindIndex(e => e.Label == label);
            if (at < 0)
                return new(McpProblem.LabelMissing, "label");
            var check = McpServerValidator.Validate(entry, _entries.Where((_, i) => i != at).Select(e => e.Label));
            if (!check.IsValid)
                return check;
            _entries[at] = entry;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return McpValidation.Ok;
    }

    public bool Remove(string label)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.Label == label) == 0)
                return false;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SetEnabled(string label, bool enabled)
    {
        lock (_gate)
        {
            var at = _entries.FindIndex(e => e.Label == label);
            if (at < 0)
                return false;
            if (_entries[at].Enabled == enabled)
                return true;
            _entries[at] = _entries[at] with { Enabled = enabled };
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Marks the server trusted or not (H4): only a trusted server's read-only hints count. False when there is no such server.</summary>
    public bool SetTrusted(string label, bool trusted)
    {
        lock (_gate)
        {
            var at = _entries.FindIndex(e => e.Label == label);
            if (at < 0)
            {
                return false;
            }
            if (_entries[at].Trusted == trusted)
            {
                return true;
            }
            _entries[at] = _entries[at] with { Trusted = trusted };
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

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
        lock (_gate)
        {
            var at = _entries.FindIndex(e => e.Label == label);
            if (at < 0)
            {
                return false;
            }
            var raised = _entries[at].ToolClasses.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (toolClass is { } value)
            {
                raised[tool] = value;
            }
            else
            {
                raised.Remove(tool);
            }
            _entries[at] = _entries[at] with { ToolClasses = raised };
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

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
            SecretProtection.WriteOwnerOnly(_file, _protector.Protect(json));
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
            var json = _protector.Unprotect(File.ReadAllBytes(_file));
            var file = JsonSerializer.Deserialize<FileDto>(json, Json);
            _entries = [.. (file?.Servers ?? []).Select(s => s.ToEntry()).Where(e => McpServerValidator.IsLabel(e.Label))];
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

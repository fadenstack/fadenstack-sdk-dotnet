using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fadenstack.Mcp;

/// <summary>The programs the person can import MCP servers from (desktop-agent/31 §4.4).</summary>
public enum McpImportSource
{
    ClaudeDesktop,
    VsCode,
    Cursor,

    /// <summary>A <c>.mcp.json</c> (or any file of that shape) the person picks.</summary>
    ProjectFile,

    // The scan's further apps (desktop-agent/32 §3); their files are read by the scanner and by an import of a picked file.
    Windsurf,
    ClaudeCode,
    GeminiCli,
    LmStudio,
}

public enum McpOs
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>The operating system the code runs on, as <see cref="McpOs"/> (netstandard2.0 has no <c>OperatingSystem.Is*</c>).</summary>
public static class McpOsExtensions
{
    public static McpOs Current() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? McpOs.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? McpOs.MacOs
        : McpOs.Linux;
}

/// <summary>Where each program keeps its server list on this computer; behind an interface so tests read fixtures.</summary>
public interface IMcpImportLocations
{
    /// <summary>The files to try for <paramref name="source"/>, best first; none for a file the person picks.</summary>
    IReadOnlyList<string> PathsOf(McpImportSource source);
}

/// <summary>
/// The server lists of the other programs, per operating system:
/// <list type="bullet">
/// <item>Claude Desktop (modelcontextprotocol.io, "Connect to local MCP servers"): <c>~/Library/Application Support/Claude/claude_desktop_config.json</c>,
/// <c>%APPDATA%\Claude\claude_desktop_config.json</c>, <c>~/.config/Claude/claude_desktop_config.json</c>. The Windows Store build keeps it below
/// <c>%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude</c>, tried second.</item>
/// <item>VS Code (user profile, "MCP: Open User Configuration"): <c>mcp.json</c> in the user data folder <c>Code/User</c>: <c>%APPDATA%\Code\User</c>,
/// <c>~/Library/Application Support/Code/User</c>, <c>~/.config/Code/User</c> (the folder is VS Code's documented user data folder; the page
/// on MCP names the command, not the path). Named profiles keep their own file: pick it as a file.</item>
/// <item>Cursor (cursor.com/docs/context/mcp): <c>~/.cursor/mcp.json</c> on all three.</item>
/// <item>Windsurf (docs.windsurf.com/windsurf/cascade/mcp, "Cascade MCP Integration"): <c>~/.codeium/windsurf/mcp_config.json</c> on all three
/// (<c>%USERPROFILE%</c> on Windows). Some translated pages of the same docs say <c>~/.codeium/mcp_config.json</c>, without the
/// <c>windsurf</c> folder; the documented English path is tried first, the shorter one second.</item>
/// <item>Claude Code (code.claude.com/docs/en/mcp, "MCP installation scopes"): <c>~/.claude.json</c> on all three; user-scoped servers are
/// the top-level <c>mcpServers</c> (local-scope ones sit under <c>projects</c> and are not read; project ones are a <c>.mcp.json</c> the person
/// can pick). The docs do not show the user-scope JSON shape outright; it is the same <c>mcpServers</c> map as the project file. Claude Code
/// can move its configuration folder with <c>CLAUDE_CONFIG_DIR</c>; the docs do not say that moves <c>.claude.json</c> too, so it is not followed.</item>
/// <item>Gemini CLI (google-gemini/gemini-cli docs, "MCP servers"): <c>~/.gemini/settings.json</c>, key <c>mcpServers</c>; the project file
/// <c>.gemini/settings.json</c> is not read.</item>
/// <item>LM Studio (lmstudio.ai/docs/app/mcp and the launch post lmstudio.ai/blog/mcp): <c>~/.lmstudio/mcp.json</c>, <c>%USERPROFILE%\.lmstudio\mcp.json</c>
/// on Windows. The app's docs page only says "Install, Edit mcp.json" and names no path; the launch post names this one. A bug report
/// (lmstudio-bug-tracker #1371) says a 0.3.33 on macOS had no <c>.lmstudio</c> folder and a copy in <c>~/.cache/lm-studio/mcp.json</c>;
/// unconfirmed, so not read.</item>
/// </list>
/// </summary>
public sealed class McpImportLocations : IMcpImportLocations
{
    private readonly McpOs _os;
    private readonly Func<string, string?> _env;
    private readonly string _home;
    private readonly Func<string, IEnumerable<string>> _glob;

    public McpImportLocations(McpOs os, Func<string, string?> env, string home, Func<string, IEnumerable<string>>? glob = null)
    {
        _os = os;
        _env = env;
        _home = home;
        _glob = glob ?? (pattern => Directory.Exists(Path.GetDirectoryName(pattern)) ? Directory.GetDirectories(Path.GetDirectoryName(pattern)!, Path.GetFileName(pattern)) : []);
    }

    /// <summary>The locations on this computer; each argument can be given instead (tests, or a host that knows better).</summary>
    public static McpImportLocations ForThisComputer(McpOs? os = null, Func<string, string?>? env = null, string? home = null) => new(
        os ?? McpOsExtensions.Current(),
        env ?? Environment.GetEnvironmentVariable,
        home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public IReadOnlyList<string> PathsOf(McpImportSource source)
    {
        var roaming = _os switch
        {
            McpOs.Windows => _env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(_home, "AppData", "Roaming"),
            McpOs.MacOs => Path.Combine(_home, "Library", "Application Support"),
            _ => _env("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x) ? x : Path.Combine(_home, ".config"),
        };
        switch (source)
        {
            case McpImportSource.ClaudeDesktop:
                var paths = new List<string> { Path.Combine(roaming, "Claude", "claude_desktop_config.json") };
                if (_os == McpOs.Windows)
                {
                    var local = _env("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(_home, "AppData", "Local");
                    paths.AddRange(_glob(Path.Combine(local, "Packages", "Claude_*"))
                        .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
                }
                return paths;
            case McpImportSource.VsCode:
                return [Path.Combine(roaming, "Code", "User", "mcp.json")];
            case McpImportSource.Cursor:
                return [Path.Combine(_home, ".cursor", "mcp.json")];
            case McpImportSource.Windsurf:
                return [Path.Combine(_home, ".codeium", "windsurf", "mcp_config.json"), Path.Combine(_home, ".codeium", "mcp_config.json")];
            case McpImportSource.ClaudeCode:
                return [Path.Combine(_home, ".claude.json")];
            case McpImportSource.GeminiCli:
                return [Path.Combine(_home, ".gemini", "settings.json")];
            case McpImportSource.LmStudio:
                return [Path.Combine(_home, ".lmstudio", "mcp.json")];
            default:
                return [];
        }
    }
}

/// <summary>Why a server of a file was not offered for import; a view model turns it into a text.</summary>
public enum McpSkipReason
{
    /// <summary>
    /// An address beyond this computer: the person types such a server in (sdk/51 F05); another app's file often carries its token
    /// in the URL, and plain http there needs the person's switch, which an import never sets.
    /// </summary>
    NetworkServer,

    /// <summary>Neither a command nor an address.</summary>
    NothingToStart,

    /// <summary>Not an object, or fields of the wrong type.</summary>
    NotUnderstood,

    /// <summary>The entry breaks a rule (<see cref="McpSkip.Detail"/> names the field).</summary>
    NotValid,
}

/// <summary>Something the person should know about a server before they tick it.</summary>
public enum McpImportNote
{
    /// <summary>VS Code asks for a value (<c>${input:...}</c>) when the server starts; here it is empty and the server is off until it is filled in.</summary>
    NeedsValue,

    /// <summary>The file uses other placeholders (<c>${env:HOME}</c>, <c>${workspaceFolder}</c>) that this app does not expand; they are kept as written.</summary>
    HasPlaceholders,

    /// <summary>Fields this app does not use were left out (<see cref="McpImportCandidate.Detail"/>).</summary>
    IgnoredFields,

    /// <summary>The name was not a valid label and became <see cref="McpServerEntry.Label"/>.</summary>
    Renamed,

    /// <summary>The other program had the server switched off.</summary>
    WasOff,
}

public sealed record McpImportCandidate(McpServerEntry Entry, string OriginalName, IReadOnlyList<McpImportNote> Notes, string? Detail = null);

/// <param name="Name">The server's name in the file.</param>
/// <param name="Reason">Why it was left out.</param>
/// <param name="Detail">The field that broke a rule; for <see cref="McpSkipReason.NetworkServer"/> the address without user, query or fragment.</param>
public sealed record McpSkip(string Name, McpSkipReason Reason, string? Detail = null);

public enum McpImportProblem
{
    None,

    /// <summary>The file is not there.</summary>
    NotFound,

    /// <summary>It cannot be read or is not JSON.</summary>
    Unreadable,

    /// <summary>It is JSON without a server list.</summary>
    NoServerList,

    /// <summary>Bigger than a server list can be.</summary>
    TooLarge,
}

public sealed record McpImportResult(McpImportSource Source, string? Path, McpImportProblem Problem, IReadOnlyList<McpImportCandidate> Servers, IReadOnlyList<McpSkip> Skipped)
{
    public static McpImportResult Failed(McpImportSource source, string? path, McpImportProblem problem) => new(source, path, problem, [], []);
}

/// <summary>
/// Reads another program's MCP server list into candidates for the person to tick (desktop-agent/31 §4.4). Nothing is copied
/// here: the import sheet adds the ticked ones to the store. Claude Desktop, Cursor and a <c>.mcp.json</c> use <c>mcpServers</c>;
/// VS Code uses <c>servers</c> with <c>inputs</c> (<c>${input:id}</c> becomes an empty value and the server is added switched
/// off). Skipped, with the reason: addresses that are not on this computer, entries with nothing to start, entries that break a
/// rule. Fields this app does not use are named in a note.
/// </summary>
public sealed class McpImporter
{
    private const long MaxBytes = 1024 * 1024;

    // Claude Code keeps its whole history and every project's settings in ~/.claude.json, which is far bigger than a server list.
    private const long MaxBytesClaudeCode = 32 * 1024 * 1024;
    private static readonly Regex Input = new(@"\$\{input:[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\$\{[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "type", "command", "args", "env", "cwd", "url", "httpUrl", "serverUrl", "headers", "disabled", "name",
    };

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 };

    private readonly IMcpImportLocations _locations;

    public McpImporter(IMcpImportLocations locations) => _locations = locations;

    /// <summary>The file this computer has for <paramref name="source"/>, or null (none there, or a file the person picks).</summary>
    public string? FindFile(McpImportSource source) => _locations.PathsOf(source).FirstOrDefault(File.Exists);

    /// <param name="source">Which program's list.</param>
    /// <param name="path">The file; this computer's own for the source if not given.</param>
    /// <param name="taken">The labels the person has already, so imported ones get free labels.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<McpImportResult> ReadAsync(McpImportSource source, string? path, IEnumerable<string> taken, CancellationToken cancellationToken = default)
    {
        path ??= FindFile(source);
        if (path is null || !File.Exists(path))
            return McpImportResult.Failed(source, path, McpImportProblem.NotFound);
        try
        {
            if (new FileInfo(path).Length > (source == McpImportSource.ClaudeCode ? MaxBytesClaudeCode : MaxBytes))
                return McpImportResult.Failed(source, path, McpImportProblem.TooLarge);
            return Parse(source, path, await Task.Run(() => File.ReadAllText(path), cancellationToken).ConfigureAwait(false), taken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return McpImportResult.Failed(source, path, McpImportProblem.Unreadable);
        }
    }

    /// <summary>Parses the text of a file (tests give fixtures).</summary>
    public static McpImportResult Parse(McpImportSource source, string? path, string text, IEnumerable<string> taken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Options);
        }
        catch (JsonException)
        {
            return McpImportResult.Failed(source, path, McpImportProblem.Unreadable);
        }
        using (document)
        {
            var root = document.RootElement;
            var key = source == McpImportSource.VsCode ? "servers" : "mcpServers";
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var servers) || servers.ValueKind != JsonValueKind.Object)
                return McpImportResult.Failed(source, path, McpImportProblem.NoServerList);

            var used = new HashSet<string>(taken, StringComparer.Ordinal);
            var found = new List<McpImportCandidate>();
            var skipped = new List<McpSkip>();
            var origin = Name(source);
            foreach (var property in servers.EnumerateObject())
            {
                var outcome = Read(property.Name, property.Value, origin, used);
                if (outcome.Skip is { } skip)
                    skipped.Add(skip);
                else
                {
                    used.Add(outcome.Candidate!.Entry.Label);
                    found.Add(outcome.Candidate);
                }
            }
            return new McpImportResult(source, path, McpImportProblem.None, found, skipped);
        }
    }

    /// <summary>The program's name for the entry's origin.</summary>
    public static string Name(McpImportSource source) => source switch
    {
        McpImportSource.ClaudeDesktop => "Claude Desktop",
        McpImportSource.VsCode => "VS Code",
        McpImportSource.Cursor => "Cursor",
        McpImportSource.Windsurf => "Windsurf",
        McpImportSource.ClaudeCode => "Claude Code",
        McpImportSource.GeminiCli => "Gemini CLI",
        McpImportSource.LmStudio => "LM Studio",
        _ => ".mcp.json",
    };

    private static (McpImportCandidate? Candidate, McpSkip? Skip) Read(string name, JsonElement server, string origin, HashSet<string> used)
    {
        if (server.ValueKind != JsonValueKind.Object)
            return (null, new McpSkip(name, McpSkipReason.NotUnderstood));
        var notes = new List<McpImportNote>();
        var needsInput = false;
        var placeholders = false;

        string? Text(string field)
        {
            if (!server.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            return Convert(value);
        }

        string Convert(JsonElement value)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (Input.IsMatch(text))
            {
                needsInput = true;
                text = Input.Replace(text, "");
            }
            if (Placeholder.IsMatch(text))
                placeholders = true;
            return text;
        }

        IEnumerable<McpSecretPair> Pairs(string field)
        {
            if (!server.TryGetProperty(field, out var map) || map.ValueKind != JsonValueKind.Object)
                return [];
            return map.EnumerateObject().Select(p => new McpSecretPair(p.Name, Convert(p.Value))).ToList();
        }

        string[] Args()
        {
            if (!server.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
                return [];
            return [.. args.EnumerateArray().Select(Convert)];
        }

        var command = Text("command");
        // Gemini CLI: httpUrl is streamable HTTP and url is SSE; Windsurf: serverUrl (url also works there); Claude Code: type + url.
        var url = Text("httpUrl") ?? Text("url") ?? Text("serverUrl");
        var type = Text("type")?.ToLowerInvariant();
        if (type is "ws" or "wss" or "websocket" or "sdk")
            return (null, new McpSkip(name, McpSkipReason.NotUnderstood, "type"));
        var asAddress = url is not null && (command is null || type is "http" or "sse" or "streamable-http" or "streamablehttp");
        if (!asAddress && string.IsNullOrWhiteSpace(command))
            return (null, new McpSkip(name, McpSkipReason.NothingToStart));
        // Only servers on this computer arrive (off). A server elsewhere is typed in by the person (sdk/51 F05): another app's file
        // often carries its token in the URL, and such servers mostly need OAuth; plain http beyond this computer needs the
        // person's switch, which an import never sets.
        if (asAddress && (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var parsed) || !LocalMcpServer.IsLoopback(parsed)))
            return (null, new McpSkip(name, McpSkipReason.NetworkServer, McpAddresses.Display(url!)));

        var wanted = McpServerValidator.LabelFrom(name);
        var label = McpServerValidator.FreeLabel(wanted, used);
        if (label != name)
            notes.Add(McpImportNote.Renamed);
        var cwd = Text("cwd");
        var entry = new McpServerEntry
        {
            Label = label,
            Command = asAddress ? null : command!.Trim(),
            Arguments = asAddress ? [] : Args(),
            WorkingFolder = asAddress || string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) ? null : cwd,
            Environment = asAddress ? [] : [.. Pairs("env")],
            Url = asAddress ? url!.Trim() : null,
            Headers = asAddress ? [.. Pairs("headers")] : [],
            Origin = origin,
            Enabled = false, // sdk/51 F05: an imported server arrives off; the person switches each one on
        };
        if (server.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
        {
            entry = entry with { Enabled = false };
            notes.Add(McpImportNote.WasOff);
        }
        if (needsInput)
        {
            entry = entry with { Enabled = false };
            notes.Add(McpImportNote.NeedsValue);
        }
        if (placeholders)
            notes.Add(McpImportNote.HasPlaceholders);
        var ignored = server.EnumerateObject().Select(p => p.Name).Where(n => !Known.Contains(n)).ToList();
        if (ignored.Count > 0)
            notes.Add(McpImportNote.IgnoredFields);

        var check = McpServerValidator.Validate(entry, []);
        if (!check.IsValid)
            return (null, new McpSkip(name, McpSkipReason.NotValid, check.Field));
        return (new McpImportCandidate(entry, name, notes, ignored.Count > 0 ? string.Join(", ", ignored) : null), null);
    }
}

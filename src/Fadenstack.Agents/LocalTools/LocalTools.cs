using Fadenstack.Tools;

namespace Fadenstack.LocalTools;

// What the Tools view and the MCP module share (sdk/51 F05). The layers keep the UI layer from referencing a module, so the
// contract sits here, in the core: Fadenstack.UI builds its view models on ILocalToolsCatalog, and Fadenstack.Mcp implements it
// over the person's servers (McpToolsCatalog). It is the desktop app's Tools page contract, without what only the desktop has
// (its built-in tools, the other assistants, its Settings).

/// <summary>A tool the app itself gives the agent (a host tool), as the Tools view's "In this app" lists it. Never a local MCP server's tool, a server's tool or one of the SDK's own built-ins.</summary>
public sealed class AppToolInfo
{
    public AppToolInfo(string name, string description, ToolClass toolClass)
    {
        Name = name;
        Description = description;
        Class = toolClass;
    }

    /// <summary>The tool's name, as the model sees it.</summary>
    public string Name { get; }

    /// <summary>What the app says the tool does; may be long and may hold line breaks (the view cleans it).</summary>
    public string Description { get; }

    /// <summary>The class the tool has now (what decides whether a call asks).</summary>
    public ToolClass Class { get; }
}

/// <summary>What the agent's local MCP policy says; the page's banner.</summary>
public enum ToolsPolicy
{
    /// <summary>Not known yet: nobody is signed in, or the agent has not started.</summary>
    Unknown,

    /// <summary>The organisation (or, standalone, the app) lets this assistant use MCP servers on this computer.</summary>
    Allowed,

    /// <summary>It has not allowed it.</summary>
    NotAllowed,

    /// <summary>Any server, for this person only.</summary>
    DeveloperMode,
}

/// <summary>Where a local server stands.</summary>
public enum LocalServerState
{
    Off,
    Starting,
    Running,
    Failed,
    NotAllowed,
}

/// <summary>The class a tool has now (what decides whether a call asks): a server's word alone never makes it Read.</summary>
public enum LocalToolClass
{
    /// <summary>Not marked: every call asks.</summary>
    Unknown,

    /// <summary>Reads only: the server is trusted and says so, or the person raised the tool.</summary>
    Read,

    /// <summary>The person raised the tool to write.</summary>
    Write,

    /// <summary>The server says it may delete or overwrite: always asks, and no raise can lower it.</summary>
    Destructive,
}

/// <summary>The person's raise of one tool, or none.</summary>
public enum LocalToolRaise
{
    /// <summary>No raise: the tool is as it arrives.</summary>
    None,

    Read,

    Write,
}

/// <summary>Why plain http to a server was refused (the MCP module's reason, in the view's terms).</summary>
public enum LocalPlainHttpRefusal
{
    /// <summary>The address is beyond this computer and the person has not turned on the switch.</summary>
    NoOptIn,

    /// <summary>The switch is on, but the address is not on the local network.</summary>
    NotLocal,

    /// <summary>The server is sent a header value: never over plain http beyond this computer.</summary>
    Secret,

    /// <summary>The agent's profile or this computer's policy does not allow plain http on the local network.</summary>
    NotAllowed,
}

/// <summary>What a server says about one of its tools, with the class the tool has now and the person's raise.</summary>
public sealed class LocalToolInfo
{
    public LocalToolInfo(string name, string description, string? title, bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld,
        LocalToolClass toolClass = LocalToolClass.Unknown, LocalToolRaise raise = LocalToolRaise.None)
    {
        Name = name;
        Description = description;
        Title = title;
        ReadOnly = readOnly;
        Destructive = destructive;
        Idempotent = idempotent;
        OpenWorld = openWorld;
        Class = toolClass;
        Raise = raise;
    }

    public string Name { get; }

    public string Description { get; }

    public string? Title { get; }

    /// <summary>The server's own word: it counts only from a server marked trusted.</summary>
    public bool? ReadOnly { get; }

    /// <summary>The server's own word: it always counts, and it is a floor no raise goes below.</summary>
    public bool? Destructive { get; }

    public bool? Idempotent { get; }

    public bool? OpenWorld { get; }

    /// <summary>The class the tool has now.</summary>
    public LocalToolClass Class { get; }

    /// <summary>The person's raise of this tool.</summary>
    public LocalToolRaise Raise { get; }
}

/// <summary>One of the person's local servers with where it stands. Never carries a secret.</summary>
public sealed class LocalServerInfo
{
    public string Label { get; set; } = "";

    /// <summary>The program's name or the address.</summary>
    public string Where { get; set; } = "";

    public bool IsProgram { get; set; }

    public bool Enabled { get; set; }

    public LocalServerState State { get; set; }

    /// <summary>The last error (Failed) or the policy's reason (NotAllowed).</summary>
    public string? Detail { get; set; }

    /// <summary>The server changed its tools after it was attached.</summary>
    public bool ToolsChanged { get; set; }

    /// <summary>Where it came from: "typed" or the program it was imported from.</summary>
    public string Origin { get; set; } = "";

    public IReadOnlyList<LocalToolInfo> Tools { get; set; } = Array.Empty<LocalToolInfo>();

    /// <summary>The names, never the values, of the environment variables or headers the server takes.</summary>
    public IReadOnlyList<string> SecretNames { get; set; } = Array.Empty<string>();

    /// <summary>The names among <see cref="SecretNames"/> that have no value yet (a server found by a scan or imported with a placeholder).</summary>
    public IReadOnlyList<string> MissingValues { get; set; } = Array.Empty<string>();

    /// <summary>The longest one call may take, in seconds.</summary>
    public int CallTimeoutSeconds { get; set; }

    /// <summary>When the server was seen running first; null while it is not running.</summary>
    public DateTimeOffset? RunningSince { get; set; }

    /// <summary>The server's read-only hints count (the person, or the administrator, marked it trusted).</summary>
    public bool Trusted { get; set; }

    /// <summary>The server is an address over plain http beyond this computer: the switch "Allow plain http on the local network" applies.</summary>
    public bool PlainHttpApplies { get; set; }

    /// <summary>The person's switch is on.</summary>
    public bool PlainHttpInLan { get; set; }

    /// <summary>Why a connection over plain http was refused; null when it was not.</summary>
    public LocalPlainHttpRefusal? PlainHttpRefusal { get; set; }
}

/// <summary>A name and a value of a program's environment or an address's headers while the person edits them.</summary>
public sealed class SecretPairDraft
{
    public string Name { get; set; } = "";

    /// <summary>The new value. Empty with <see cref="IsKept"/>: the saved value stays.</summary>
    public string Value { get; set; } = "";

    /// <summary>The value is the one saved earlier, which the page never sees (it shows dots).</summary>
    public bool IsKept { get; set; }

    /// <summary>Forgets a typed value (a draft is not kept once it was saved or tested).</summary>
    public void Clear() => Value = "";
}

/// <summary>A server as the add and edit sheet holds it.</summary>
public sealed class ServerDraft
{
    public string Label { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool IsProgram { get; set; } = true;

    public string Command { get; set; } = "";

    public IList<string> Arguments { get; set; } = new List<string>();

    public string WorkingFolder { get; set; } = "";

    public IList<SecretPairDraft> Environment { get; set; } = new List<SecretPairDraft>();

    public string Url { get; set; } = "";

    public IList<SecretPairDraft> Headers { get; set; } = new List<SecretPairDraft>();

    public int CallTimeoutSeconds { get; set; } = 120;

    /// <summary>Forgets every typed value of the environment and the headers.</summary>
    public void ClearSecrets()
    {
        foreach (var pair in Environment)
        {
            pair.Clear();
        }
        foreach (var pair in Headers)
        {
            pair.Clear();
        }
    }
}

/// <summary>What is wrong with a draft.</summary>
public enum ServerProblem
{
    None,
    LabelMissing,
    LabelInvalid,
    LabelTaken,
    CommandMissing,
    CommandInvalid,
    FolderNotAbsolute,
    NameInvalid,
    ValueInvalid,
    AddressMissing,
    AddressInvalid,
    TimeoutOutOfRange,

    /// <summary>Plain http beyond this computer with a header value: headers hold tokens, so they need https.</summary>
    PlainHttpWithHeaders,

    /// <summary>Plain http to an address outside the local network.</summary>
    PlainHttpNotLocal,
}

/// <summary>How a test of a server went.</summary>
public enum TestOutcome
{
    /// <summary>The policy does not allow local servers: nothing was started.</summary>
    NotAllowed,

    Started,

    Failed,
}

/// <summary>The result of a test. <see cref="Error"/> is a short cleaned line, never a secret.</summary>
public sealed class ServerTestInfo
{
    public ServerTestInfo(TestOutcome outcome, IReadOnlyList<string> tools, string? error, LocalPlainHttpRefusal? plainHttp = null)
    {
        Outcome = outcome;
        Tools = tools;
        Error = error;
        PlainHttp = plainHttp;
    }

    public TestOutcome Outcome { get; }

    public IReadOnlyList<string> Tools { get; }

    public string? Error { get; }

    /// <summary>Why plain http to the server was refused, when it was: nothing was sent.</summary>
    public LocalPlainHttpRefusal? PlainHttp { get; }
}

/// <summary>The programs whose server list can be imported.</summary>
public enum ImportSourceKind
{
    ClaudeDesktop,
    VsCode,
    Cursor,
    ProjectFile,
}

public enum ImportProblem
{
    None,
    NotFound,
    Unreadable,
    NoServerList,
    TooLarge,
}

public enum ImportNote
{
    NeedsValue,
    HasPlaceholders,
    IgnoredFields,
    Renamed,
    WasOff,
}

public enum ImportSkipReason
{
    NetworkServer,
    NothingToStart,
    NotUnderstood,
    NotValid,
}

public sealed class ImportCandidateInfo
{
    public string Label { get; set; } = "";

    public string OriginalName { get; set; } = "";

    public string Where { get; set; } = "";

    public IReadOnlyList<ImportNote> Notes { get; set; } = Array.Empty<ImportNote>();

    /// <summary>The names of the fields that were left out.</summary>
    public string? Detail { get; set; }
}

public sealed class ImportSkipInfo
{
    public string Name { get; set; } = "";

    public ImportSkipReason Reason { get; set; }

    public string? Detail { get; set; }
}

/// <summary>What was found in another program's file. Nothing is copied until <see cref="ILocalToolsCatalog.ImportAsync"/>; an import never turns on the plain-http switch or marks a server trusted.</summary>
public sealed class ImportPreview
{
    public ImportProblem Problem { get; set; }

    /// <summary>The file read.</summary>
    public string? Path { get; set; }

    public IReadOnlyList<ImportCandidateInfo> Servers { get; set; } = Array.Empty<ImportCandidateInfo>();

    public IReadOnlyList<ImportSkipInfo> Skipped { get; set; } = Array.Empty<ImportSkipInfo>();
}

public enum CloudToolsStatus
{
    /// <summary>Nobody is signed in or the agent has not started.</summary>
    NotSignedIn,

    Listed,

    /// <summary>The server does not list its tools (an older one).</summary>
    NotListed,

    /// <summary>The list could not be loaded right now.</summary>
    Unavailable,
}

/// <summary>A tool of the organisation's server, as "On your server" lists it.</summary>
public sealed class CloudToolInfo
{
    public CloudToolInfo(string name, string server, string description, bool allowed, bool enabled)
    {
        Name = name;
        Server = server;
        Description = description;
        Allowed = allowed;
        Enabled = enabled;
    }

    public string Name { get; }

    public string Server { get; }

    public string Description { get; }

    /// <summary>This assistant's tool rules let it be offered and run.</summary>
    public bool Allowed { get; }

    /// <summary>Switched on; an administrator may have switched it off.</summary>
    public bool Enabled { get; }
}

public sealed class CloudToolsResult
{
    public CloudToolsResult(CloudToolsStatus status, IReadOnlyList<CloudToolInfo> tools)
    {
        Status = status;
        Tools = tools;
    }

    public CloudToolsStatus Status { get; }

    public IReadOnlyList<CloudToolInfo> Tools { get; }
}

/// <summary>What a scan looks at: the person ticks the places on the scan sheet.</summary>
public sealed class ScanOptionsInfo
{
    /// <summary>Other apps' MCP settings files.</summary>
    public bool OtherApps { get; set; } = true;

    /// <summary>Servers answering on this computer's loopback ports.</summary>
    public bool ThisComputer { get; set; } = true;
}

/// <summary>A range of loopback ports a scan looks at, both ends included.</summary>
public sealed class PortRangeInfo
{
    public PortRangeInfo(int from, int to)
    {
        From = from;
        To = to;
    }

    public int From { get; }

    public int To { get; }
}

/// <summary>What the person can do with a finding of a scan.</summary>
public enum ScanFindingState
{
    /// <summary>Not among the person's servers: it can be ticked and added.</summary>
    New,

    /// <summary>The person has a server that starts the same program or has the same address.</summary>
    AlreadyAdded,

    /// <summary>An address that is not on this computer: a scan does not add it; the person types it in.</summary>
    NetworkLater,

    /// <summary>It can be added, but a value it needs is empty: it is added switched off.</summary>
    NeedsValue,

    /// <summary>The entry could not be read; shown with the reason, not addable.</summary>
    NotUnderstood,
}

/// <summary>One app the scan looked in: whether it is on this computer, and how many servers it had.</summary>
public sealed class ScanAppInfo
{
    public ScanAppInfo(string name, bool installed, int found)
    {
        Name = name;
        Installed = installed;
        Found = found;
    }

    public string Name { get; }

    public bool Installed { get; }

    public int Found { get; }
}

/// <summary>One server a scan found. Names of secrets only, never a value; <see cref="Id"/> is what <see cref="ILocalToolsCatalog.AddFoundAsync"/> takes.</summary>
public sealed class ScanFindingInfo
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>The app whose settings had it ("Claude Desktop"), or empty for a server answering on a port.</summary>
    public string FoundIn { get; set; } = "";

    /// <summary>Found in another app's settings (otherwise answering on this computer).</summary>
    public bool FromApp { get; set; }

    public bool IsProgram { get; set; }

    /// <summary>The program with its arguments, or the address.</summary>
    public string Where { get; set; } = "";

    public ScanFindingState State { get; set; }

    public IReadOnlyList<string> SecretNames { get; set; } = Array.Empty<string>();

    /// <summary>What a server answering on a port called itself.</summary>
    public string? ServerName { get; set; }

    public int? ToolCount { get; set; }

    /// <summary>Why it was not understood.</summary>
    public string? Detail { get; set; }
}

public sealed class ScanResultInfo
{
    public IReadOnlyList<ScanAppInfo> Apps { get; set; } = Array.Empty<ScanAppInfo>();

    public int PortsChecked { get; set; }

    public IReadOnlyList<ScanFindingInfo> Findings { get; set; } = Array.Empty<ScanFindingInfo>();

    public double Seconds { get; set; }
}

/// <summary>
/// Everything the Tools view shows and changes (sdk/51 F05; the desktop app's Tools page without its built-in tools and
/// assistants): the person's local MCP servers with their tools, and the cloud tools of their server. Secrets of a server never
/// leave the host: a draft carries only names and "kept" marks, and the host puts the saved values back when it stores the draft.
/// </summary>
public interface ILocalToolsCatalog
{
    /// <summary>The agent is running for a signed-in person: the lists are real.</summary>
    bool SignedIn { get; }

    ToolsPolicy Policy { get; }

    /// <summary>
    /// The agent is connected to an organisation's Fadenstack server. False while that is not known (nobody is signed in, the agent
    /// has not started) and for a standalone agent (the person's own model endpoint): the view then shows no "On your server" and
    /// asks for no cloud tools.
    /// </summary>
    bool IsGoverned { get; }

    /// <summary>The tools the app itself gives the agent (host tools only); empty while the agent is not running.</summary>
    IReadOnlyList<AppToolInfo> AppTools { get; }

    /// <summary>The list of servers is written to disk (encrypted); false: it is kept in memory until the app closes.</summary>
    bool ServersKeptOnDisk { get; }

    /// <summary>The saved list could not be read; it was set aside and the list starts empty.</summary>
    bool ServersUnreadable { get; }

    IReadOnlyList<LocalServerInfo> LocalServers { get; }

    /// <summary>The readable folders the servers are told as their roots.</summary>
    int RootCount { get; }

    /// <summary>The names of the readable folders the servers are told as their roots.</summary>
    IReadOnlyList<string> RootNames { get; }

    /// <summary>
    /// Plain http on the local network is allowed here: the agent's profile says <c>local_mcp.plain_http: allow</c> and this
    /// computer's policy allows it. The person's switch per server is still needed. False: the view hides the switch.
    /// </summary>
    bool PlainHttpPermitted { get; }

    /// <summary>The person's own marks count (standalone); false: an administrator's do, and the view shows them without a way to change them.</summary>
    bool MarksAreYours { get; }

    /// <summary>Loads the cloud tools for this assistant (<c>GET /v1/agents/{agent}/tools</c>).</summary>
    Task<CloudToolsResult> GetCloudToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches a server on or off; it holds from the next message.</summary>
    Task SetEnabledAsync(string label, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Marks a server trusted or not: only a trusted server's read-only hints count. From the next message.</summary>
    void SetTrusted(string label, bool trusted);

    /// <summary>
    /// The person's raise of one tool: read, write or none. A tool the server says is destructive is never lowered: the raise
    /// is not kept for it.
    /// </summary>
    void SetToolClass(string label, string tool, LocalToolRaise raise);

    /// <summary>
    /// The switch "Allow plain http on the local network" of an address server. Turning it on needs the version of the warning
    /// the person confirmed just before (<paramref name="warningVersion"/>); the problem when the server cannot have it.
    /// </summary>
    Task<ServerProblem> SetPlainHttpInLanAsync(string label, bool on, string? warningVersion, CancellationToken cancellationToken = default);

    /// <summary>A draft of a saved server for the edit sheet; null for an unknown label.</summary>
    ServerDraft? GetDraft(string label);

    /// <summary>Checks a draft without saving it. <paramref name="originalLabel"/> is the server being edited, null for a new one.</summary>
    ServerProblem Validate(ServerDraft draft, string? originalLabel);

    /// <summary>Saves a new server (<paramref name="originalLabel"/> null) or replaces one; the problem when it is not valid.</summary>
    Task<ServerProblem> SaveAsync(string? originalLabel, ServerDraft draft, CancellationToken cancellationToken = default);

    Task RemoveAsync(string label, CancellationToken cancellationToken = default);

    /// <summary>Starts the draft's server for a moment, lists its tools and stops it. Nothing starts where the policy does not allow it.</summary>
    Task<ServerTestInfo> TestAsync(ServerDraft draft, string? originalLabel, CancellationToken cancellationToken = default);

    /// <summary>Reads another program's server list. <paramref name="path"/> null: this computer's own file for the source.</summary>
    Task<ImportPreview> ReadImportAsync(ImportSourceKind source, string? path, CancellationToken cancellationToken = default);

    /// <summary>The file this computer has for the source, or null.</summary>
    string? FindImportFile(ImportSourceKind source);

    /// <summary>Adds the ticked servers (by their labels in <paramref name="preview"/>); returns how many were added.</summary>
    Task<int> ImportAsync(ImportPreview preview, IReadOnlyList<string> labels, CancellationToken cancellationToken = default);

    /// <summary>Stops the server and starts it again (it holds from the next message).</summary>
    Task RestartAsync(string label, CancellationToken cancellationToken = default);

    /// <summary>The host has a scanner: otherwise the page hides its Scan buttons.</summary>
    bool CanScan { get; }

    /// <summary>The apps a scan can look in on this operating system, by name, in the order the sheet lists them.</summary>
    IReadOnlyList<string> ScanAppNames { get; }

    /// <summary>The loopback port ranges a scan looks at.</summary>
    IReadOnlyList<PortRangeInfo> ScanPortRanges { get; }

    /// <summary>
    /// Looks for MCP servers where the person ticked: reads and probes only, never starts a program, sends nothing off this
    /// computer. <paramref name="progress"/> gets the steps done and the steps in all.
    /// </summary>
    Task<ScanResultInfo> ScanAsync(ScanOptionsInfo options, Action<int, int>? progress, CancellationToken cancellationToken = default);

    /// <summary>Adds the findings with these ids from a scan's result (a finding that needs a value is added switched off); returns how many were added.</summary>
    Task<int> AddFoundAsync(ScanResultInfo result, IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Servers, their states, the policy or the signed-in agent changed. From any thread.</summary>
    event EventHandler? Changed;
}

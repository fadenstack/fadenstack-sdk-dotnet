using System.Globalization;
using System.Runtime.CompilerServices;
using Fadenstack.Agents;
using Fadenstack.LocalTools;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Mcp;

/// <summary>What the Tools view needs to know of the running agent; an app gives it (<see cref="FadenstackAgentToolsView"/>, or a fake in tests).</summary>
public interface IAgentToolsView
{
    /// <summary>The agent has started for a signed-in person: its profile and tools are real.</summary>
    bool IsRunning { get; }

    AgentProfile? Profile { get; }

    /// <summary>How the agent is connected (a governed server or the person's own endpoint); null until it runs.</summary>
    AgentConnection? Connection { get; }

    /// <summary>The tools the agent runs, with their effective classes; empty until it runs.</summary>
    IReadOnlyList<AgentTool> Tools { get; }

    /// <summary>
    /// The agent's profile says <c>local_mcp.plain_http: allow</c> and this computer's policy allows plain http (the same test
    /// the MCP module makes when it connects a server).
    /// </summary>
    bool PlainHttpPermitted { get; }

    /// <summary>The cloud tools of the agent's server (<c>null</c>: the server does not list them); throws a <c>FadenstackException</c> when it cannot.</summary>
    Task<IReadOnlyList<AgentToolInfo>?> ListCloudToolsAsync(CancellationToken cancellationToken);

    /// <summary>The agent started, or the person signed out.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// The view of a <see cref="FadenstackAgent"/> for <see cref="McpToolsCatalog"/>. An agent raises no event when it starts, so the
/// app calls <see cref="RaiseChanged"/> after <c>StartAsync</c> and after a sign-out.
/// </summary>
public sealed class FadenstackAgentToolsView : IAgentToolsView
{
    private readonly FadenstackAgent _agent;

    public FadenstackAgentToolsView(FadenstackAgent agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public event EventHandler? Changed;

    /// <summary>The agent has started (<c>Profile</c> throws before it).</summary>
    public bool IsRunning => Profile is not null;

    public AgentProfile? Profile
    {
        get
        {
            try
            {
                return _agent.Profile;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>How the agent is connected; null before it runs (<c>Connection</c> reads the settings, so it is only read while it runs).</summary>
    public AgentConnection? Connection => IsRunning ? _agent.Connection : null;

    /// <summary>The agent's tools once it runs; none before.</summary>
    public IReadOnlyList<AgentTool> Tools => IsRunning ? _agent.Tools : Array.Empty<AgentTool>();

    /// <summary>
    /// The agent's profile is already narrowed by the machine policy (<c>plain_http</c> is <c>deny</c> where the policy forbids
    /// plain http), and the policy is read again here: the module's own test, <c>LocalMcpModule.PlainHttpPermitted</c>.
    /// </summary>
    public bool PlainHttpPermitted => Profile is { } profile
        && string.Equals(profile.LocalMcp.PlainHttp, "allow", StringComparison.Ordinal)
        && _agent.MachinePolicy.PlainHttp == PolicyAccess.Allowed;

    public Task<IReadOnlyList<AgentToolInfo>?> ListCloudToolsAsync(CancellationToken cancellationToken) => _agent.ListServerToolsAsync(cancellationToken);

    /// <summary>Tells the catalog that the agent started or the person signed out.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The Tools view's side of the MCP module (sdk/51 F05): the person's servers from the <see cref="McpHost"/>, with their trust mark,
/// their tools' classes and raises and the plain-http switch; the cloud tools from the agent's server; import and scan. Ported from
/// the desktop's <c>ToolsCatalog</c> without its built-in tools, assistants and Settings. It changes only what the person asked
/// for through a command of the view; an import or a scan never sets the switch or the trust mark.
/// </summary>
public sealed class McpToolsCatalog : ILocalToolsCatalog, IDisposable
{
    private readonly McpHost _host;
    private readonly IAgentToolsView? _agent;
    private readonly McpImporter _importer;
    private readonly IMcpScanner? _scanner;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConditionalWeakTable<ImportPreview, McpImportResult> _previews = new();
    private readonly ConditionalWeakTable<ScanResultInfo, McpScanResult> _scans = new();
    private readonly Dictionary<string, DateTimeOffset> _runningSince = new(StringComparer.Ordinal);
    private readonly object _sinceGate = new();

    /// <summary>The origin of a server a scan found by its answer on a port (one found in another app's settings has that app's name).</summary>
    public const string ScanOrigin = "scan";

    /// <param name="host">The person's servers.</param>
    /// <param name="agent">The running agent; none: nothing to list.</param>
    /// <param name="importer">Reads the other programs' lists; this computer's if not given.</param>
    /// <param name="scanner">The scanner; none: the page hides its Scan buttons.</param>
    /// <param name="now">The clock, for "running since"; the system's if not given.</param>
    public McpToolsCatalog(McpHost host, IAgentToolsView? agent, McpImporter? importer = null, IMcpScanner? scanner = null,
        Func<DateTimeOffset>? now = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _agent = agent;
        _scanner = scanner;
        _now = now ?? (() => DateTimeOffset.Now);
        _importer = importer ?? new McpImporter(McpImportLocations.ForThisComputer());
        _host.Changed += OnChanged;
        if (_agent is not null)
        {
            _agent.Changed += OnChanged;
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        _host.Changed -= OnChanged;
        if (_agent is not null)
        {
            _agent.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public bool SignedIn => _agent is { IsRunning: true } && _host.Store is not null;

    public ToolsPolicy Policy
    {
        get
        {
            if (_agent?.Profile is not { } profile || _host.Module is not { } module)
            {
                return ToolsPolicy.Unknown;
            }
            var decision = module.Policy.Decide(LocalMcpServer.FromTransport("policy", _ => throw new InvalidOperationException()), profile);
            return !decision.Allowed ? ToolsPolicy.NotAllowed : profile.LocalMcp.Mode == "allow" ? ToolsPolicy.Allowed : ToolsPolicy.DeveloperMode;
        }
    }

    public bool PlainHttpPermitted => _agent is { IsRunning: true } && _agent.PlainHttpPermitted;

    public bool IsGoverned => _agent is { IsRunning: true } && _agent.Connection == AgentConnection.Governed;

    /// <summary>Only the app's own tools (<c>Source == "host"</c>): not a local server's, the server's or the SDK's built-ins (<c>find_tool</c>, <c>load_skill</c>).</summary>
    public IReadOnlyList<AppToolInfo> AppTools => _agent is { IsRunning: true } agent
        ? [.. agent.Tools.Where(t => t.Source == "host").Select(t => new AppToolInfo(t.Name, t.Function.Description ?? "", t.Class))]
        : [];

    public bool MarksAreYours => _host.MarksArePersons;

    public bool ServersKeptOnDisk => _host.Store?.KeepsOnDisk ?? true;

    public bool ServersUnreadable => _host.Store?.Unreadable ?? false;

    public int RootCount => _host.CurrentRoots().Count;

    public IReadOnlyList<string> RootNames => [.. _host.CurrentRoots().Select(RootName)];

    // The folder's own name: what the person called it, else the last part of its path.
    private static string RootName(McpRoot root)
    {
        if (!string.IsNullOrWhiteSpace(root.Name))
        {
            return root.Name!;
        }
        var path = Uri.TryCreate(root.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : root.Uri;
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return name.Length > 0 ? name : path;
    }

    public IReadOnlyList<LocalServerInfo> LocalServers
    {
        get
        {
            var rows = _host.Rows;
            lock (_sinceGate)
            {
                var running = new HashSet<string>(rows.Where(r => r.State == McpServerState.Running).Select(r => r.Label), StringComparer.Ordinal);
                foreach (var gone in _runningSince.Keys.Where(k => !running.Contains(k)).ToList())
                {
                    _runningSince.Remove(gone);
                }
                foreach (var label in running)
                {
                    if (!_runningSince.ContainsKey(label))
                    {
                        _runningSince[label] = _now();
                    }
                }
                return [.. rows.Select(r => Info(r, _runningSince.TryGetValue(r.Label, out var since) ? since : null))];
            }
        }
    }

    private LocalServerInfo Info(McpServerRow row, DateTimeOffset? since)
    {
        var entry = row.Entry;
        // Whose word counts: the person's marks, or in governed mode the administrator's (never the entry's then).
        var marks = _host.MarksOf(entry) ?? new McpServerMarks(false, new Dictionary<string, ToolClass>());
        var secrets = entry.Environment.Concat(entry.Headers).ToList();
        return new LocalServerInfo
        {
            SecretNames = [.. secrets.Select(p => p.Name)],
            MissingValues = [.. secrets.Where(p => p.Value.Length == 0).Select(p => p.Name)],
            CallTimeoutSeconds = entry.CallTimeoutSeconds,
            RunningSince = since,
            Label = row.Label,
            Where = entry.Where,
            IsProgram = entry.IsProgram,
            Enabled = entry.Enabled,
            State = row.State switch
            {
                McpServerState.Running => LocalServerState.Running,
                McpServerState.Starting => LocalServerState.Starting,
                McpServerState.Failed => LocalServerState.Failed,
                McpServerState.NotAllowed => LocalServerState.NotAllowed,
                _ => LocalServerState.Off,
            },
            Detail = row.Status?.Detail,
            ToolsChanged = row.Status?.ToolsChanged == true,
            Origin = entry.Origin,
            Trusted = marks.Trusted,
            PlainHttpApplies = PlainHttpApplies(entry),
            PlainHttpInLan = entry.PlainHttpInLan,
            PlainHttpRefusal = row.Status?.PlainHttp is { } refusal ? Refusal(refusal) : null,
            Tools = [.. (row.Status?.Tools ?? []).Select(t => Tool(t, marks))],
        };
    }

    private static LocalToolInfo Tool(McpToolInfo tool, McpServerMarks marks)
    {
        ToolClass? raised = marks.ToolClasses.TryGetValue(tool.Name, out var value) ? value : null;
        var toolClass = McpToolClasses.Classify(marks.Trusted, tool.Hints, raised);
        return new LocalToolInfo(tool.Name, tool.Description, tool.Hints.Title, tool.Hints.ReadOnly, tool.Hints.Destructive, tool.Hints.Idempotent,
            tool.Hints.OpenWorld,
            toolClass switch
            {
                ToolClass.Read => LocalToolClass.Read,
                ToolClass.Write => LocalToolClass.Write,
                ToolClass.Destructive => LocalToolClass.Destructive,
                _ => LocalToolClass.Unknown,
            },
            raised switch { ToolClass.Read => LocalToolRaise.Read, ToolClass.Write => LocalToolRaise.Write, _ => LocalToolRaise.None });
    }

    private static LocalPlainHttpRefusal Refusal(McpPlainHttpRefusal refusal) => refusal switch
    {
        McpPlainHttpRefusal.NoOptIn => LocalPlainHttpRefusal.NoOptIn,
        McpPlainHttpRefusal.NotLocal => LocalPlainHttpRefusal.NotLocal,
        McpPlainHttpRefusal.Secret => LocalPlainHttpRefusal.Secret,
        _ => LocalPlainHttpRefusal.NotAllowed,
    };

    /// <summary>The switch applies to an address over plain http that is not on this computer (https and this computer need none).</summary>
    private static bool PlainHttpApplies(McpServerEntry entry) =>
        !entry.IsProgram && Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttp && !LocalMcpServer.IsLoopback(url);

    // ---- cloud --------------------------------------------------------------------------------------------------------

    public async Task<CloudToolsResult> GetCloudToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_agent is not { IsRunning: true })
        {
            return new CloudToolsResult(CloudToolsStatus.NotSignedIn, []);
        }
        try
        {
            var tools = await _agent.ListCloudToolsAsync(cancellationToken).ConfigureAwait(false);
            return tools is null
                ? new CloudToolsResult(CloudToolsStatus.NotListed, [])
                : new CloudToolsResult(CloudToolsStatus.Listed, [.. tools.Select(t => new CloudToolInfo(t.Name, t.Server, t.Description, t.Allowed, t.Enabled))]);
        }
        catch (Exception e) when (e is FadenstackException or System.Net.Http.HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new CloudToolsResult(CloudToolsStatus.Unavailable, []);
        }
    }

    // ---- local servers ------------------------------------------------------------------------------------------------

    public Task SetEnabledAsync(string label, bool enabled, CancellationToken cancellationToken = default) =>
        _host.SetEnabledAsync(label, enabled, cancellationToken);

    public Task RemoveAsync(string label, CancellationToken cancellationToken = default) => _host.RemoveAsync(label, cancellationToken);

    public void SetTrusted(string label, bool trusted)
    {
        // In governed mode the administrator's marks count: the person's would change nothing, so they are not kept.
        if (_host.MarksArePersons)
        {
            _host.SetTrusted(label, trusted);
        }
    }

    public void SetToolClass(string label, string tool, LocalToolRaise raise)
    {
        if (!_host.MarksArePersons)
        {
            return;
        }
        // A destructive hint is a floor: no raise is kept for such a tool. A tool the server does not list now has no known hint, so it is not raised either.
        var listed = _host.Rows.FirstOrDefault(r => r.Label == label)?.Status?.Tools.FirstOrDefault(t => t.Name == tool);
        if (listed is null || listed.Hints.Destructive == true)
        {
            return;
        }
        _host.SetToolClass(label, tool, raise switch { LocalToolRaise.Read => ToolClass.Read, LocalToolRaise.Write => ToolClass.Write, _ => null });
    }

    public async Task<ServerProblem> SetPlainHttpInLanAsync(string label, bool on, string? warningVersion, CancellationToken cancellationToken = default) =>
        Problem((await _host.SetPlainHttpInLanAsync(label, on, warningVersion, cancellationToken).ConfigureAwait(false)).Problem);

    public ServerDraft? GetDraft(string label)
    {
        if (_host.Store?.Find(label) is not { } e)
        {
            return null;
        }
        return new ServerDraft
        {
            Label = e.Label,
            Enabled = e.Enabled,
            IsProgram = e.IsProgram,
            Command = e.Command ?? "",
            Arguments = [.. e.Arguments],
            WorkingFolder = e.WorkingFolder ?? "",
            Environment = [.. e.Environment.Select(p => new SecretPairDraft { Name = p.Name, IsKept = true })],
            Url = e.Url ?? "",
            Headers = [.. e.Headers.Select(p => new SecretPairDraft { Name = p.Name, IsKept = true })],
            CallTimeoutSeconds = e.CallTimeoutSeconds,
        };
    }

    public ServerProblem Validate(ServerDraft draft, string? originalLabel)
    {
        var taken = (_host.Store?.Entries ?? []).Select(e => e.Label).Where(l => l != originalLabel);
        return Problem(McpServerValidator.Validate(ToEntry(draft, originalLabel), taken).Problem);
    }

    public async Task<ServerProblem> SaveAsync(string? originalLabel, ServerDraft draft, CancellationToken cancellationToken = default)
    {
        var entry = ToEntry(draft, originalLabel);
        McpValidation check;
        if (originalLabel is null)
        {
            check = await _host.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var saved = _host.Store?.Find(originalLabel);
            var taken = (_host.Store?.Entries ?? []).Select(e => e.Label).Where(l => l != originalLabel);
            if (saved is { PlainHttpInLan: true } && !entry.PlainHttpInLan && McpServerValidator.Validate(entry, taken).IsValid)
            {
                // The address changed under a switch that was on: the switch was for the old address, so it goes off, on the record.
                await _host.SetPlainHttpInLanAsync(originalLabel, false, null, cancellationToken).ConfigureAwait(false);
            }
            check = await _host.UpdateAsync(originalLabel, entry, cancellationToken).ConfigureAwait(false);
        }
        return Problem(check.Problem);
    }

    public async Task<ServerTestInfo> TestAsync(ServerDraft draft, string? originalLabel, CancellationToken cancellationToken = default)
    {
        var allowed = Policy is ToolsPolicy.Allowed or ToolsPolicy.DeveloperMode;
        var result = await _host.TestAsync(ToEntry(draft, originalLabel), allowed, cancellationToken, PlainHttpPermitted).ConfigureAwait(false);
        return new ServerTestInfo(result.NotAllowed ? TestOutcome.NotAllowed : result.Started ? TestOutcome.Started : TestOutcome.Failed, result.Tools,
            result.Error, result.PlainHttp is { } refusal ? Refusal(refusal) : null);
    }

    // The saved secret stays when the draft keeps it: the page never held it. The marks and the switch of a saved server stay too
    // (an edit is not the person's word on them); the switch only while the server's origin is the one it was turned on for.
    private McpServerEntry ToEntry(ServerDraft draft, string? originalLabel)
    {
        var saved = originalLabel is null ? null : _host.Store?.Find(originalLabel);
        McpSecretPair[] Pairs(IEnumerable<SecretPairDraft> drafts, IReadOnlyList<McpSecretPair>? old) =>
        [.. drafts.Select(p => new McpSecretPair(p.Name.Trim(), p.IsKept && p.Value.Length == 0
            ? old?.FirstOrDefault(o => o.Name == p.Name)?.Value ?? "" : p.Value))];
        var url = draft.IsProgram ? null : draft.Url.Trim();
        return new McpServerEntry
        {
            Label = draft.Label.Trim(),
            Enabled = draft.Enabled,
            Command = draft.IsProgram ? draft.Command.Trim() : null,
            Arguments = draft.IsProgram ? [.. draft.Arguments] : [],
            WorkingFolder = draft.IsProgram && draft.WorkingFolder.Trim().Length > 0 ? draft.WorkingFolder.Trim() : null,
            Environment = draft.IsProgram ? Pairs(draft.Environment, saved?.Environment) : [],
            Url = url,
            Headers = draft.IsProgram ? [] : Pairs(draft.Headers, saved?.Headers),
            CallTimeoutSeconds = draft.CallTimeoutSeconds,
            Origin = saved?.Origin ?? McpServerEntry.TypedOrigin,
            Trusted = saved?.Trusted ?? false,
            ToolClasses = saved?.ToolClasses ?? new Dictionary<string, ToolClass>(),
            PlainHttpInLan = saved is { PlainHttpInLan: true } && SameOrigin(saved.Url, url),
        };
    }

    private static bool SameOrigin(string? first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a) && Uri.TryCreate(second, UriKind.Absolute, out var b)
        && a.Scheme == Uri.UriSchemeHttp && b.Scheme == Uri.UriSchemeHttp
        && string.Equals(McpHttp.Origin(a), McpHttp.Origin(b), StringComparison.OrdinalIgnoreCase);

    private static ServerProblem Problem(McpProblem problem) => problem switch
    {
        McpProblem.None => ServerProblem.None,
        McpProblem.LabelMissing => ServerProblem.LabelMissing,
        McpProblem.LabelInvalid => ServerProblem.LabelInvalid,
        McpProblem.LabelTaken => ServerProblem.LabelTaken,
        McpProblem.CommandMissing => ServerProblem.CommandMissing,
        McpProblem.CommandInvalid => ServerProblem.CommandInvalid,
        McpProblem.FolderNotAbsolute => ServerProblem.FolderNotAbsolute,
        McpProblem.NameInvalid => ServerProblem.NameInvalid,
        McpProblem.ValueInvalid => ServerProblem.ValueInvalid,
        McpProblem.AddressMissing => ServerProblem.AddressMissing,
        McpProblem.TimeoutOutOfRange => ServerProblem.TimeoutOutOfRange,
        McpProblem.PlainHttpWithHeaders => ServerProblem.PlainHttpWithHeaders,
        McpProblem.PlainHttpNotLocal => ServerProblem.PlainHttpNotLocal,
        // AddressNotLoopback is no longer given; an address that is not valid is the nearest word.
        _ => ServerProblem.AddressInvalid,
    };

    // ---- import -------------------------------------------------------------------------------------------------------

    private static McpImportSource Source(ImportSourceKind source) => source switch
    {
        ImportSourceKind.ClaudeDesktop => McpImportSource.ClaudeDesktop,
        ImportSourceKind.VsCode => McpImportSource.VsCode,
        ImportSourceKind.Cursor => McpImportSource.Cursor,
        _ => McpImportSource.ProjectFile,
    };

    public string? FindImportFile(ImportSourceKind source) => _importer.FindFile(Source(source));

    public async Task<ImportPreview> ReadImportAsync(ImportSourceKind source, string? path, CancellationToken cancellationToken = default)
    {
        var taken = (_host.Store?.Entries ?? []).Select(e => e.Label);
        var read = await _importer.ReadAsync(Source(source), path, taken, cancellationToken).ConfigureAwait(false);
        var preview = new ImportPreview
        {
            Problem = (ImportProblem)Enum.Parse(typeof(ImportProblem), read.Problem.ToString()),
            Path = read.Path,
            Servers = [.. read.Servers.Select(s => new ImportCandidateInfo
            {
                Label = s.Entry.Label,
                OriginalName = s.OriginalName,
                Where = s.Entry.Where,
                Notes = [.. s.Notes.Select(n => (ImportNote)Enum.Parse(typeof(ImportNote), n.ToString()))],
                Detail = s.Detail,
            })],
            Skipped = [.. read.Skipped.Select(s => new ImportSkipInfo
            {
                Name = s.Name,
                Reason = (ImportSkipReason)Enum.Parse(typeof(ImportSkipReason), s.Reason.ToString()),
                Detail = s.Detail,
            })],
        };
        _previews.Add(preview, read);
        return preview;
    }

    public async Task<int> ImportAsync(ImportPreview preview, IReadOnlyList<string> labels, CancellationToken cancellationToken = default)
    {
        if (!_previews.TryGetValue(preview, out var read))
        {
            return 0;
        }
        var added = 0;
        foreach (var candidate in read.Servers.Where(s => labels.Contains(s.Entry.Label)))
        {
            if ((await _host.AddAsync(Fresh(candidate.Entry), cancellationToken).ConfigureAwait(false)).IsValid)
            {
                added++;
            }
        }
        return added;
    }

    /// <summary>An entry that came from another program's file arrives not trusted, with no raises and the plain-http switch off, whatever the file said.</summary>
    private static McpServerEntry Fresh(McpServerEntry entry) =>
        entry with { Trusted = false, ToolClasses = new Dictionary<string, ToolClass>(), PlainHttpInLan = false };

    // ---- restart ------------------------------------------------------------------------------------------------------

    public async Task RestartAsync(string label, CancellationToken cancellationToken = default)
    {
        if (_host.Store?.Find(label) is not { Enabled: true })
        {
            return;
        }
        lock (_sinceGate)
        {
            _runningSince.Remove(label);
        }
        await _host.SetEnabledAsync(label, false, cancellationToken).ConfigureAwait(false);
        await _host.SetEnabledAsync(label, true, cancellationToken).ConfigureAwait(false);
    }

    // ---- scan ---------------------------------------------------------------------------------------------------------

    public bool CanScan => _scanner is not null;

    public IReadOnlyList<string> ScanAppNames => [.. (_scanner?.Apps ?? []).Select(AppName)];

    public IReadOnlyList<PortRangeInfo> ScanPortRanges => [.. McpScanOptions.DefaultPorts.Select(r => new PortRangeInfo(r.From, r.To))];

    /// <summary>The name of an app as the sheet shows it.</summary>
    internal static string AppName(McpImportSource source) => source switch
    {
        McpImportSource.ClaudeDesktop => "Claude Desktop",
        McpImportSource.VsCode => "VS Code",
        McpImportSource.Cursor => "Cursor",
        McpImportSource.Windsurf => "Windsurf",
        McpImportSource.ClaudeCode => "Claude Code",
        McpImportSource.GeminiCli => "Gemini CLI",
        McpImportSource.LmStudio => "LM Studio",
        McpImportSource.ProjectFile => ".mcp.json",
        _ => source.ToString(),
    };

    public async Task<ScanResultInfo> ScanAsync(ScanOptionsInfo options, Action<int, int>? progress, CancellationToken cancellationToken = default)
    {
        var scanner = _scanner ?? throw new InvalidOperationException("No scanner.");
        var current = _host.Store?.Entries ?? [];
        var report = progress is null ? null : new Progress<McpScanProgress>(p => progress(p.Done, p.Total));
        var result = await scanner.ScanAsync(new McpScanOptions(options.OtherApps, options.ThisComputer), current, report, cancellationToken).ConfigureAwait(false);
        var info = new ScanResultInfo
        {
            Apps = [.. result.Apps.Select(a => new ScanAppInfo(a.Name, a.Installed, a.Found))],
            PortsChecked = result.PortsChecked,
            Seconds = result.Took.TotalSeconds,
            Findings = [.. result.Findings.Select((f, i) => new ScanFindingInfo
            {
                Id = i.ToString(CultureInfo.InvariantCulture),
                Label = f.Label,
                FoundIn = f.FoundIn,
                FromApp = f.Source is not null,
                IsProgram = f.Entry?.IsProgram ?? false,
                Where = f.Where,
                State = (ScanFindingState)Enum.Parse(typeof(ScanFindingState), f.State.ToString()),
                SecretNames = [.. f.SecretNames],
                ServerName = f.ServerName,
                ToolCount = f.ToolCount,
                Detail = f.Detail,
            })],
        };
        _scans.Add(info, result);
        return info;
    }

    public async Task<int> AddFoundAsync(ScanResultInfo result, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (!_scans.TryGetValue(result, out var scan))
        {
            return 0;
        }
        var added = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index >= scan.Findings.Count || scan.Findings[index] is not { Entry: { } entry } finding
                || finding.State is not (McpFindingState.New or McpFindingState.NeedsValue))
            {
                continue;
            }
            // A value it still needs: it comes in switched off.
            var toAdd = Fresh(entry) with
            {
                Enabled = finding.State == McpFindingState.NeedsValue ? false : entry.Enabled,
                Origin = finding.Source is null ? ScanOrigin : entry.Origin,
            };
            if ((await _host.AddAsync(toAdd, cancellationToken).ConfigureAwait(false)).IsValid)
            {
                added++;
            }
        }
        return added;
    }
}

using Fadenstack.LocalTools;
using Fadenstack.Tools;
using Fadenstack.UI.Services;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>The app's catalog as the Tools view sees it, set by the test; every change the view asks for is recorded.</summary>
internal sealed class FakeLocalToolsCatalog : ILocalToolsCatalog
{
    public bool SignedIn { get; set; } = true;

    public ToolsPolicy Policy { get; set; } = ToolsPolicy.Allowed;

    /// <summary>The agent is connected to an organisation's server; false: standalone or not known yet.</summary>
    public bool IsGoverned { get; set; } = true;

    /// <summary>The tools the app itself gives the agent.</summary>
    public List<AppToolInfo> App { get; } = new();

    public IReadOnlyList<AppToolInfo> AppTools => App;

    public bool ServersKeptOnDisk { get; set; } = true;

    public bool ServersUnreadable { get; set; }

    public bool PlainHttpPermitted { get; set; } = true;

    public bool MarksAreYours { get; set; } = true;

    public List<LocalServerInfo> Servers { get; } = new();

    public IReadOnlyList<LocalServerInfo> LocalServers => Servers;

    public int RootCount => Roots.Count;

    public List<string> Roots { get; } = new() { "Fadenstack tests" };

    public IReadOnlyList<string> RootNames => Roots;

    /// <summary>What the next cloud lists answer, in order; the last one repeats.</summary>
    public List<CloudToolsResult> CloudAnswers { get; } = new() { new CloudToolsResult(CloudToolsStatus.Listed, Array.Empty<CloudToolInfo>()) };

    public int CloudCalls { get; private set; }

    public void ResetCloud() => CloudCalls = 0;

    public List<string> Calls { get; } = new();

    public Dictionary<string, ServerDraft> Drafts { get; } = new();

    public ServerProblem ValidateAnswer { get; set; }

    public ServerProblem SaveAnswer { get; set; }

    public ServerProblem PlainHttpAnswer { get; set; }

    public ServerTestInfo TestAnswer { get; set; } = new(TestOutcome.Started, new[] { "add", "now" }, null);

    public ImportPreview PreviewAnswer { get; set; } = new();

    public ServerDraft? Saved { get; private set; }

    public string? SavedOriginal { get; private set; }

    /// <summary>What the catalog saw of the secret values at the moment of each save and test (the drafts are cleared afterwards).</summary>
    public List<string> SecretValuesSeen { get; } = new();

    /// <summary>The drafts the view gave, to look at after the call returned.</summary>
    public List<ServerDraft> DraftsGiven { get; } = new();

    public List<string> Imported { get; } = new();

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<CloudToolsResult> GetCloudToolsAsync(CancellationToken cancellationToken = default)
    {
        var answer = CloudAnswers[Math.Min(CloudCalls, CloudAnswers.Count - 1)];
        CloudCalls++;
        return Task.FromResult(answer);
    }

    public Task SetEnabledAsync(string label, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add($"enable {label} {enabled}");
        Servers.Single(s => s.Label == label).Enabled = enabled;
        return Task.CompletedTask;
    }

    public void SetTrusted(string label, bool trusted)
    {
        Calls.Add($"trust {label} {trusted}");
        Servers.Single(s => s.Label == label).Trusted = trusted;
    }

    public void SetToolClass(string label, string tool, LocalToolRaise raise) => Calls.Add($"raise {label} {tool} {raise}");

    public Task<ServerProblem> SetPlainHttpInLanAsync(string label, bool on, string? warningVersion, CancellationToken cancellationToken = default)
    {
        Calls.Add($"plain-http {label} {on} {warningVersion ?? "-"}");
        if (PlainHttpAnswer == ServerProblem.None)
        {
            Servers.Single(s => s.Label == label).PlainHttpInLan = on;
        }
        return Task.FromResult(PlainHttpAnswer);
    }

    public ServerDraft? GetDraft(string label) => Drafts.TryGetValue(label, out var draft) ? draft : null;

    public ServerProblem Validate(ServerDraft draft, string? originalLabel) => ValidateAnswer;

    public Task<ServerProblem> SaveAsync(string? originalLabel, ServerDraft draft, CancellationToken cancellationToken = default)
    {
        Calls.Add("save " + (originalLabel ?? "new"));
        DraftsGiven.Add(draft);
        SecretValuesSeen.AddRange(draft.Environment.Concat(draft.Headers).Select(p => p.Value));
        if (SaveAnswer == ServerProblem.None)
        {
            Saved = Snapshot(draft);
            SavedOriginal = originalLabel;
        }
        return Task.FromResult(SaveAnswer);
    }

    // What the catalog stored: a copy, since the view clears the draft after the call.
    private static ServerDraft Snapshot(ServerDraft draft) => new()
    {
        Label = draft.Label, Enabled = draft.Enabled, IsProgram = draft.IsProgram, Command = draft.Command, Arguments = draft.Arguments.ToList(),
        WorkingFolder = draft.WorkingFolder, Url = draft.Url, CallTimeoutSeconds = draft.CallTimeoutSeconds,
        Environment = draft.Environment.Select(p => new SecretPairDraft { Name = p.Name, Value = p.Value, IsKept = p.IsKept }).ToList(),
        Headers = draft.Headers.Select(p => new SecretPairDraft { Name = p.Name, Value = p.Value, IsKept = p.IsKept }).ToList(),
    };

    public Task RemoveAsync(string label, CancellationToken cancellationToken = default)
    {
        Calls.Add("remove " + label);
        Servers.RemoveAll(s => s.Label == label);
        return Task.CompletedTask;
    }

    public Task<ServerTestInfo> TestAsync(ServerDraft draft, string? originalLabel, CancellationToken cancellationToken = default)
    {
        Calls.Add("test " + (originalLabel ?? draft.Label));
        DraftsGiven.Add(draft);
        SecretValuesSeen.AddRange(draft.Environment.Concat(draft.Headers).Select(p => p.Value));
        return Task.FromResult(TestAnswer);
    }

    public Task<ImportPreview> ReadImportAsync(ImportSourceKind source, string? path, CancellationToken cancellationToken = default)
    {
        Calls.Add($"read {source} {path}");
        return Task.FromResult(PreviewAnswer);
    }

    public string? FindImportFile(ImportSourceKind source) => null;

    public Task<int> ImportAsync(ImportPreview preview, IReadOnlyList<string> labels, CancellationToken cancellationToken = default)
    {
        Imported.AddRange(labels);
        return Task.FromResult(labels.Count);
    }

    public Task RestartAsync(string label, CancellationToken cancellationToken = default)
    {
        Calls.Add("restart " + label);
        return Task.CompletedTask;
    }

    /// <summary>What "Use the new program" answers.</summary>
    public ServerProblem ProgramAnswer { get; set; }

    public Task<ServerProblem> ConfirmNewProgramAsync(string label, CancellationToken cancellationToken = default)
    {
        Calls.Add("use-new-program " + label);
        if (ProgramAnswer == ServerProblem.None)
        {
            var server = Servers.Single(s => s.Label == label);
            server.State = LocalServerState.Starting;
            server.ProgramChange = null;
        }
        return Task.FromResult(ProgramAnswer);
    }

    // ---- scan -----------------------------------------------------------------------------------------------------

    public bool CanScan { get; set; } = true;

    public List<string> ScanApps { get; } = new() { "Claude Desktop", "VS Code", "Cursor", "Claude Code", "Windsurf", "Gemini CLI", "LM Studio" };

    public IReadOnlyList<string> ScanAppNames => ScanApps;

    public List<PortRangeInfo> PortRanges { get; } = new() { new PortRangeInfo(3000, 3999), new PortRangeInfo(8000, 8999) };

    public IReadOnlyList<PortRangeInfo> ScanPortRanges => PortRanges;

    public ScanResultInfo ScanAnswer { get; set; } = new();

    /// <summary>When set, a scan waits for it (so a test can look at the running sheet or cancel it).</summary>
    public TaskCompletionSource<bool>? ScanGate { get; set; }

    /// <summary>When set, a scan throws it.</summary>
    public Exception? ScanFault { get; set; }

    public List<ScanOptionsInfo> ScanOptionsSeen { get; } = new();

    public List<string> AddedFound { get; } = new();

    public async Task<ScanResultInfo> ScanAsync(ScanOptionsInfo options, Action<int, int>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add($"scan apps={options.OtherApps} computer={options.ThisComputer}");
        ScanOptionsSeen.Add(options);
        progress?.Invoke(1, 4);
        if (ScanGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ScanFault is { } fault)
        {
            throw fault;
        }
        progress?.Invoke(4, 4);
        return ScanAnswer;
    }

    public Task<int> AddFoundAsync(ScanResultInfo result, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Calls.Add("add-found " + string.Join(",", ids));
        AddedFound.AddRange(ids);
        return Task.FromResult(ids.Count);
    }

    /// <summary>A scene with every state the page can show: four servers (one failed for want of a value, one off, one beyond this computer), two cloud servers.</summary>
    public static FakeLocalToolsCatalog Scene()
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.App.Add(new AppToolInfo("read_page", "Reads the page that is open in the browser.\n\n  Returns its text.", ToolClass.Read));
        catalog.App.Add(new AppToolInfo("fill_field", "Types a value into a field of the page.", ToolClass.Write));
        catalog.App.Add(new AppToolInfo("click_button", "Clicks a button of the page; a click may send a form.", ToolClass.Destructive));
        catalog.App.Add(new AppToolInfo("wait", "", ToolClass.Unknown));
        catalog.Servers.Add(new LocalServerInfo
        {
            Label = "demo", Where = "faden-mcp-demo.exe", IsProgram = true, Enabled = true, State = LocalServerState.Running, Origin = "typed",
            CallTimeoutSeconds = 120, RunningSince = new DateTimeOffset(2026, 11, 1, 17, 3, 0, TimeSpan.Zero), ProgramPinned = true,
            Tools = new[]
            {
                new LocalToolInfo("world_time", "The current time in a city.", null, true, null, null, null),
                new LocalToolInfo("allowed_folders", "Lists the folders the app told this server it may work in.", null, true, null, null, null),
                new LocalToolInfo("long_report", "Writes a demo report step by step; it can be stopped.", null, true, null, null, null),
                new LocalToolInfo("plan_meeting", "Plans a demo meeting; it asks you for the topic and length.", null, false, false, null, true),
            },
        });
        catalog.Servers.Add(new LocalServerInfo
        {
            Label = "notes-db", Where = "npx @acme/notes-mcp", IsProgram = true, Enabled = true, State = LocalServerState.Running, Origin = "Claude Desktop",
            CallTimeoutSeconds = 45, SecretNames = new[] { "NOTES_DIR" }, OnlyRunnerChecked = true,
            Tools = new[]
            {
                new LocalToolInfo("search_notes", "Searches the notes.", null, true, null, null, null),
                new LocalToolInfo("delete_note", "Deletes a note.", "Delete", false, true, false, null, LocalToolClass.Destructive),
            },
        });
        catalog.Servers.Add(new LocalServerInfo
        {
            Label = "github-local", Where = "docker run ghcr.io/github/github-mcp-server", IsProgram = true, Enabled = true, State = LocalServerState.Failed,
            Detail = "The program does not start", Origin = "VS Code", SecretNames = new[] { "GITHUB_TOKEN" }, MissingValues = new[] { "GITHUB_TOKEN" }, CallTimeoutSeconds = 120,
        });
        catalog.Servers.Add(new LocalServerInfo
        {
            Label = "calendar", Where = "127.0.0.1:8123/mcp", IsProgram = false, Enabled = false, State = LocalServerState.Off, Origin = "scan", CallTimeoutSeconds = 30,
            Tools = new[]
            {
                new LocalToolInfo("list_events", "Lists events.", null, true, null, null, null),
                new LocalToolInfo("add_event", "Adds an event.", null, false, false, null, null),
                new LocalToolInfo("remove_event", "Removes an event.", null, false, true, null, null, LocalToolClass.Destructive),
            },
        });
        catalog.CloudAnswers.Clear();
        catalog.CloudAnswers.Add(new CloudToolsResult(CloudToolsStatus.Listed, new[]
        {
            new CloudToolInfo("echo", "everything", "Echoes back the input.", true, true),
            new CloudToolInfo("add", "everything", "Adds two numbers.", true, true),
            new CloudToolInfo("getTinyImage", "everything", "Returns a small picture.", true, true),
            new CloudToolInfo("longRunningOperation", "everything", "A long task that reports progress.", true, true),
            new CloudToolInfo("sampleLLM", "everything", "Asks the model through the client.", false, true),
            new CloudToolInfo("printEnv", "everything", "Prints the server's environment.", true, false),
            new CloudToolInfo("search_repositories", "github", "Searches repositories.", true, true),
            new CloudToolInfo("create_issue", "github", "Opens an issue.", false, true),
            new CloudToolInfo("delete_repository", "github", "Deletes a repository.", false, true),
        }));
        return catalog;
    }

    /// <summary>A program whose file changed since the person added it: it does not run.</summary>
    public static LocalServerInfo ChangedProgram(LocalProgramChange change = LocalProgramChange.Hash) => new()
    {
        Label = "files", Where = "server-files.exe", IsProgram = true, Enabled = true, State = LocalServerState.ProgramChanged, Origin = "typed",
        CallTimeoutSeconds = 60, ProgramChange = change, ProgramPinned = true,
    };

    /// <summary>A server on the local network over plain http, with the switch in the state given.</summary>
    public static LocalServerInfo Nas(bool on = false, LocalServerState state = LocalServerState.Starting, LocalPlainHttpRefusal? refusal = null) => new()
    {
        Label = "nas", Where = "192.168.1.5:9/mcp", IsProgram = false, Enabled = true, State = state, Origin = "typed", CallTimeoutSeconds = 60,
        PlainHttpApplies = true, PlainHttpInLan = on, PlainHttpRefusal = refusal,
    };
}

/// <summary>The UI thread that holds what is posted until the test flushes it (a catalog raises <c>Changed</c> from any thread).</summary>
internal sealed class FakeUi : IUiDispatcher
{
    private readonly List<Action> _pending = new();

    /// <summary>The caller is not on the UI thread: everything is posted.</summary>
    public bool Hold { get; set; }

    public int Pending => _pending.Count;

    public bool CheckAccess() => !Hold;

    public void Post(Action action) => _pending.Add(action);

    public void Flush()
    {
        var run = _pending.ToList();
        _pending.Clear();
        foreach (var action in run)
        {
            action();
        }
    }
}

internal sealed class FakeFiles : IToolsFileHost
{
    public string? Answer { get; set; }

    public Task<string?> PickFolderAsync(string title, string? start, CancellationToken cancellationToken) => Task.FromResult(Answer);

    public Task<string?> PickFileAsync(string title, string? start, CancellationToken cancellationToken) => Task.FromResult(Answer);
}

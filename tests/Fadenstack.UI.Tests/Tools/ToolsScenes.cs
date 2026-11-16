using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Fadenstack.WebSearch;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>A Tools page over the fake catalog in one of the states of <see cref="ToolsScenes"/>.</summary>
internal sealed class ToolsScene : IDisposable
{
    public ToolsScene(ToolsViewModel page, FakeLocalToolsCatalog catalog, FakeFiles files)
    {
        Page = page;
        Catalog = catalog;
        Files = files;
    }

    public ToolsViewModel Page { get; }

    public FakeLocalToolsCatalog Catalog { get; }

    public FakeFiles Files { get; }

    /// <summary>Set by the scan sheet's states that wait: completes the scan.</summary>
    public TaskCompletionSource<bool>? ScanGate { get; init; }

    /// <summary>The person's web search settings the page was given (the states with web search); the fixture deletes its folder with the scene.</summary>
    public WebSearchFixture? WebSearch { get; init; }

    public void Dispose()
    {
        Page.Dispose();
        WebSearch?.Dispose();
    }
}

/// <summary>
/// The states of the Tools view the skins draw and check (the styling pass): built from the view model tests' fakes, the way a person gets
/// to them (a click on a card, on Add, on Scan), so the view shows what the view model shows. Every command a state runs completes at once
/// (the fakes answer at once), except the scan that waits for its gate.
/// </summary>
internal static class ToolsScenes
{
    /// <summary>The names of the states, in the order the checks and the renders take them.</summary>
    public static readonly string[] States =
    {
        "empty", "sign-in", "list", "refusals", "banner", "filtered",
        "drawer", "drawer-tools", "drawer-advanced", "drawer-warning", "drawer-governed", "drawer-remove", "drawer-program",
        "add-program", "add-address", "edit-kept", "edit-address", "import", "scan-running", "scan-done",
        "cloud", "cloud-unavailable", "app", "app-standalone", "standalone",
        "app-websearch", "websearch", "websearch-kept", "websearch-refused",
    };

    public static ToolsScene Create(string state, string language = "en")
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        var files = new FakeFiles();
        TaskCompletionSource<bool>? gate = null;
        var strings = UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language));
        var web = state.StartsWith("websearch", StringComparison.Ordinal) || state == "app-websearch" ? new WebSearchFixture() : null;
        if (state == "websearch-kept")
        {
            web!.WithBrave();
        }

        switch (state)
        {
            case "empty":
                catalog.Servers.Clear();
                break;
            case "sign-in":
                catalog.SignedIn = false;
                break;
            case "refusals":
                catalog.Servers.Clear();
                catalog.Servers.Add(Nas("nas-opt-in", LocalPlainHttpRefusal.NoOptIn));
                catalog.Servers.Add(Nas("nas-not-local", LocalPlainHttpRefusal.NotLocal));
                catalog.Servers.Add(Nas("nas-secret", LocalPlainHttpRefusal.Secret));
                catalog.Servers.Add(Nas("nas-policy", LocalPlainHttpRefusal.NotAllowed));
                catalog.Servers.Add(new LocalServerInfo
                {
                    Label = "blocked", Where = "faden-mcp-demo.exe", IsProgram = true, Enabled = true, State = LocalServerState.NotAllowed, Origin = "typed",
                    CallTimeoutSeconds = 120,
                });
                break;
            case "banner":
                catalog.Policy = ToolsPolicy.NotAllowed;
                catalog.ServersKeptOnDisk = false;
                foreach (var server in catalog.Servers)
                {
                    server.State = LocalServerState.NotAllowed;
                }
                break;
            case "filtered":
                break;
            case "drawer" or "drawer-remove":
                catalog.Servers[0].Trusted = true;
                break;
            case "drawer-tools" or "drawer-governed":
                catalog.Servers.Insert(0, Files());
                catalog.MarksAreYours = state != "drawer-governed";
                break;
            case "drawer-program":
                catalog.Servers.Insert(0, FakeLocalToolsCatalog.ChangedProgram());
                break;
            case "drawer-advanced" or "drawer-warning":
                catalog.Servers.Insert(0, Nas("nas", null, on: false));
                break;
            case "edit-kept":
                catalog.Drafts["github-local"] = new ServerDraft
                {
                    Label = "github-local", Enabled = true, IsProgram = true, Command = "docker",
                    Arguments = new List<string> { "run", "-i", "--rm", "ghcr.io/github/github-mcp-server" },
                    WorkingFolder = "C:\\Work",
                    Environment = new List<SecretPairDraft> { new() { Name = "GITHUB_TOKEN", IsKept = true }, new() { Name = "REGION", Value = "" } },
                    CallTimeoutSeconds = 120,
                };
                break;
            case "edit-address":
                catalog.Drafts["calendar"] = new ServerDraft
                {
                    Label = "calendar", Enabled = false, IsProgram = false, Url = "http://127.0.0.1:8123/mcp",
                    Headers = new List<SecretPairDraft> { new() { Name = "Authorization", IsKept = true } },
                    CallTimeoutSeconds = 30,
                };
                break;
            case "import":
                catalog.PreviewAnswer = new ImportPreview
                {
                    Path = "C:\\Users\\you\\AppData\\Roaming\\Claude\\claude_desktop_config.json",
                    Servers = new[]
                    {
                        new ImportCandidateInfo { Label = "filesystem", OriginalName = "filesystem", Where = "npx -y @modelcontextprotocol/server-filesystem", Notes = new[] { ImportNote.WasOff } },
                        new ImportCandidateInfo { Label = "github", OriginalName = "github", Where = "docker run -i ghcr.io/github/github-mcp-server", Notes = new[] { ImportNote.NeedsValue, ImportNote.Renamed } },
                        new ImportCandidateInfo { Label = "memory", OriginalName = "memory", Where = "npx -y @modelcontextprotocol/server-memory", Notes = new[] { ImportNote.IgnoredFields }, Detail = "autoApprove" },
                    },
                    Skipped = new[]
                    {
                        new ImportSkipInfo { Name = "linear", Reason = ImportSkipReason.NetworkServer },
                        new ImportSkipInfo { Name = "broken", Reason = ImportSkipReason.NotUnderstood },
                    },
                };
                break;
            case "scan-running":
                gate = new TaskCompletionSource<bool>();
                catalog.ScanGate = gate;
                break;
            case "scan-done":
                catalog.ScanAnswer = ScanAnswer();
                break;
            case "app-standalone" or "standalone":
                catalog.IsGoverned = false;
                break;
            case "cloud-unavailable":
                catalog.CloudAnswers.Clear();
                catalog.CloudAnswers.Add(new CloudToolsResult(CloudToolsStatus.Unavailable, Array.Empty<CloudToolInfo>()));
                break;
        }

        if (state == "cloud")
        {
            catalog.CloudAnswers[0] = new CloudToolsResult(CloudToolsStatus.Listed, catalog.CloudAnswers[0].Tools.Append(
                new CloudToolInfo("delete_everything", "github", "Deletes the organisation's repositories.", true, false)).ToList());
        }

        var page = new ToolsViewModel(catalog, files, strings: () => strings, webSearch: web?.Store);
        page.OpenAsync().GetAwaiter().GetResult();
        var scene = new ToolsScene(page, catalog, files) { ScanGate = gate, WebSearch = web };

        switch (state)
        {
            case "filtered":
                page.Filter = "zzz";
                break;
            case "drawer":
                page.Servers[0].OpenCommand.Execute(null);
                break;
            case "drawer-remove":
                page.Servers[0].OpenCommand.Execute(null);
                page.Details!.RemoveCommand.Execute(null);
                page.Details.TestText = "Started. Tools: world_time, allowed_folders";
                break;
            case "drawer-tools" or "drawer-governed":
                page.Servers[0].OpenCommand.Execute(null);
                break;
            case "drawer-advanced" or "drawer-program":
                page.Servers[0].OpenCommand.Execute(null);
                break;
            case "drawer-warning":
                page.Servers[0].OpenCommand.Execute(null);
                page.Details!.PlainHttpOn = true;
                break;
            case "add-program":
                page.AddServerCommand.Execute(null);
                break;
            case "add-address":
            {
                page.AddServerCommand.Execute(null);
                var sheet = (ServerSheetViewModel)page.Sheet!;
                sheet.Label = "nas";
                sheet.IsAddress = true;
                sheet.Url = "https://notes.example.org/mcp";
                sheet.AddHeaderCommand.Execute(null);
                sheet.HeaderRows[0].Name = "Authorization";
                sheet.HeaderRows[0].Value = "Bearer 0123456789";
                sheet.AddHeaderCommand.Execute(null);
                sheet.TestText = "Started. Tools: add, now";
                break;
            }
            case "edit-kept":
                page.Servers[2].FixCommand.Execute(null);
                break;
            case "edit-address":
                page.Servers[3].OpenCommand.Execute(null);
                page.Details!.EditCommand.Execute(null);
                break;
            case "import":
                page.ImportServersCommand.Execute(null);
                ((ImportSheetViewModel)page.Sheet!).ReadClaudeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                break;
            case "scan-running":
                page.ScanCommand.Execute(null);
                break;
            case "scan-done":
                page.ScanCommand.Execute(null);
                break;
            case "cloud" or "cloud-unavailable":
                page.ShowsServer = true;
                break;
            case "app" or "app-standalone" or "app-websearch":
                page.ShowsApp = true;
                break;
            case "websearch" or "websearch-kept":
                page.ShowsApp = true;
                page.OpenWebSearchCommand.Execute(null);
                break;
            case "websearch-refused":
            {
                page.ShowsApp = true;
                page.OpenWebSearchCommand.Execute(null);
                var sheet = (WebSearchSettingsViewModel)page.Sheet!;
                sheet.IsSearXng = true;
                sheet.Address = "http://search.example.org";
                sheet.SaveCommand.Execute(null);
                break;
            }
        }
        return scene;
    }

    private static LocalServerInfo Nas(string label, LocalPlainHttpRefusal? refusal, bool on = true) => new()
    {
        Label = label, Where = "192.168.1.5:9/mcp", IsProgram = false, Enabled = true, State = LocalServerState.Starting, Origin = "typed",
        CallTimeoutSeconds = 60, PlainHttpApplies = true, PlainHttpInLan = on, PlainHttpRefusal = refusal,
    };

    /// <summary>A running server with a tool of each class: read, write (raised), destructive, one raised to read and one not marked.</summary>
    private static LocalServerInfo Files() => new()
    {
        Label = "files", Where = "faden-files.exe", IsProgram = true, Enabled = true, State = LocalServerState.Running, Origin = "Claude Desktop",
        CallTimeoutSeconds = 90, Trusted = true, SecretNames = new[] { "FILES_ROOT" },
        RunningSince = new DateTimeOffset(2026, 11, 1, 17, 3, 0, TimeSpan.Zero),
        Tools = new[]
        {
            new LocalToolInfo("read_file", "Reads a file and returns its text.", null, true, null, true, null, LocalToolClass.Read),
            new LocalToolInfo("write_file", "Writes a file; replaces what is there.", null, false, false, null, null, LocalToolClass.Write, LocalToolRaise.Write),
            new LocalToolInfo("delete_file", "Deletes a file for good.", "Delete", false, true, false, null, LocalToolClass.Destructive),
            new LocalToolInfo("list_folder", "Lists a folder.", null, null, null, null, null, LocalToolClass.Read, LocalToolRaise.Read),
            new LocalToolInfo("search", "Searches the files by text, also on the network.", null, null, null, null, true),
        },
    };

    private static ScanResultInfo ScanAnswer() => new()
    {
        Seconds = 4,
        PortsChecked = 2000,
        Apps = new[]
        {
            new ScanAppInfo("Claude Desktop", true, 2), new ScanAppInfo("VS Code", true, 1), new ScanAppInfo("Cursor", true, 3), new ScanAppInfo("Claude Code", true, 0),
            new ScanAppInfo("Windsurf", false, 0), new ScanAppInfo("Gemini CLI", false, 0), new ScanAppInfo("LM Studio", false, 0),
        },
        Findings = new[]
        {
            new ScanFindingInfo { Id = "0", Label = "notes-db", FoundIn = "Claude Desktop", FromApp = true, IsProgram = true, Where = "npx @acme/notes-mcp", State = ScanFindingState.AlreadyAdded },
            new ScanFindingInfo
            {
                Id = "1", Label = "github", FoundIn = "Claude Desktop", FromApp = true, IsProgram = true, Where = "docker run -i ghcr.io/github/github-mcp-server",
                State = ScanFindingState.New, SecretNames = new[] { "GITHUB_TOKEN" },
            },
            new ScanFindingInfo { Id = "2", Label = "playwright", FoundIn = "VS Code", FromApp = true, IsProgram = true, Where = "npx @playwright/mcp@latest", State = ScanFindingState.New },
            new ScanFindingInfo { Id = "3", Label = "linear", FoundIn = "Cursor", FromApp = true, IsProgram = false, Where = "https://mcp.linear.app/sse", State = ScanFindingState.NetworkLater },
            new ScanFindingInfo { Id = "5", Label = "needs", FoundIn = "Cursor", FromApp = true, IsProgram = true, Where = "node x.js", State = ScanFindingState.NeedsValue, SecretNames = new[] { "API_KEY", "OTHER" } },
            new ScanFindingInfo { Id = "6", Label = "odd", FoundIn = "Cursor", FromApp = true, IsProgram = false, Where = "?", State = ScanFindingState.NotUnderstood, Detail = "It has neither a program nor an address." },
            new ScanFindingInfo
            {
                Id = "4", Label = "calendar", FoundIn = "", FromApp = false, IsProgram = false, Where = "http://127.0.0.1:8123/mcp", State = ScanFindingState.New,
                ServerName = "calendar-server 1.2", ToolCount = 3,
            },
        },
    };
}

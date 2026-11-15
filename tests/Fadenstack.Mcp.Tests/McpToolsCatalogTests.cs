using System.Net;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.LocalTools;
using Fadenstack.Mcp;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// The Tools view's service over the real pieces (sdk/51 F05; the desktop's catalog tests, without the built-in tools and the
/// assistants): the person's servers from the host, the cloud tools from the gateway's list route, import and scan, and what F05
/// added: the trust mark, a tool's class and raise, the plain-http switch and its refusals. No test touches the network: servers
/// are in this process, and a plain-http address on the local network is refused before any connection.
/// </summary>
public sealed class McpToolsCatalogTests : IDisposable
{
    private const string Allow = """{"mode":"allow","developer_mode":false,"plain_http":"allow"}""";
    private const string AllowWithoutPlainHttp = """{"mode":"allow","developer_mode":false}""";
    private const string Warning = "2026-11-15";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-toolscat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    private sealed class Scene : IDisposable
    {
        public ScriptedGateway Gateway { get; init; } = null!;

        public McpHost Host { get; init; } = null!;

        public FadenstackAgent Agent { get; init; } = null!;

        public McpToolsCatalog Catalog { get; init; } = null!;

        public void Dispose()
        {
            Catalog.Dispose();
            Agent.Dispose();
            Host.Dispose();
        }
    }

    private static McpRig HintedRig() => new()
    {
        Tools =
        {
            McpRig.Tool("look", "Looks.", () => "seen", readOnly: true),
            McpRig.Tool("wipe", "Wipes.", () => "gone", destructive: true),
            McpRig.Tool("plain", "Plain.", () => "ok"),
        },
    };

    private async Task<Scene> StartAsync(string localMcp = Allow, McpRig? rig = null, IMcpScanner? scanner = null, Func<DateTimeOffset>? now = null,
        IEnumerable<McpServerEntry>? more = null, MachinePolicy? policy = null, Func<McpServerEntry, McpServerMarks?>? marks = null,
        Action<AgentBuilder>? configure = null)
    {
        var store = new McpServerStore(Path.Combine(_root, "user"), new Reversing());
        store.Add(McpStoreTests.Program("alpha"));
        foreach (var entry in more ?? [])
            store.Add(entry);
        var served = rig ?? new McpRig();
        // A plain-http address beyond this computer goes through the real connection rules (refused before any connection).
        var host = new McpHost(_ => store, server: e => IsLan(e) ? McpHost.ToServer(e, null) : served.Server(e.Label), marks: marks);
        var gateway = new ScriptedGateway("[]", localMcp);
        var builder = gateway.Builder().AddLocalMcp(host.NewModule());
        if (policy is not null)
            builder.UseMachinePolicy(policy);
        configure?.Invoke(builder);
        var agent = builder.Build();
        await host.UseUserAsync("anna");
        await host.Loaded;
        await agent.StartAsync();
        var catalog = new McpToolsCatalog(host, new FadenstackAgentToolsView(agent), scanner: scanner, now: now);
        return new Scene { Gateway = gateway, Host = host, Agent = agent, Catalog = catalog };
    }

    private static bool IsLan(McpServerEntry e) =>
        !e.IsProgram && Uri.TryCreate(e.Url, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttp && !LocalMcpServer.IsLoopback(url);

    private static McpServerEntry Nas(bool lan = false, string url = "http://192.168.1.5:9/mcp") => new()
    {
        Label = "nas",
        Url = url,
        PlainHttpInLan = lan,
    };

    [Theory]
    [InlineData("""{"mode":"allow","developer_mode":false}""", ToolsPolicy.Allowed)]
    [InlineData("""{"mode":"disable","developer_mode":false}""", ToolsPolicy.NotAllowed)]
    [InlineData("""{"mode":"disable","developer_mode":true}""", ToolsPolicy.DeveloperMode)]
    public async Task The_policy_is_what_the_agents_profile_says(string localMcp, ToolsPolicy policy)
    {
        using var scene = await StartAsync(localMcp);

        Assert.Equal(policy, scene.Catalog.Policy);
        Assert.True(scene.Catalog.SignedIn);
    }

    [Fact]
    public async Task Without_a_running_agent_nobody_is_signed_in_and_nothing_is_listed()
    {
        var host = new McpHost();
        using var catalog = new McpToolsCatalog(host, null);

        Assert.False(catalog.SignedIn);
        Assert.Equal(ToolsPolicy.Unknown, catalog.Policy);
        Assert.Empty(catalog.LocalServers);
        Assert.False(catalog.PlainHttpPermitted);
        Assert.Equal(CloudToolsStatus.NotSignedIn, (await catalog.GetCloudToolsAsync()).Status);
        host.Dispose();
    }

    [Fact]
    public async Task Local_servers_come_with_their_state_origin_and_never_a_secret()
    {
        using var scene = await StartAsync(Allow, rig: new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b, readOnly: true) } });

        var server = Assert.Single(scene.Catalog.LocalServers);

        Assert.Equal(("alpha", "node", LocalServerState.Running, "Cursor"), (server.Label, server.Where, server.State, server.Origin));
        var tool = Assert.Single(server.Tools);
        Assert.Equal(("add", true), (tool.Name, tool.ReadOnly));
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(scene.Catalog.LocalServers));
        Assert.Equal(0, scene.Catalog.RootCount);
        Assert.True(scene.Catalog.ServersKeptOnDisk);
        Assert.False(scene.Catalog.ServersUnreadable);
    }

    [Fact]
    public async Task Where_the_policy_does_not_allow_servers_they_are_listed_as_not_allowed()
    {
        using var scene = await StartAsync("""{"mode":"disable","developer_mode":false}""");

        Assert.Equal(LocalServerState.NotAllowed, Assert.Single(scene.Catalog.LocalServers).State);
        var test = await scene.Catalog.TestAsync(scene.Catalog.GetDraft("alpha")!, "alpha");
        Assert.Equal(TestOutcome.NotAllowed, test.Outcome);
    }

    [Fact]
    public async Task A_draft_of_a_saved_server_holds_names_but_never_values_and_saving_it_keeps_the_saved_secret()
    {
        using var scene = await StartAsync();
        var draft = scene.Catalog.GetDraft("alpha")!;

        Assert.Equal(("API_KEY", "", true), (draft.Environment[0].Name, draft.Environment[0].Value, draft.Environment[0].IsKept));
        draft.CallTimeoutSeconds = 45;
        Assert.Equal(ServerProblem.None, await scene.Catalog.SaveAsync("alpha", draft));

        var saved = scene.Host.Store!.Find("alpha")!;
        Assert.Equal(("s3cret-key-123", 45, "Cursor"), (saved.Environment[0].Value, saved.CallTimeoutSeconds, saved.Origin));
        draft.Environment[0].IsKept = false;
        draft.Environment[0].Value = "new";
        await scene.Catalog.SaveAsync("alpha", draft);
        Assert.Equal("new", scene.Host.Store.Find("alpha")!.Environment[0].Value);
    }

    [Fact]
    public async Task The_checks_take_https_anywhere_and_name_the_plain_http_problems()
    {
        using var scene = await StartAsync();
        var draft = new ServerDraft { Label = "net", IsProgram = false, Url = "https://mcp.example.com/mcp" };

        Assert.Equal(ServerProblem.None, scene.Catalog.Validate(draft, null));
        draft.Headers.Add(new SecretPairDraft { Name = "Authorization", Value = "Bearer x" });
        Assert.Equal(ServerProblem.None, scene.Catalog.Validate(draft, null)); // https with a header is fine

        draft.Url = "http://192.168.1.5:9/mcp";
        Assert.Equal(ServerProblem.PlainHttpWithHeaders, scene.Catalog.Validate(draft, null));
        draft.Headers.Clear();
        Assert.Equal(ServerProblem.None, scene.Catalog.Validate(draft, null)); // kept: it waits for the switch
        draft.Url = "http://8.8.8.8/mcp";
        Assert.Equal(ServerProblem.PlainHttpNotLocal, scene.Catalog.Validate(draft, null));
        Assert.Equal(ServerProblem.PlainHttpNotLocal, await scene.Catalog.SaveAsync(null, draft));
        Assert.DoesNotContain(scene.Catalog.LocalServers, s => s.Label == "net");

        draft.Url = "http://localhost:9000/mcp";
        draft.Label = "alpha";
        Assert.Equal(ServerProblem.LabelTaken, scene.Catalog.Validate(draft, null));
        Assert.Equal(ServerProblem.None, scene.Catalog.Validate(draft, "alpha"));
        draft.Url = "nonsense";
        Assert.Equal(ServerProblem.AddressInvalid, scene.Catalog.Validate(draft, "alpha"));
    }

    [Fact]
    public async Task The_switch_and_remove_go_through_to_the_store_and_the_module()
    {
        using var scene = await StartAsync();

        await scene.Catalog.SetEnabledAsync("alpha", false);
        Assert.Equal(LocalServerState.Off, scene.Catalog.LocalServers[0].State);
        Assert.False(scene.Catalog.LocalServers[0].Enabled);
        await scene.Catalog.RemoveAsync("alpha");
        Assert.Empty(scene.Catalog.LocalServers);
    }

    [Fact]
    public async Task The_apps_tools_are_the_host_tools_of_a_running_agent_and_nothing_else()
    {
        using var scene = await StartAsync(rig: HintedRig(), configure: b => b
            .AddTool(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "app_write", "Changes the app."), ToolClass.Write)
            .AddTool(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "app_read", "Reads the app."), ToolClass.Read));

        var app = scene.Catalog.AppTools;

        Assert.Equal([("app_read", "Reads the app."), ("app_write", "Changes the app.")],
            app.Select(t => (t.Name, t.Description)).OrderBy(t => t.Name, StringComparer.Ordinal));
        // The class is the agent's effective one (the profile's tool rules may make it stricter than the app said).
        Assert.All(app, t => Assert.Equal(scene.Agent.Tools.Single(x => x.Name == t.Name).Class, t.Class));
        // The local server's tools and the SDK's own find_tool are on the agent but are not the app's.
        Assert.Contains(scene.Agent.Tools, t => t.Source == "mcp");
        Assert.DoesNotContain(app, t => t.Name is "look" or "wipe" or "plain" or "find_tool");
    }

    [Fact]
    public async Task Without_a_running_agent_the_app_has_no_tools_and_the_connection_is_not_governed()
    {
        var host = new McpHost();
        using var none = new McpToolsCatalog(host, null);
        Assert.Empty(none.AppTools);
        Assert.False(none.IsGoverned);

        var agent = new ScriptedGateway("[]", Allow).Builder().Build();
        using var notStarted = new McpToolsCatalog(host, new FadenstackAgentToolsView(agent));
        Assert.Empty(notStarted.AppTools);
        Assert.False(notStarted.IsGoverned);
        agent.Dispose();
        host.Dispose();
    }

    [Fact]
    public async Task The_catalog_is_governed_for_a_server_and_not_for_the_persons_own_endpoint()
    {
        using (var scene = await StartAsync())
        {
            Assert.True(scene.Catalog.IsGoverned);
        }

        var host = new McpHost();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseChatClient(new Fadenstack.Endpoints.DirectChatClient(new Fadenstack.Endpoints.ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m" }))
            .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant","version":{"instructions":"Hi."}}"""))
            .UseSessionStore(new Fadenstack.Agents.Sessions.MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .Build();
        await agent.StartAsync();
        using var catalog = new McpToolsCatalog(host, new FadenstackAgentToolsView(agent));

        Assert.False(catalog.IsGoverned);
        host.Dispose();
    }

    [Fact]
    public async Task Cloud_tools_come_from_the_gateways_list_a_missing_route_is_not_listed_and_an_error_is_unavailable()
    {
        using var scene = await StartAsync();
        scene.Gateway.Tools = (HttpStatusCode.OK, """
            {"tools":[{"name":"mcp.time.now","server":"time","description":"The time.","allowed":true,"enabled":true},
                      {"name":"mcp.git.push","server":"git","description":"Pushes.","allowed":false,"enabled":false}]}
            """);

        var listed = await scene.Catalog.GetCloudToolsAsync();
        Assert.Equal(CloudToolsStatus.Listed, listed.Status);
        Assert.Equal([("mcp.time.now", true, true), ("mcp.git.push", false, false)], listed.Tools.Select(t => (t.Name, t.Allowed, t.Enabled)));

        scene.Gateway.Tools = (HttpStatusCode.NotFound, """{"detail":"Not Found"}""");
        Assert.Equal(CloudToolsStatus.NotListed, (await scene.Catalog.GetCloudToolsAsync()).Status);

        scene.Gateway.Tools = (HttpStatusCode.ServiceUnavailable, """{"error":{"message":"down","type":"mcp_unavailable","code":"mcp_unavailable"}}""");
        Assert.Equal(CloudToolsStatus.Unavailable, (await scene.Catalog.GetCloudToolsAsync()).Status);
        scene.Gateway.Tools = (HttpStatusCode.Forbidden, """{"error":{"message":"no","type":"permission_error","code":"forbidden"}}""");
        Assert.Equal(CloudToolsStatus.Unavailable, (await scene.Catalog.GetCloudToolsAsync()).Status);
    }

    [Fact]
    public async Task The_page_hears_of_a_change_of_a_server()
    {
        using var scene = await StartAsync();
        var raised = 0;
        scene.Catalog.Changed += (_, _) => Interlocked.Increment(ref raised);

        await scene.Catalog.SetEnabledAsync("alpha", false);

        Assert.True(raised > 0);
    }

    [Fact]
    public async Task A_server_comes_with_the_names_of_its_secrets_its_longest_call_and_what_has_no_value_never_a_value()
    {
        using var scene = await StartAsync(more: [new McpServerEntry
        {
            Label = "github", Command = "docker", Environment = [new McpSecretPair("GITHUB_TOKEN", ""), new McpSecretPair("REGION", "eu")],
            Headers = [], Origin = "VS Code", CallTimeoutSeconds = 30, Enabled = false,
        }]);

        var servers = scene.Catalog.LocalServers.ToDictionary(s => s.Label);

        Assert.Equal(["API_KEY"], servers["alpha"].SecretNames);
        Assert.Empty(servers["alpha"].MissingValues);
        Assert.Equal(90, servers["alpha"].CallTimeoutSeconds);
        Assert.Equal(["GITHUB_TOKEN", "REGION"], servers["github"].SecretNames);
        Assert.Equal(["GITHUB_TOKEN"], servers["github"].MissingValues);
        Assert.Equal(30, servers["github"].CallTimeoutSeconds);
        var all = System.Text.Json.JsonSerializer.Serialize(scene.Catalog.LocalServers);
        Assert.DoesNotContain("s3cret", all);
        Assert.DoesNotContain("\"eu\"", all);
    }

    [Fact]
    public async Task A_running_server_is_stamped_with_when_it_was_first_seen_running_and_a_stop_clears_it()
    {
        var clock = new DateTimeOffset(2026, 11, 1, 17, 3, 0, TimeSpan.Zero);
        using var scene = await StartAsync(now: () => clock);

        Assert.Equal(clock, scene.Catalog.LocalServers[0].RunningSince);
        clock = clock.AddMinutes(5);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 17, 3, 0, TimeSpan.Zero), scene.Catalog.LocalServers[0].RunningSince); // the first sight stays

        await scene.Catalog.SetEnabledAsync("alpha", false);
        Assert.Null(scene.Catalog.LocalServers[0].RunningSince);

        await scene.Catalog.SetEnabledAsync("alpha", true);
        Assert.Equal(clock, scene.Catalog.LocalServers[0].RunningSince);
    }

    [Fact]
    public async Task Restart_stops_the_server_and_starts_it_again_and_leaves_one_that_is_off_alone()
    {
        using var scene = await StartAsync();
        var raised = 0;
        scene.Catalog.Changed += (_, _) => Interlocked.Increment(ref raised);

        await scene.Catalog.RestartAsync("alpha");

        Assert.Equal((LocalServerState.Running, true), (scene.Catalog.LocalServers[0].State, scene.Catalog.LocalServers[0].Enabled));
        Assert.True(raised > 0);

        await scene.Catalog.SetEnabledAsync("alpha", false);
        await scene.Catalog.RestartAsync("alpha");
        Assert.Equal((LocalServerState.Off, false), (scene.Catalog.LocalServers[0].State, scene.Catalog.LocalServers[0].Enabled));
    }

    [Fact]
    public void The_folders_told_to_servers_are_named()
    {
        var folders = new List<string>();
        var source = new McpRootsSource(() => [.. folders]);
        var store = new McpServerStore(Path.Combine(_root, "user"), new Reversing());
        using var host = new McpHost(_ => store, source);
        using var catalog = new McpToolsCatalog(host, null);
        Assert.Empty(catalog.RootNames);

        folders.Add(Path.Combine(_root, "Offers"));
        folders.Add(Path.Combine(_root, "My Invoices"));

        Assert.Equal(["Offers", "My Invoices"], catalog.RootNames);
        Assert.Equal(2, catalog.RootCount);
    }

    // ---- the scan ------------------------------------------------------------------------------------------------------

    private sealed class FakeScanner(McpScanResult result) : IMcpScanner
    {
        public IReadOnlyList<McpImportSource> Apps { get; } = [McpImportSource.ClaudeDesktop, McpImportSource.VsCode, McpImportSource.LmStudio];

        public McpScanOptions? Options { get; private set; }

        public IReadOnlyList<McpServerEntry> Current { get; private set; } = [];

        public Task<McpScanResult> ScanAsync(McpScanOptions options, IReadOnlyList<McpServerEntry> current, IProgress<McpScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            Options = options;
            Current = current;
            progress?.Report(new McpScanProgress(2, 5));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private static McpScanResult SomeFindings() => new(
        [new McpScanApp(McpImportSource.ClaudeDesktop, "Claude Desktop", true, 3), new McpScanApp(McpImportSource.LmStudio, "LM Studio", false, 0)],
        2000,
        [
            new McpFinding("github", "Claude Desktop", McpImportSource.ClaudeDesktop,
                new McpServerEntry { Label = "github", Command = "docker", Environment = [new McpSecretPair("GITHUB_TOKEN", "ghp_VERYSECRET")], Origin = "Claude Desktop",
                    Trusted = true, PlainHttpInLan = true },
                McpFindingState.New, "docker run github", ["GITHUB_TOKEN"]),
            new McpFinding("notes", "Claude Desktop", McpImportSource.ClaudeDesktop,
                new McpServerEntry { Label = "notes", Command = "node", Origin = "Claude Desktop" }, McpFindingState.AlreadyAdded, "node", []),
            new McpFinding("needs", "Claude Desktop", McpImportSource.ClaudeDesktop,
                new McpServerEntry { Label = "needs", Command = "python", Environment = [new McpSecretPair("KEY", "")], Origin = "Claude Desktop", Enabled = true },
                McpFindingState.NeedsValue, "python", ["KEY"]),
            new McpFinding("linear", "Claude Desktop", McpImportSource.ClaudeDesktop, null, McpFindingState.NetworkLater, "https://mcp.linear.app/sse", []),
            new McpFinding("odd", "Claude Desktop", McpImportSource.ClaudeDesktop, null, McpFindingState.NotUnderstood, "", [], Detail: "no program"),
            new McpFinding("calendar", "", null, new McpServerEntry { Label = "calendar", Url = "http://127.0.0.1:8123/mcp", Origin = "x" },
                McpFindingState.New, "http://127.0.0.1:8123/mcp", [], ServerName: "calendar-server 1.2", ToolCount: 3),
        ],
        TimeSpan.FromSeconds(4.2));

    [Fact]
    public async Task Without_a_scanner_the_catalog_cannot_scan_and_says_so()
    {
        using var scene = await StartAsync();

        Assert.False(scene.Catalog.CanScan);
        Assert.Empty(scene.Catalog.ScanAppNames);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scene.Catalog.ScanAsync(new ScanOptionsInfo(), null));
    }

    [Fact]
    public async Task A_scan_goes_to_the_scanner_with_the_persons_servers_and_comes_back_with_names_only()
    {
        var scanner = new FakeScanner(SomeFindings());
        using var scene = await StartAsync(scanner: scanner);
        var steps = new List<(int, int)>();

        Assert.True(scene.Catalog.CanScan);
        Assert.Equal(["Claude Desktop", "VS Code", "LM Studio"], scene.Catalog.ScanAppNames);
        Assert.Equal([(3000, 3999), (8000, 8999)], scene.Catalog.ScanPortRanges.Select(r => (r.From, r.To)));

        var result = await scene.Catalog.ScanAsync(new ScanOptionsInfo { OtherApps = true, ThisComputer = false }, (d, t) => steps.Add((d, t)));

        Assert.Equal((true, false), (scanner.Options!.OtherApps, scanner.Options.ThisComputer));
        Assert.Equal(["alpha"], scanner.Current.Select(e => e.Label));
        await Task.Delay(50);
        Assert.Equal([(2, 5)], steps);
        Assert.Equal([("Claude Desktop", true, 3), ("LM Studio", false, 0)], result.Apps.Select(a => (a.Name, a.Installed, a.Found)));
        Assert.Equal((2000, 4.2), (result.PortsChecked, Math.Round(result.Seconds, 1)));
        Assert.Equal(["github", "notes", "needs", "linear", "odd", "calendar"], result.Findings.Select(f => f.Label));
        Assert.Equal(["0", "1", "2", "3", "4", "5"], result.Findings.Select(f => f.Id));
        Assert.Equal([ScanFindingState.New, ScanFindingState.AlreadyAdded, ScanFindingState.NeedsValue, ScanFindingState.NetworkLater, ScanFindingState.NotUnderstood, ScanFindingState.New],
            result.Findings.Select(f => f.State));
        var github = result.Findings[0];
        Assert.Equal((true, true, "Claude Desktop", "docker run github"), (github.FromApp, github.IsProgram, github.FoundIn, github.Where));
        Assert.Equal(["GITHUB_TOKEN"], github.SecretNames);
        var calendar = result.Findings[5];
        Assert.Equal((false, false, "calendar-server 1.2", 3), (calendar.FromApp, calendar.IsProgram, calendar.ServerName, calendar.ToolCount));
        Assert.Equal("no program", result.Findings[4].Detail);
        Assert.DoesNotContain("VERYSECRET", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Adding_findings_adds_the_new_ones_the_one_that_needs_a_value_switched_off_and_a_probe_as_found_by_a_scan_and_never_trusted()
    {
        using var scene = await StartAsync(scanner: new FakeScanner(SomeFindings()));
        var result = await scene.Catalog.ScanAsync(new ScanOptionsInfo(), null);

        var added = await scene.Catalog.AddFoundAsync(result, ["0", "1", "2", "3", "4", "5", "99", "x"]);

        Assert.Equal(3, added);
        var store = scene.Host.Store!;
        Assert.Equal(["alpha", "github", "needs", "calendar"], store.Entries.Select(e => e.Label));
        Assert.Equal(("ghp_VERYSECRET", "Claude Desktop", true), (store.Find("github")!.Environment[0].Value, store.Find("github")!.Origin, store.Find("github")!.Enabled));
        Assert.False(store.Find("needs")!.Enabled);
        Assert.Equal((McpToolsCatalog.ScanOrigin, "http://127.0.0.1:8123/mcp"), (store.Find("calendar")!.Origin, store.Find("calendar")!.Url));
        // A scan never marks a server trusted or turns on the switch, whatever the finding carried.
        Assert.All(store.Entries, e => Assert.False(e.Trusted || e.PlainHttpInLan));

        Assert.Equal(0, await scene.Catalog.AddFoundAsync(result, ["0", "5"]));
        Assert.Equal(0, await scene.Catalog.AddFoundAsync(new ScanResultInfo(), ["0"]));
    }

    [Fact]
    public async Task A_cancelled_scan_throws_so_the_sheet_can_say_stopped()
    {
        using var scene = await StartAsync(scanner: new FakeScanner(SomeFindings()));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Catalog.ScanAsync(new ScanOptionsInfo(), null, cancel.Token));
    }

    [Fact]
    public async Task Import_reads_a_file_adds_only_the_ticked_servers_and_never_trusts_them_or_sets_the_switch()
    {
        using var scene = await StartAsync();
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, ".mcp.json");
        File.WriteAllText(file, """{"mcpServers":{"one":{"command":"node"},"two":{"command":"python"},"net":{"type":"http","url":"http://192.168.1.5:8080/mcp"}}}""");

        var preview = await scene.Catalog.ReadImportAsync(ImportSourceKind.ProjectFile, file);
        Assert.Equal(["one", "two"], preview.Servers.Select(s => s.Label));
        Assert.Equal(("net", ImportSkipReason.NetworkServer), (preview.Skipped[0].Name, preview.Skipped[0].Reason));
        Assert.Equal(["alpha"], scene.Catalog.LocalServers.Select(s => s.Label)); // reading adds nothing

        var added = await scene.Catalog.ImportAsync(preview, ["two"]);

        Assert.Equal(1, added);
        Assert.Equal(["alpha", "two"], scene.Catalog.LocalServers.Select(s => s.Label));
        var two = scene.Host.Store!.Find("two")!;
        Assert.Equal(".mcp.json", two.Origin);
        Assert.False(two.Trusted || two.PlainHttpInLan);
        Assert.Empty(two.ToolClasses);
        Assert.Equal(0, await scene.Catalog.ImportAsync(new ImportPreview(), ["one"])); // a preview the catalog never made adds nothing
    }

    // ---- F05: trust, a tool's class and the person's raise ---------------------------------------------------------------

    private static LocalToolInfo ToolOf(McpToolsCatalog catalog, string name) =>
        catalog.LocalServers.Single(s => s.Label == "alpha").Tools.Single(t => t.Name == name);

    [Fact]
    public async Task A_tool_arrives_asking_and_a_destructive_hint_always_counts()
    {
        using var scene = await StartAsync(rig: HintedRig());

        Assert.Equal(LocalToolClass.Unknown, ToolOf(scene.Catalog, "look").Class); // a server's word alone is not enough
        Assert.Equal(LocalToolClass.Unknown, ToolOf(scene.Catalog, "plain").Class);
        Assert.Equal(LocalToolClass.Destructive, ToolOf(scene.Catalog, "wipe").Class);
        Assert.False(scene.Catalog.LocalServers[0].Trusted);
        Assert.True(scene.Catalog.MarksAreYours);
    }

    [Fact]
    public async Task Trusting_a_server_lets_its_read_only_hint_count_and_going_back_takes_it_away_in_the_store_too()
    {
        using var scene = await StartAsync(rig: HintedRig());

        scene.Catalog.SetTrusted("alpha", true);

        Assert.True(scene.Host.Store!.Find("alpha")!.Trusted);
        Assert.True(scene.Catalog.LocalServers[0].Trusted);
        Assert.Equal(LocalToolClass.Read, ToolOf(scene.Catalog, "look").Class);
        Assert.Equal(LocalToolClass.Unknown, ToolOf(scene.Catalog, "plain").Class); // trust is not a raise
        Assert.Equal(LocalToolClass.Destructive, ToolOf(scene.Catalog, "wipe").Class);

        scene.Catalog.SetTrusted("alpha", false);

        Assert.False(scene.Host.Store.Find("alpha")!.Trusted);
        Assert.Equal(LocalToolClass.Unknown, ToolOf(scene.Catalog, "look").Class);
    }

    [Fact]
    public async Task A_raise_goes_to_the_store_and_changes_the_class_and_none_takes_it_back()
    {
        using var scene = await StartAsync(rig: HintedRig());

        scene.Catalog.SetToolClass("alpha", "plain", LocalToolRaise.Write);
        Assert.Equal((LocalToolClass.Write, LocalToolRaise.Write), (ToolOf(scene.Catalog, "plain").Class, ToolOf(scene.Catalog, "plain").Raise));
        Assert.Equal(ToolClass.Write, scene.Host.Store!.Find("alpha")!.ToolClasses["plain"]);

        scene.Catalog.SetToolClass("alpha", "plain", LocalToolRaise.Read);
        Assert.Equal(LocalToolClass.Read, ToolOf(scene.Catalog, "plain").Class);

        scene.Catalog.SetToolClass("alpha", "plain", LocalToolRaise.None);
        Assert.Equal((LocalToolClass.Unknown, LocalToolRaise.None), (ToolOf(scene.Catalog, "plain").Class, ToolOf(scene.Catalog, "plain").Raise));
        Assert.Empty(scene.Host.Store.Find("alpha")!.ToolClasses);
    }

    [Fact]
    public async Task A_destructive_hint_is_a_floor_no_raise_is_kept_below_it_and_an_unknown_tool_is_not_raised()
    {
        using var scene = await StartAsync(rig: HintedRig());

        scene.Catalog.SetToolClass("alpha", "wipe", LocalToolRaise.Read);
        scene.Catalog.SetToolClass("alpha", "wipe", LocalToolRaise.Write);
        scene.Catalog.SetToolClass("alpha", "not-listed", LocalToolRaise.Read);
        scene.Catalog.SetToolClass("nobody", "plain", LocalToolRaise.Read);

        Assert.Empty(scene.Host.Store!.Find("alpha")!.ToolClasses);
        Assert.Equal((LocalToolClass.Destructive, LocalToolRaise.None), (ToolOf(scene.Catalog, "wipe").Class, ToolOf(scene.Catalog, "wipe").Raise));

        // Even a raise written to the entry some other way cannot lower the class: the rule is applied where the class is made.
        scene.Host.Store.SetToolClass("alpha", "wipe", ToolClass.Read);
        scene.Catalog.SetTrusted("alpha", true);
        Assert.Equal(LocalToolClass.Destructive, ToolOf(scene.Catalog, "wipe").Class);
    }

    [Fact]
    public async Task Editing_a_server_keeps_its_trust_mark_and_its_raises()
    {
        using var scene = await StartAsync(rig: HintedRig());
        scene.Catalog.SetTrusted("alpha", true);
        scene.Catalog.SetToolClass("alpha", "plain", LocalToolRaise.Write);
        var draft = scene.Catalog.GetDraft("alpha")!;
        draft.CallTimeoutSeconds = 60;

        Assert.Equal(ServerProblem.None, await scene.Catalog.SaveAsync("alpha", draft));

        var saved = scene.Host.Store!.Find("alpha")!;
        Assert.True(saved.Trusted);
        Assert.Equal(ToolClass.Write, saved.ToolClasses["plain"]);
    }

    [Fact]
    public async Task In_governed_mode_the_administrators_marks_count_and_the_persons_are_not_kept()
    {
        using var scene = await StartAsync(rig: HintedRig(), marks: _ => new McpServerMarks(true, new Dictionary<string, ToolClass> { ["plain"] = ToolClass.Read }));

        Assert.False(scene.Catalog.MarksAreYours);
        Assert.True(scene.Catalog.LocalServers[0].Trusted);
        Assert.Equal(LocalToolClass.Read, ToolOf(scene.Catalog, "look").Class);
        Assert.Equal((LocalToolClass.Read, LocalToolRaise.Read), (ToolOf(scene.Catalog, "plain").Class, ToolOf(scene.Catalog, "plain").Raise));

        scene.Catalog.SetTrusted("alpha", false);
        scene.Catalog.SetToolClass("alpha", "plain", LocalToolRaise.Write);

        Assert.False(scene.Host.Store!.Find("alpha")!.Trusted);
        Assert.Empty(scene.Host.Store.Find("alpha")!.ToolClasses);
        Assert.True(scene.Catalog.LocalServers[0].Trusted);
    }

    // ---- F05: plain http ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Allow, null, true)]
    [InlineData(AllowWithoutPlainHttp, null, false)] // the profile does not say allow
    [InlineData("""{"mode":"allow","developer_mode":false,"plain_http":"deny"}""", null, false)]
    [InlineData(Allow, """{"plainHttp":"forbidden"}""", false)] // this computer's policy wins
    public async Task Plain_http_is_permitted_where_the_profile_says_allow_and_this_computers_policy_does_not_forbid_it(string localMcp, string? policy, bool permitted)
    {
        using var scene = await StartAsync(localMcp, policy: policy is null ? null : MachinePolicy.FromJson(policy));

        Assert.Equal(permitted, scene.Catalog.PlainHttpPermitted);
    }

    [Fact]
    public async Task The_switch_applies_only_to_plain_http_beyond_this_computer()
    {
        using var scene = await StartAsync(more:
        [
            Nas(),
            new McpServerEntry { Label = "secure", Url = "https://mcp.example.com/mcp", Enabled = false },
            new McpServerEntry { Label = "here", Url = "http://127.0.0.1:8123/mcp", Enabled = false },
        ]);

        var servers = scene.Catalog.LocalServers.ToDictionary(s => s.Label);

        Assert.True(servers["nas"].PlainHttpApplies);
        Assert.False(servers["secure"].PlainHttpApplies);
        Assert.False(servers["here"].PlainHttpApplies);
        Assert.False(servers["alpha"].PlainHttpApplies);
    }

    [Fact]
    public async Task A_server_beyond_this_computer_without_the_switch_says_why_it_was_refused_and_nothing_was_sent()
    {
        using var scene = await StartAsync(more: [Nas()]);

        var nas = scene.Catalog.LocalServers.Single(s => s.Label == "nas");

        Assert.Equal((LocalServerState.NotAllowed, LocalPlainHttpRefusal.NoOptIn, false), (nas.State, nas.PlainHttpRefusal, nas.PlainHttpInLan));
    }

    [Fact]
    public async Task With_the_switch_on_where_the_profile_does_not_allow_it_the_refusal_is_not_allowed()
    {
        using var scene = await StartAsync(AllowWithoutPlainHttp, more: [Nas(lan: true)]);

        var nas = scene.Catalog.LocalServers.Single(s => s.Label == "nas");

        Assert.Equal((LocalServerState.NotAllowed, LocalPlainHttpRefusal.NotAllowed, true), (nas.State, nas.PlainHttpRefusal, nas.PlainHttpInLan));
        Assert.False(scene.Catalog.PlainHttpPermitted);
    }

    [Fact]
    public async Task Turning_the_switch_on_needs_the_warnings_version_goes_to_the_store_and_the_audit_log_and_turning_it_off_needs_none()
    {
        using var scene = await StartAsync(AllowWithoutPlainHttp, more: [Nas()]);

        await Assert.ThrowsAsync<ArgumentException>(() => scene.Catalog.SetPlainHttpInLanAsync("nas", true, null));
        Assert.False(scene.Host.Store!.Find("nas")!.PlainHttpInLan);

        Assert.Equal(ServerProblem.None, await scene.Catalog.SetPlainHttpInLanAsync("nas", true, Warning));
        Assert.True(scene.Host.Store.Find("nas")!.PlainHttpInLan);
        Assert.True(scene.Catalog.LocalServers.Single(s => s.Label == "nas").PlainHttpInLan);
        Assert.Equal(ServerProblem.None, await scene.Catalog.SetPlainHttpInLanAsync("nas", false, null));
        Assert.False(scene.Host.Store.Find("nas")!.PlainHttpInLan);

        var lines = File.ReadAllLines(Path.Combine(_root, "user", McpAuditLog.FileName));
        using var on = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Equal(("on", Warning), (on.RootElement.GetProperty("state").GetString(), on.RootElement.GetProperty("warning_version").GetString()));
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task The_switch_is_not_for_a_program_or_a_server_that_is_not_there()
    {
        using var scene = await StartAsync();

        Assert.Equal(ServerProblem.AddressInvalid, await scene.Catalog.SetPlainHttpInLanAsync("alpha", true, Warning));
        Assert.Equal(ServerProblem.LabelMissing, await scene.Catalog.SetPlainHttpInLanAsync("nobody", true, Warning));
    }

    [Fact]
    public async Task A_test_of_plain_http_beyond_this_computer_is_refused_before_any_connection_for_each_reason()
    {
        using var permitted = await StartAsync(Allow);
        var draft = new ServerDraft { Label = "x", IsProgram = false, Url = "http://192.168.1.5:9/mcp" };

        var noOptIn = await permitted.Catalog.TestAsync(draft, null);
        Assert.Equal((TestOutcome.Failed, LocalPlainHttpRefusal.NoOptIn), (noOptIn.Outcome, noOptIn.PlainHttp));

        // A header value: never over plain http beyond this computer, whatever the switch says.
        draft.Headers.Add(new SecretPairDraft { Name = "Authorization", Value = "Bearer secret-value" });
        var secret = await permitted.Catalog.TestAsync(draft, null);
        Assert.Equal(LocalPlainHttpRefusal.Secret, secret.PlainHttp);
        Assert.DoesNotContain("secret-value", secret.Error ?? "");

        // With the switch on (kept from the saved server) but where the profile does not allow it.
        using var notAllowed = await StartAsync(AllowWithoutPlainHttp, more: [Nas(lan: true)]);
        var saved = notAllowed.Catalog.GetDraft("nas")!;
        var refused = await notAllowed.Catalog.TestAsync(saved, "nas");
        Assert.Equal((TestOutcome.Failed, LocalPlainHttpRefusal.NotAllowed), (refused.Outcome, refused.PlainHttp));
    }

    [Fact]
    public async Task Editing_a_server_keeps_the_switch_while_its_origin_stays_and_turns_it_off_on_the_record_when_the_address_moves()
    {
        using var scene = await StartAsync(AllowWithoutPlainHttp, more: [Nas(lan: true)]);

        var same = scene.Catalog.GetDraft("nas")!;
        same.Url = "http://192.168.1.5:9/other-path";
        Assert.Equal(ServerProblem.None, await scene.Catalog.SaveAsync("nas", same));
        Assert.True(scene.Host.Store!.Find("nas")!.PlainHttpInLan);

        var moved = scene.Catalog.GetDraft("nas")!;
        moved.Url = "http://192.168.1.99:9/mcp";
        Assert.Equal(ServerProblem.None, await scene.Catalog.SaveAsync("nas", moved));
        Assert.False(scene.Host.Store.Find("nas")!.PlainHttpInLan);
        var lines = File.ReadAllLines(Path.Combine(_root, "user", McpAuditLog.FileName));
        using var off = System.Text.Json.JsonDocument.Parse(lines[^1]);
        Assert.Equal("off", off.RootElement.GetProperty("state").GetString());

        // A draft that breaks a rule changes nothing, the switch included.
        await scene.Catalog.SetPlainHttpInLanAsync("nas", true, Warning);
        var bad = scene.Catalog.GetDraft("nas")!;
        bad.Url = "http://8.8.8.8/mcp";
        Assert.Equal(ServerProblem.PlainHttpNotLocal, await scene.Catalog.SaveAsync("nas", bad));
        Assert.True(scene.Host.Store.Find("nas")!.PlainHttpInLan);
    }

    [Fact]
    public async Task The_adapter_reads_the_permission_from_the_agent_as_the_module_does_and_a_started_agent_is_running()
    {
        var gateway = new ScriptedGateway("[]", Allow);
        using var agent = gateway.Builder().UseMachinePolicy(MachinePolicy.FromJson("""{"plainHttp":"forbidden"}""")).Build();
        var view = new FadenstackAgentToolsView(agent);
        var raised = 0;
        view.Changed += (_, _) => raised++;

        Assert.False(view.IsRunning); // not started: the profile is not there yet
        Assert.False(view.PlainHttpPermitted);
        await agent.StartAsync();
        view.RaiseChanged();

        Assert.True(view.IsRunning);
        Assert.False(view.PlainHttpPermitted); // the profile says allow, this computer's policy forbids
        Assert.Equal(1, raised);
    }
}

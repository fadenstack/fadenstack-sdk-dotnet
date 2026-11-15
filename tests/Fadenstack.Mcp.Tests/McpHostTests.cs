using System.Collections.Concurrent;

using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCP9005 // roots are deprecated in the newest MCP revision; the design still tells servers the readable folders

namespace Fadenstack.Mcp.Tests;

/// <summary>The host between the store, the SDK's module and the preferences: servers, secrets in logs, roots, tests of a server.</summary>
public sealed class McpHostTests : IDisposable
{
    private const string Allow = """{"mode":"allow","developer_mode":false}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcphost-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>The host's folders as a source: change <see cref="Folders"/>, then call <c>RaiseChanged</c>.</summary>
    private sealed class Folders
    {
        public List<string> Items { get; } = [];

        public McpRootsSource Source { get; }

        public Folders() => Source = new McpRootsSource(() => [.. Items]);

        public void Set(params string[] items)
        {
            Items.Clear();
            Items.AddRange(items);
            Source.RaiseChanged();
        }
    }

    private McpServerStore Store() => new(Path.Combine(_root, "user"), new Reversing());

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception) + " " + exception);
        }
    }

    [Fact]
    public async Task Secrets_of_servers_that_do_not_start_are_in_no_log_no_status_and_no_test_result()
    {
        var capture = new Capture();
        var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var program = McpStoreTests.Program() with { Command = Path.Combine(_root, "no-such-program-" + Guid.NewGuid().ToString("N")), Arguments = ["--token=s3cret-arg"] };
        var address = McpStoreTests.Address() with { Url = "http://127.0.0.1:1/mcp" };
        var store = Store();
        store.Add(program);
        store.Add(address);
        using var host = new McpHost(_ => store);
        var gateway = new ScriptedGateway(localMcp: Allow);
        using var agent = gateway.Builder().UseLogging(logs).AddLocalMcp(host.NewModule()).Build();

        await host.UseUserAsync("anna");
        await host.Loaded;
        await agent.StartAsync();
        var tested = await host.TestAsync(program, allowed: true);

        var seen = string.Join("\n", capture.Lines) + "\n" + string.Join("\n", host.Rows.Select(r => r.Status?.Detail)) + "\n" + tested.Error
            + "\n" + string.Join("\n", host.Rows.Select(r => r.ToString())) + "\n" + string.Join("\n", host.Rows.Select(r => r.Entry.ToString()));
        Assert.All(host.Rows, r => Assert.Equal(McpServerState.Failed, r.State));
        Assert.False(tested.Started);
        Assert.DoesNotContain("s3cret", seen);
        Assert.NotEmpty(capture.Lines);
    }

    [Fact]
    public async Task A_test_starts_nothing_where_the_policy_does_not_allow_local_servers()
    {
        var rig = new McpRig();
        using var host = new McpHost(transport: _ => rig.Transport());

        var result = await host.TestAsync(McpStoreTests.Program(), allowed: false);

        Assert.True(result.NotAllowed);
        Assert.Equal(0, rig.Connects);
    }

    [Fact]
    public async Task A_test_lists_the_servers_tools_and_stops_it()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b), McpRig.Tool("now", "Time.", () => "noon") } };
        using var host = new McpHost(transport: _ => rig.Transport());

        var result = await host.TestAsync(McpStoreTests.Program(), allowed: true);

        Assert.True(result.Started);
        Assert.Equal(["add", "now"], result.Tools.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(1, rig.Connects);
    }

    [Fact]
    public async Task The_signed_in_users_servers_join_the_module_switch_and_leave_with_the_store()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };
        var store = Store();
        store.Add(McpStoreTests.Program("alpha"));
        store.Add(McpStoreTests.Program("beta") with { Enabled = false });
        using var host = new McpHost(_ => store, server: e => rig.Server(e.Label));
        var gateway = new ScriptedGateway(localMcp: Allow).Says("One.").Says("Two.");
        using var agent = gateway.Builder().AddLocalMcp(host.NewModule()).Build();
        var changed = 0;
        host.Changed += (_, _) => Interlocked.Increment(ref changed);

        await host.UseUserAsync("anna");
        await host.Loaded;
        var session = await agent.StartSessionAsync();
        await session.SendAsync("first");

        Assert.Equal([McpServerState.Running, McpServerState.Off], host.Rows.Select(r => r.State));
        Assert.Contains("find_tool", gateway.Offered[0]);

        await host.SetEnabledAsync("alpha", false);
        await session.SendAsync("second");
        Assert.Equal(McpServerState.Off, host.Rows[0].State);
        Assert.False(store.Find("alpha")!.Enabled);
        Assert.DoesNotContain("find_tool", gateway.Offered[1]);
        Assert.True(changed > 0);

        await host.RemoveAsync("beta");
        Assert.Equal(["alpha"], host.Rows.Select(r => r.Label));
    }

    [Fact]
    public async Task Adding_a_plain_http_server_beyond_this_computer_with_a_header_is_refused_and_nothing_is_stored_or_started()
    {
        var rig = new McpRig();
        using var host = new McpHost(_ => Store(), server: e => rig.Server(e.Label));
        host.NewModule();
        await host.UseUserAsync("anna");

        var check = await host.AddAsync(McpStoreTests.Address() with { Url = "http://192.168.1.2/mcp" });

        Assert.Equal(McpProblem.PlainHttpWithHeaders, check.Problem);
        Assert.Empty(host.Rows);
        Assert.Equal(0, rig.Connects);
    }

    [Fact]
    public void The_readable_folders_are_the_roots_as_file_uris_and_follow_the_setting()
    {
        var folders = new Folders();
        var offers = Path.Combine(_root, "Offers");
        var invoices = Path.Combine(_root, "My Invoices");
        using var host = new McpHost(roots: folders.Source);
        Assert.Empty(host.CurrentRoots());

        folders.Set(offers, invoices);
        var roots = host.CurrentRoots();

        Assert.Equal(["Offers", "My Invoices"], roots.Select(r => r.Name));
        Assert.All(roots, r => Assert.StartsWith("file:///", r.Uri));
        Assert.EndsWith("/My%20Invoices", roots[1].Uri);
        Assert.Equal(new Uri(offers).AbsoluteUri, roots[0].Uri);
        folders.Set(invoices);
        Assert.Equal(["My Invoices"], host.CurrentRoots().Select(r => r.Name));
    }

    [Fact]
    public async Task A_running_server_is_told_when_the_readable_folders_change_and_asks_again()
    {
        var folders = new Folders();
        var notices = 0;
        var rig = new McpRig
        {
            OnServer = server => server.RegisterNotificationHandler(NotificationMethods.RootsListChangedNotification, (_, _) =>
            {
                Interlocked.Increment(ref notices);
                return default;
            }),
            Tools =
            {
                McpRig.Tool("show_roots", "Lists the roots.", async (McpServer server, CancellationToken ct) =>
                {
                    var roots = await server.RequestRootsAsync(new ListRootsRequestParams(), ct);
                    return string.Join(",", roots.Roots.Select(r => r.Name));
                }),
            },
        };
        var store = Store();
        store.Add(McpStoreTests.Program("alpha"));
        using var host = new McpHost(_ => store, folders.Source, server: e => rig.Server(e.Label));
        using var agent = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(host.NewModule()).Build();
        await host.UseUserAsync("anna");
        await host.Loaded;
        await agent.StartAsync();
        Assert.Equal(McpServerState.Running, host.Rows[0].State);
        async Task<string> RootsAsync() => (string)(await agent.Tools.Single(t => t.Name == "show_roots").Function
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None))!;
        Assert.DoesNotContain("Offers", await RootsAsync());

        folders.Set(Path.Combine(_root, "Offers"));

        await McpRig.EventuallyAsync(() => Volatile.Read(ref notices) == 1, "the server was told");
        Assert.Contains("Offers", await RootsAsync());
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref notices));
    }

    [Fact]
    public void The_sdk_server_of_an_entry_uses_lists_not_a_shell_string_and_the_entrys_timeout()
    {
        var program = McpHost.ToServer(McpStoreTests.Program() with { CallTimeoutSeconds = 30 });
        var address = McpHost.ToServer(McpStoreTests.Address());

        Assert.Equal(("node", ["server.js", "--token=abc"]), (program.Command, program.Arguments.ToArray()), new PairComparer());
        Assert.Equal(TimeSpan.FromSeconds(30), program.CallTimeout);
        Assert.Equal("http://127.0.0.1:8123/mcp", address.Url!.ToString());
        Assert.Null(address.Command);
    }

    private sealed class PairComparer : IEqualityComparer<(string?, string[])>
    {
        public bool Equals((string?, string[]) x, (string?, string[]) y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2);

        public int GetHashCode((string?, string[]) obj) => obj.Item1?.GetHashCode() ?? 0;
    }
}

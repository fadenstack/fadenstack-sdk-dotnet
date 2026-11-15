using Fadenstack.Auth;
using Fadenstack.Tools;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// The hint rule (sdk/51 F05, H4): a tool arrives unclassified and asks; a destructive hint from any server makes it destructive;
/// a read-only hint counts only from a server the person or the administrator trusts; only they can raise a tool to read or
/// write, and never below a destructive hint.
/// </summary>
public sealed class McpHintRuleTests : IDisposable
{
    private const string Allow = """{"mode":"allow","developer_mode":false}""";

    private static McpToolHints Hints(bool? readOnly = null, bool? destructive = null) => new(null, readOnly, destructive, null, null);

    // ---- the rule itself

    [Theory]
    [InlineData(false, null, null, null, ToolClass.Unknown)] // the floor: it arrives unclassified and asks
    [InlineData(true, null, null, null, ToolClass.Unknown)] // trust alone changes nothing
    [InlineData(false, true, null, null, ToolClass.Unknown)] // an untrusted server's read-only hint is ignored
    [InlineData(true, true, null, null, ToolClass.Read)] // a trusted server's counts
    [InlineData(true, false, null, null, ToolClass.Unknown)] // "not read-only" never lowers anything
    [InlineData(false, null, true, null, ToolClass.Destructive)] // a destructive hint raises, from any server
    [InlineData(true, null, true, null, ToolClass.Destructive)]
    [InlineData(false, true, true, null, ToolClass.Destructive)] // both: destructive wins
    [InlineData(true, true, true, null, ToolClass.Destructive)]
    [InlineData(false, null, false, null, ToolClass.Unknown)] // destructiveHint: false is the server's word, not a class
    [InlineData(false, null, null, ToolClass.Read, ToolClass.Read)] // the person raises it
    [InlineData(false, null, null, ToolClass.Write, ToolClass.Write)]
    [InlineData(false, true, null, ToolClass.Write, ToolClass.Write)] // the person's word beats the untrusted hint
    [InlineData(true, null, true, ToolClass.Read, ToolClass.Destructive)] // destructive stays a floor
    [InlineData(false, null, true, ToolClass.Write, ToolClass.Destructive)]
    [InlineData(false, null, null, ToolClass.Destructive, ToolClass.Unknown)] // a raise only goes to read or write
    [InlineData(false, null, null, ToolClass.Unknown, ToolClass.Unknown)]
    public void Classify_follows_the_rule(bool trusted, bool? readOnly, bool? destructive, ToolClass? raised, ToolClass expected) =>
        Assert.Equal(expected, McpToolClasses.Classify(trusted, Hints(readOnly, destructive), raised));

    [Fact]
    public void The_servers_marks_are_read_by_the_tools_own_name()
    {
        var server = LocalMcpServer.FromTransport("alpha", _ => throw new InvalidOperationException());
        server.ToolClasses = new Dictionary<string, ToolClass> { ["write_note"] = ToolClass.Write };

        Assert.Equal(ToolClass.Write, McpToolClasses.Classify(server, "write_note", Hints()));
        Assert.Equal(ToolClass.Unknown, McpToolClasses.Classify(server, "other", Hints(readOnly: true)));
        server.Trusted = true;
        Assert.Equal(ToolClass.Read, McpToolClasses.Classify(server, "other", Hints(readOnly: true)));
        Assert.Equal(ToolClass.Destructive, McpToolClasses.Classify(server, "write_note", Hints(destructive: true)));
    }

    // ---- in the agent: the tools the model is offered

    private static McpRig Rig() => new()
    {
        Tools =
        {
            McpRig.Tool("peek", "Looks.", () => "seen", readOnly: true),
            McpRig.Tool("wipe", "Deletes.", () => "gone", destructive: true),
            McpRig.Tool("both", "Says both.", () => "?", readOnly: true, destructive: true),
            McpRig.Tool("plain", "Says nothing.", () => "ok"),
        },
    };

    private static async Task<Dictionary<string, ToolClass>> ClassesAsync(LocalMcpServer server)
    {
        using var agent = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(new LocalMcpModule([server])).Build();
        await agent.StartAsync();
        return agent.Tools.Where(t => t.Source == "mcp" && t.Name != LocalMcpModule.FindToolName).ToDictionary(t => t.Name, t => t.Class);
    }

    [Fact]
    public async Task A_tool_arrives_unclassified_an_untrusted_read_hint_is_ignored_and_a_destructive_hint_raises()
    {
        var classes = await ClassesAsync(Rig().Server("alpha"));

        Assert.Equal(ToolClass.Unknown, classes["peek"]);
        Assert.Equal(ToolClass.Unknown, classes["plain"]);
        Assert.Equal(ToolClass.Destructive, classes["wipe"]);
        Assert.Equal(ToolClass.Destructive, classes["both"]);
    }

    [Fact]
    public async Task A_trusted_servers_read_hint_counts_and_a_destructive_hint_still_wins()
    {
        var server = Rig().Server("alpha");
        server.Trusted = true;

        var classes = await ClassesAsync(server);

        Assert.Equal(ToolClass.Read, classes["peek"]);
        Assert.Equal(ToolClass.Unknown, classes["plain"]);
        Assert.Equal(ToolClass.Destructive, classes["wipe"]);
        Assert.Equal(ToolClass.Destructive, classes["both"]);
    }

    [Fact]
    public async Task The_persons_raise_works_on_an_untrusted_server_and_never_below_a_destructive_hint()
    {
        var server = Rig().Server("alpha");
        server.ToolClasses = new Dictionary<string, ToolClass>
        {
            ["plain"] = ToolClass.Write, ["peek"] = ToolClass.Read, ["wipe"] = ToolClass.Read, ["both"] = ToolClass.Write,
        };

        var classes = await ClassesAsync(server);

        Assert.Equal(ToolClass.Write, classes["plain"]);
        Assert.Equal(ToolClass.Read, classes["peek"]);
        Assert.Equal(ToolClass.Destructive, classes["wipe"]);
        Assert.Equal(ToolClass.Destructive, classes["both"]);
    }

    // ---- through the host: whose word counts

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcphint-" + Guid.NewGuid().ToString("N"));

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    private McpServerStore Store(params McpServerEntry[] entries)
    {
        var store = new McpServerStore(Path.Combine(_root, "user"), new Reversing());
        foreach (var entry in entries)
        {
            Assert.True(store.Add(entry).IsValid);
        }
        return store;
    }

    private static async Task<(LocalLiveAgent Agent, McpHost Host)> StartAsync(McpServerStore store, McpRig rig, Func<McpServerEntry, McpServerMarks?>? marks = null)
    {
        var host = new McpHost(_ => store, server: e => rig.Server(e.Label), marks: marks);
        var agent = new ScriptedGateway(localMcp: Allow).Says("One.").Says("Two.").Builder().AddLocalMcp(host.NewModule()).Build();
        await host.UseUserAsync("anna");
        await host.Loaded;
        await agent.StartAsync();
        return (new LocalLiveAgent(agent), host);
    }

    private sealed class LocalLiveAgent(Fadenstack.Agents.FadenstackAgent agent) : IDisposable
    {
        public Fadenstack.Agents.FadenstackAgent Agent => agent;

        public ToolClass ClassOf(string tool) => agent.Tools.Single(t => t.Name == tool).Class;

        public void Dispose() => agent.Dispose();
    }

    [Fact]
    public async Task The_persons_marks_in_the_store_reach_the_tools_and_a_change_applies_at_the_next_turn()
    {
        var entry = McpStoreTests.Program("alpha") with { Enabled = true };
        var store = Store(entry);
        var (live, host) = await StartAsync(store, Rig());
        using var _1 = live;
        using var _2 = host;
        Assert.Equal(ToolClass.Unknown, live.ClassOf("peek"));
        var changed = 0;
        host.Module!.Changed += (_, _) => Interlocked.Increment(ref changed);

        host.SetTrusted("alpha", true);
        host.SetToolClass("alpha", "plain", ToolClass.Write);

        Assert.True(store.Find("alpha")!.Trusted);
        Assert.Equal(ToolClass.Write, store.Find("alpha")!.ToolClasses["plain"]);
        Assert.True(changed >= 1); // the agent offers the tools again at the next turn
        var session = await live.Agent.StartSessionAsync();
        await session.SendAsync("go");
        Assert.Equal(ToolClass.Read, live.ClassOf("peek"));
        Assert.Equal(ToolClass.Write, live.ClassOf("plain"));
        Assert.Equal(ToolClass.Destructive, live.ClassOf("wipe"));

        host.SetTrusted("alpha", false);
        host.SetToolClass("alpha", "plain", null);
        await session.SendAsync("again");
        Assert.Equal(ToolClass.Unknown, live.ClassOf("peek"));
        Assert.Equal(ToolClass.Unknown, live.ClassOf("plain"));
    }

    [Fact]
    public async Task Where_the_administrator_decides_the_persons_marks_in_the_store_count_for_nothing()
    {
        // The store says trusted and raised; the administrator's marks (governed mode) say neither.
        var entry = McpStoreTests.Program("alpha") with
        {
            Trusted = true, ToolClasses = new Dictionary<string, ToolClass> { ["plain"] = ToolClass.Write },
        };
        var store = Store(entry);
        var (live, host) = await StartAsync(store, Rig(), marks: _ => new McpServerMarks(false, new Dictionary<string, ToolClass>()));
        using var _1 = live;
        using var _2 = host;

        Assert.Equal(ToolClass.Unknown, live.ClassOf("peek"));
        Assert.Equal(ToolClass.Unknown, live.ClassOf("plain"));
        Assert.Equal(ToolClass.Destructive, live.ClassOf("wipe"));
    }

    [Fact]
    public async Task The_administrators_marks_raise_tools_the_person_did_not()
    {
        var store = Store(McpStoreTests.Program("alpha"));
        var (live, host) = await StartAsync(store, Rig(),
            marks: _ => new McpServerMarks(true, new Dictionary<string, ToolClass> { ["plain"] = ToolClass.Write }));
        using var _1 = live;
        using var _2 = host;

        Assert.Equal(ToolClass.Read, live.ClassOf("peek"));
        Assert.Equal(ToolClass.Write, live.ClassOf("plain"));
        Assert.Equal(ToolClass.Destructive, live.ClassOf("both"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Chat;
using Fadenstack.Endpoints;
using Fadenstack.Storage;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// Who decides whether a local server runs (sdk/51 F05). Governed: the profile's <c>local_mcp.mode</c>, as before. Standalone:
/// the person's setting, under the machine policy's <c>mcp</c> (forbidden, or a list of allowed sources).
/// </summary>
[Collection("McpPaths")]
public sealed class McpPolicyTests : IDisposable
{
    private const string Definition = """{"slug":"docs-assistant","version":{"instructions":"Hi."}}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcppolicy-" + Guid.NewGuid().ToString("N"));

    public McpPolicyTests() => FadenstackPaths.RootForTests = _root;

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static McpRig Rig() => new() { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };

    private static AgentBuilder Standalone(MachinePolicy policy, bool localMcp, LocalMcpModule module) => FadenstackAgent.CreateBuilder()
        .UseChatClient(new DirectChatClient(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m" }))
        .UseLocalProfile(AgentDefinition.FromJson(Definition), options: new LocalProfileOptions { LocalMcp = localMcp })
        .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
        .UseMachinePolicy(policy).AddLocalMcp(module);

    // ---- governed: the profile's mode, as today

    [Theory]
    [InlineData("""{"mode":"disable","developer_mode":false}""", McpServerState.NotAllowed)]
    [InlineData("""{"mode":"disable","developer_mode":true}""", McpServerState.Running)]
    [InlineData("""{"mode":"allow","developer_mode":false}""", McpServerState.Running)]
    [InlineData("""{"mode":"allowlist","developer_mode":false}""", McpServerState.NotAllowed)]
    public async Task Governed_the_profiles_local_mcp_mode_decides(string localMcp, McpServerState expected)
    {
        var module = new LocalMcpModule([Rig().Server("alpha")]);
        using var agent = new ScriptedGateway(localMcp: localMcp).Builder().AddLocalMcp(module).Build();

        await agent.StartAsync();

        Assert.Equal(expected, module.GetStatus("alpha")!.State);
    }

    [Fact]
    public async Task Governed_the_machine_policy_can_only_narrow_what_the_profile_allows()
    {
        var module = new LocalMcpModule([LocalMcpServer.Stdio("alpha", "not-listed-program")]);
        using var agent = new ScriptedGateway(localMcp: """{"mode":"allow","developer_mode":false}""").Builder()
            .UseMachinePolicy(MachinePolicy.FromJson("""{"mcp":["node"]}""")).AddLocalMcp(module).Build();

        await agent.StartAsync();

        var status = module.GetStatus("alpha")!;
        Assert.Equal(McpServerState.NotAllowed, status.State);
        Assert.Contains("does not list", status.Detail);
    }

    // ---- standalone: the person's settings under the machine policy

    [Fact]
    public async Task Standalone_the_persons_setting_decides_and_it_is_off_by_default()
    {
        var off = new LocalMcpModule([Rig().Server("alpha")]);
        var on = new LocalMcpModule([Rig().Server("alpha")]);
        using var refused = Standalone(MachinePolicy.None, localMcp: false, off).Build();
        using var allowed = Standalone(MachinePolicy.None, localMcp: true, on).Build();

        await refused.StartAsync();
        await allowed.StartAsync();

        Assert.Equal(McpServerState.NotAllowed, off.GetStatus("alpha")!.State);
        Assert.Equal(McpServerState.Running, on.GetStatus("alpha")!.State);
    }

    [Fact]
    public async Task Standalone_a_machine_policy_that_forbids_mcp_wins_over_the_person()
    {
        var module = new LocalMcpModule([Rig().Server("alpha")]);
        using var agent = Standalone(MachinePolicy.FromJson("""{"mcp":"forbidden"}"""), localMcp: true, module).Build();

        await agent.StartAsync();

        Assert.Equal(McpServerState.NotAllowed, module.GetStatus("alpha")!.State);
    }

    [Fact]
    public async Task Standalone_a_list_of_sources_allows_only_those_servers()
    {
        var module = new LocalMcpModule([Rig().Server("alpha"), LocalMcpServer.Stdio("beta", "some-other-program")]);
        using var agent = Standalone(MachinePolicy.FromJson("""{"mcp":["only-this-program"]}"""), localMcp: true, module).Build();

        await agent.StartAsync();

        // a server with no program or address of its own (a custom transport) names no source: refused
        Assert.Equal(McpServerState.NotAllowed, module.GetStatus("alpha")!.State);
        Assert.Equal(McpServerState.NotAllowed, module.GetStatus("beta")!.State);
    }

    // ---- the sources a policy list can name

    private static readonly PolicyRule Listed = new(PolicyAccess.Listed, ["node", "/usr/bin/python3", "http://127.0.0.1:8123", "http://localhost:3000/mcp"]);

    [Theory]
    [InlineData("node", true)]
    [InlineData("NODE", true)] // case does not matter
    [InlineData(@"C:\Program Files\nodejs\node.exe", true)] // the program's file name, with or without .exe
    [InlineData("/usr/local/bin/node", true)]
    [InlineData("/usr/bin/python3", true)] // or its full path
    [InlineData("/opt/evil/python3", false)] // only the full path is listed, not the name python3
    [InlineData("npx", false)]
    [InlineData("nodejs", false)]
    public void A_program_is_a_source_by_its_path_or_its_file_name(string command, bool allowed) =>
        Assert.Equal(allowed, McpSources.Decide(Listed, LocalMcpServer.Stdio("x", command)).Allowed);

    [Theory]
    [InlineData("http://127.0.0.1:8123/mcp", true)] // an origin listed allows every path below it
    [InlineData("http://127.0.0.1:8123/", true)]
    [InlineData("http://127.0.0.1:9000/mcp", false)]
    [InlineData("http://localhost:3000/mcp", true)] // a full address allows that address
    [InlineData("http://localhost:3000/other", false)]
    public void An_address_is_a_source_by_its_origin_or_its_full_url(string url, bool allowed) =>
        Assert.Equal(allowed, McpSources.Decide(Listed, LocalMcpServer.Http("x", new Uri(url))).Allowed);

    [Fact]
    public void A_label_is_the_persons_own_word_and_never_a_source()
    {
        var server = LocalMcpServer.Stdio("node", "npx"); // labelled like a listed program

        Assert.False(McpSources.Decide(Listed, server).Allowed);
        Assert.True(McpSources.Decide(PolicyRule.Allowed, server).Allowed);
        Assert.False(McpSources.Decide(PolicyRule.Forbidden, LocalMcpServer.Stdio("x", "node")).Allowed);
        Assert.False(McpSources.Decide(new PolicyRule(PolicyAccess.Listed, []), LocalMcpServer.Stdio("x", "node")).Allowed);
    }
}

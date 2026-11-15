using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Mcp;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The MCP revisions the local client speaks (sdk/51 F05, first check): 2026-07-28 (stateless: no <c>initialize</c>, no
/// session) first, and 2025-11-25 as the fallback. One small server per revision, over pipes, so no network and no
/// <c>uvx</c>. The cold-start case is what the live check found: a server on 2026-07-28 only, that is slow to start.
/// </summary>
public class LocalMcpProtocolTests
{
    private const string DeveloperMode = """{"mode":"disable","developer_mode":true}""";

    private static async Task<McpServerStatus> AttachAsync(McpRig rig, Action<LocalMcpModule>? configure = null)
    {
        var gateway = new Gateway(localMcp: DeveloperMode);
        using var module = new LocalMcpModule(new[] { rig.Server("time") }) { ReconnectDelay = TimeSpan.Zero };
        configure?.Invoke(module);
        using var agent = gateway.Builder().AddLocalMcp(module).UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        await agent.StartAsync();
        return Assert.Single(module.Servers);
    }

    [Fact]
    public async Task A_server_on_2026_07_28_only_attaches_with_no_initialize_handshake()
    {
        var rig = new McpRig { Tools = { McpTools.Count() }, ProtocolVersion = "2026-07-28" };

        var status = await AttachAsync(rig);

        Assert.Equal(McpServerState.Running, status.State);
        Assert.Equal("2026-07-28", status.ProtocolVersion);
        Assert.Equal("count_terminals", Assert.Single(status.Tools).Name);
        Assert.Equal(1, rig.Connects);
    }

    [Fact]
    public async Task A_server_on_2025_11_25_only_attaches_by_the_initialize_fallback()
    {
        var rig = new McpRig { Tools = { McpTools.Count() }, ProtocolVersion = "2025-11-25" };

        var status = await AttachAsync(rig);

        Assert.Equal(McpServerState.Running, status.State);
        Assert.Equal("2025-11-25", status.ProtocolVersion);
        Assert.Equal("count_terminals", Assert.Single(status.Tools).Name);
    }

    [Fact]
    public async Task A_server_that_speaks_both_is_spoken_to_on_the_newest()
    {
        var rig = new McpRig { Tools = { McpTools.Count() } };

        var status = await AttachAsync(rig);

        Assert.Equal(McpServerState.Running, status.State);
        Assert.Equal("2026-07-28", status.ProtocolVersion);
    }

    [Fact]
    public async Task A_slow_starting_server_on_2026_07_28_only_attaches_after_the_probe_ran_out()
    {
        // The probe waits 100 ms; the server starts answering after 1.5 s, as a uvx does on its first run.
        var rig = new McpRig { Tools = { McpTools.Count() }, ProtocolVersion = "2026-07-28", StartDelay = TimeSpan.FromMilliseconds(1500) };

        var status = await AttachAsync(rig, m => m.DiscoverProbeTimeout = TimeSpan.FromMilliseconds(100));

        Assert.Equal(McpServerState.Running, status.State);
        Assert.Equal("2026-07-28", status.ProtocolVersion);
        Assert.Equal(2, rig.Connects); // the second connect is the pinned one
    }

    [Fact]
    public async Task A_slow_starting_server_on_2025_11_25_only_attaches_too()
    {
        var rig = new McpRig { Tools = { McpTools.Count() }, ProtocolVersion = "2025-11-25", StartDelay = TimeSpan.FromMilliseconds(1500) };

        var status = await AttachAsync(rig, m => m.DiscoverProbeTimeout = TimeSpan.FromMilliseconds(100));

        Assert.Equal(McpServerState.Running, status.State);
        Assert.Equal("2025-11-25", status.ProtocolVersion);
    }
}

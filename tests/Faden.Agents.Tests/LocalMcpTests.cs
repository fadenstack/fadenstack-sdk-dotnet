using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Faden.Governance;
using Faden.Mcp;
using Faden.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Faden.Agents.Tests;

/// <summary>Local MCP servers under the agent's policy (01-design §7.1), against an in-process server.</summary>
public class LocalMcpTests
{
    private const string DeveloperMode = """{"mode":"disable","developer_mode":true}""";

    /// <summary>An MCP server in this process, over pipes; says it is read-only (which is not trusted).</summary>
    private static LocalMcpServer InProcess(string label, List<string> calls, Action? onConnect = null) =>
        LocalMcpServer.FromTransport(label, ct =>
        {
            onConnect?.Invoke();
            var toServer = new Pipe();
            var toClient = new Pipe();
            var tool = McpServerTool.Create(
                [Description("Counts the terminals on a page.")] (int page) => { calls.Add($"count {page}"); return $"page {page}: 12 terminals"; },
                new McpServerToolCreateOptions { Name = "count_terminals", ReadOnly = true, OpenWorld = false });
            var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
                new McpServerOptions { ToolCollection = [tool] });
            _ = server.RunAsync();
            return Task.FromResult<IClientTransport>(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        });

    [Fact]
    public async Task By_default_no_local_server_is_even_started()
    {
        var started = false;
        var module = new LocalMcpModule([InProcess("eplan-db", [], () => started = true)]);
        using var agent = new Gateway().Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.False(started);
        Assert.Empty(agent.Tools);
        Assert.Equal(("eplan-db", "The agent does not allow local MCP servers."), module.Skipped.Select(s => (s.Label, s.Reason)).Single());
    }

    [Fact]
    public async Task With_developer_mode_its_tools_ask_every_time_and_the_attach_is_reported()
    {
        var calls = new List<string>();
        var gateway = new Gateway(localMcp: DeveloperMode)
            .Calls(("call-1", "count_terminals", """{"page":4}""")).Says("12 terminals.");
        var module = new LocalMcpModule([InProcess("eplan-db", calls)]);
        var asked = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddLocalMcp(module)
            .UseApprovals((r, _) => { asked.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("How many terminals on page 4?");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        var tool = agent.Tools.Single();
        Assert.Equal((ToolClass.Unknown, "mcp", "eplan-db", (bool?)false), (tool.Class, tool.Source, tool.Server, tool.OpenWorld));
        Assert.Equal(("count_terminals", "mcp"), (asked.Single().ToolName, asked.Single().Source)); // readOnlyHint is not trusted
        Assert.Equal(new[] { "count 4" }, calls);
        var answer = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Deserialize<JsonElement>(gateway.ChatRequests[1].Body).GetProperty("messages")[2].GetProperty("content").GetString()!);
        Assert.Contains("12 terminals", answer.GetRawText());

        var events = gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()).ToList();
        var attached = events.Single(e => e.GetProperty("type").GetString() == "local_mcp.attached");
        Assert.Equal("eplan-db", attached.GetProperty("label").GetString());
        Assert.Equal("count_terminals", attached.GetProperty("tools")[0].GetString());
        var executed = events.Single(e => e.GetProperty("type").GetString() == "tool.executed");
        Assert.Equal(("mcp", "eplan-db"), (executed.GetProperty("source").GetString(), executed.GetProperty("server").GetString()));
    }

    [Fact]
    public async Task Read_only_hides_local_mcp_tools()
    {
        var gateway = new Gateway(localMcp: """{"mode":"allow","developer_mode":false}""").Says("Hi.");
        using var agent = gateway.Builder().AddLocalMcp(InProcess("eplan-db", [])).Build();
        var session = await agent.StartSessionAsync(ToolMode.ReadOnly);
        await session.SendAsync("Hi");
        Assert.Single(agent.Tools);
        Assert.False(gateway.ChatRequests[0].Json.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_skipped_and_the_agent_runs()
    {
        var module = new LocalMcpModule([
            LocalMcpServer.Stdio("broken", "/nonexistent/mcp-server"),
            InProcess("eplan-db", []),
        ]);
        using var agent = new Gateway(localMcp: DeveloperMode).Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.Equal("count_terminals", agent.Tools.Single().Name);
        var skipped = module.Skipped.Single();
        Assert.Equal("broken", skipped.Label);
        Assert.StartsWith("Could not attach", skipped.Reason);
        Assert.Equal(new[] { "eplan-db" }, module.Attached.Select(a => a.Label));
    }

    [Fact]
    public async Task Clashing_names_can_be_prefixed_with_the_label()
    {
        var module = new LocalMcpModule([InProcess("a", []), InProcess("b", [])]) { PrefixToolNames = true };
        using var agent = new Gateway(localMcp: DeveloperMode).Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.Equal(new[] { "a_count_terminals", "b_count_terminals" }, agent.Tools.Select(t => t.Name));
    }
}

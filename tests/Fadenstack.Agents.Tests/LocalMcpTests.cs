using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Fadenstack.Governance;
using Fadenstack.Mcp;
using Fadenstack.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Local MCP servers under the agent's policy, against an in-process server.</summary>
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
        var module = new LocalMcpModule([InProcess("notes-db", [], () => started = true)]);
        using var agent = new Gateway().Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.False(started);
        Assert.Empty(agent.Tools);
        Assert.Equal(("notes-db", "The agent does not allow local MCP servers."), module.Skipped.Select(s => (s.Label, s.Reason)).Single());
    }

    [Fact]
    public async Task With_developer_mode_its_tools_ask_every_time_and_the_attach_is_reported()
    {
        var calls = new List<string>();
        var gateway = new Gateway(localMcp: DeveloperMode)
            .Calls(("call-0", "find_tool", """{"query":"count terminals"}"""))
            .Calls(("call-1", "count_terminals", """{"page":4}"""))
            .Says("12 terminals.");
        var module = new LocalMcpModule([InProcess("notes-db", calls)]);
        var asked = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddLocalMcp(module)
            .UseApprovals((r, _) => { asked.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("How many terminals on page 4?");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        var tool = agent.Tools.Single(t => t.Name == "count_terminals");
        Assert.Equal(ToolClass.Read, agent.Tools.Single(t => t.Name == LocalMcpModule.FindToolName).Class);
        Assert.Equal((ToolClass.Unknown, "mcp", "notes-db", (bool?)false), (tool.Class, tool.Source, tool.Server, tool.OpenWorld));
        Assert.Equal(("count_terminals", "mcp"), (asked.Single().ToolName, asked.Single().Source)); // readOnlyHint is not trusted
        // The card shows the server and its own hint, still not trusted for the class.
        Assert.Equal(("notes-db", (bool?)true), (asked.Single().Server, asked.Single().ReadOnlyHint));
        Assert.Equal(true, tool.ReadOnlyHint);
        Assert.Equal(new[] { "count 4" }, calls);
        var answer = McpRig.LastToolResult(gateway.ChatRequests[2]);
        Assert.Contains("page 4: 12 terminals", answer);
        Assert.StartsWith("<<mcp-result ", answer); // a server's words are fenced as data

        var events = gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()).ToList();
        var attached = events.Single(e => e.GetProperty("type").GetString() == "local_mcp.attached");
        Assert.Equal("notes-db", attached.GetProperty("label").GetString());
        Assert.Equal("count_terminals", attached.GetProperty("tools")[0].GetString());
        var executed = events.Single(e => e.GetProperty("type").GetString() == "tool.executed" && e.GetProperty("tool").GetString() == "count_terminals");
        Assert.Equal(("mcp", "notes-db"), (executed.GetProperty("source").GetString(), executed.GetProperty("server").GetString()));
    }

    [Fact]
    public async Task Read_only_hides_local_mcp_tools()
    {
        var gateway = new Gateway(localMcp: """{"mode":"allow","developer_mode":false}""").Says("Hi.");
        using var agent = gateway.Builder().AddLocalMcp(InProcess("notes-db", [])).Build();
        var session = await agent.StartSessionAsync(ToolMode.ReadOnly);
        await session.SendAsync("Hi");
        Assert.Equal(2, agent.Tools.Count);
        Assert.Equal(new[] { "find_tool" }, McpRig.ToolNames(gateway.ChatRequests[0])); // the server's tool is unclassified: hidden
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_skipped_and_the_agent_runs()
    {
        var module = new LocalMcpModule([
            LocalMcpServer.Stdio("broken", "/nonexistent/mcp-server"),
            InProcess("notes-db", []),
        ]);
        using var agent = new Gateway(localMcp: DeveloperMode).Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.Equal(new[] { "find_tool", "count_terminals" }, agent.Tools.Select(t => t.Name));
        var skipped = module.Skipped.Single();
        Assert.Equal("broken", skipped.Label);
        Assert.StartsWith("Could not attach", skipped.Reason);
        Assert.Equal(new[] { "notes-db" }, module.Attached.Select(a => a.Label));
    }

    [Fact]
    public async Task Clashing_names_can_be_prefixed_with_the_label()
    {
        var module = new LocalMcpModule([InProcess("a", []), InProcess("b", [])]) { PrefixToolNames = true };
        using var agent = new Gateway(localMcp: DeveloperMode).Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();
        Assert.Equal(new[] { "find_tool", "a_count_terminals", "b_count_terminals" }, agent.Tools.Select(t => t.Name));
    }
}

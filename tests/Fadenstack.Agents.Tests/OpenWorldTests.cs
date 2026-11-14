using System.ComponentModel;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Below Auto, a call to an open-world tool asks, and the person may allow it for the rest of the conversation; Auto and
/// Bypass run it. The mark is the stricter of the tool's own and the gateway's (sdk/51 F08 §4, the owner, 14 Nov 2026):
/// what such a tool is given leaves the organisation, and injected text can put private data into it.
/// </summary>
public class OpenWorldTests
{
    private sealed class Tools
    {
        public const string Contract = """
            [{"name":"search_news","risk":"read"},{"name":"read_page","risk":"read"},{"name":"search_web","risk":"write"}]
            """;

        public List<string> Ran { get; } = new();

        [AgentTool(ToolClass.Read, OpenWorld = true), Description("Searches the news.")]
        public string SearchNews(string query)
        {
            Ran.Add("search_news");
            return $"news on {query}";
        }

        [AgentTool(ToolClass.Read), Description("Reads a page.")]
        public string ReadPage(int page)
        {
            Ran.Add("read_page");
            return $"page {page}";
        }

        [AgentTool(ToolClass.Write, OpenWorld = true), Description("Searches the web.")]
        public string SearchWeb(string query)
        {
            Ran.Add("search_web");
            return $"results for {query}";
        }
    }

    [Theory]
    [InlineData(ToolMode.Off, ToolClass.Read, ToolModeDecision.Hidden)]
    [InlineData(ToolMode.ReadOnly, ToolClass.Read, ToolModeDecision.Ask)]
    [InlineData(ToolMode.Ask, ToolClass.Read, ToolModeDecision.Ask)]
    [InlineData(ToolMode.Auto, ToolClass.Read, ToolModeDecision.Run)]
    [InlineData(ToolMode.Bypass, ToolClass.Read, ToolModeDecision.Run)]
    [InlineData(ToolMode.ReadOnly, ToolClass.Write, ToolModeDecision.Hidden)]
    [InlineData(ToolMode.Ask, ToolClass.Write, ToolModeDecision.Ask)]
    [InlineData(ToolMode.Auto, ToolClass.Write, ToolModeDecision.Run)]
    [InlineData(ToolMode.Bypass, ToolClass.Write, ToolModeDecision.Run)]
    [InlineData(ToolMode.Auto, ToolClass.Destructive, ToolModeDecision.Ask)]
    [InlineData(ToolMode.Auto, ToolClass.Unknown, ToolModeDecision.Ask)]
    public void Below_auto_an_open_world_call_asks_and_auto_and_bypass_run_it(ToolMode mode, ToolClass toolClass, ToolModeDecision expected)
    {
        Assert.Equal(expected, ToolModes.Decide(mode, toolClass, openWorld: true));
        Assert.True(ToolModes.OpenWorldEnforced);
        Assert.Equal(mode < ToolMode.Auto, ToolModes.ConversationOnly(mode, openWorld: true));
        Assert.False(ToolModes.ConversationOnly(mode, openWorld: false));
    }

    [Theory]
    [InlineData(ToolMode.ReadOnly)]
    [InlineData(ToolMode.Ask)]
    public void Without_the_mark_a_read_tool_runs_as_before(ToolMode mode)
    {
        Assert.Equal(ToolModeDecision.Run, ToolModes.Decide(mode, ToolClass.Read));
        Assert.Equal(ToolModeDecision.Run, ToolModes.Decide(mode, ToolClass.Read, openWorld: false));
    }

    [Theory]
    [InlineData(ToolMode.Ask)]
    [InlineData(ToolMode.ReadOnly)]
    public async Task An_open_world_read_tool_asks_below_auto_and_may_be_allowed_for_this_conversation_only(ToolMode mode)
    {
        var gateway = new Gateway(contract: Tools.Contract)
            .Calls(("call-1", "search_news", """{"query":"rates"}""")).Says("One.")
            .Calls(("call-2", "search_news", """{"query":"rates again"}""")).Says("Two.")
            .Calls(("call-3", "search_news", """{"query":"rates later"}""")).Says("Three.");
        var tools = new Tools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();

        var session = await agent.StartSessionAsync(mode);
        await session.SendAsync("News?");
        await session.SendAsync("Again?");
        var other = await agent.StartSessionAsync(mode);
        await other.SendAsync("Later?");

        Assert.Equal(new[] { "search_news", "search_news", "search_news" }, tools.Ran);
        Assert.Equal(2, requests.Count); // the first conversation asked once; the second asked again
        Assert.All(requests, r => Assert.Equal((ToolClass.Read, true, true, false), (r.ToolClass, r.OpenWorld, r.CanAllowForSession, r.CanAllowAlways)));
        Assert.All(requests, r => Assert.Empty(r.GrantDurations));
        Assert.All(requests, r => Assert.StartsWith("What 'search_news' is given leaves the organisation", r.Reason));
        Assert.Equal(new[] { "search_news" }, session.ApprovedForSession);
    }

    [Theory]
    [InlineData(ToolMode.Auto)]
    public async Task In_auto_an_open_world_tool_runs_unasked(ToolMode mode)
    {
        var gateway = new Gateway(contract: Tools.Contract)
            .Calls(("call-1", "search_news", """{"query":"rates"}"""), ("call-2", "search_web", """{"query":"rates"}""")).Says("Done.");
        var tools = new Tools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();

        await (await agent.StartSessionAsync(mode)).SendAsync("Search.");

        Assert.Equal(new[] { "search_news", "search_web" }, tools.Ran);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task An_always_saved_before_does_not_answer_an_open_world_call_below_auto_and_always_is_not_offered()
    {
        var gateway = new Gateway(contract: Tools.Contract)
            .Calls(("call-1", "search_web", """{"query":"rates"}""")).Says("Done.");
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("search_web", ToolClass.Write, DateTimeOffset.UtcNow));
        var tools = new Tools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder(approvals: store).AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();

        var session = await agent.StartSessionAsync(ToolMode.Ask);
        await session.SendAsync("Search.");

        var request = Assert.Single(requests); // the stored "always" did not answer it
        Assert.Equal((ToolClass.Write, true, true, false), (request.ToolClass, request.OpenWorld, request.CanAllowForSession, request.CanAllowAlways));
        Assert.Single(tools.Ran); // approved once: the handler's "always" counted once
        Assert.Equal("once", ApprovalTests.Str(Assert.Single(ApprovalTests.Decisions(session)), "scope"));
    }

    [Fact]
    public async Task The_gateways_open_world_mark_makes_a_tool_open_world_the_stricter_mark_wins()
    {
        // read_page carries no mark of its own; the gateway holds it open-world (an admin's class rule).
        var gateway = ApprovalTests.MarkedCall(new Gateway(contract: Tools.Contract), "call-1", "read_page", """{"page":1}""",
            new { toolCallId = "call-1", toolCallName = "read_page", message = "This tool needs your approval in this chat's tool mode.", toolClass = "read", rule = false, openWorld = true })
            .Says("Page 1.");
        var tools = new Tools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Read page 1.");

        var request = Assert.Single(requests);
        Assert.Equal((ToolClass.Read, true, true, false), (request.ToolClass, request.OpenWorld, request.CanAllowForSession, request.CanAllowAlways));
        Assert.Equal(new[] { "read_page" }, tools.Ran);
    }

    [Fact]
    public async Task A_gateway_mark_without_the_open_world_word_leaves_a_read_tool_unasked()
    {
        var gateway = ApprovalTests.MarkedCall(new Gateway(contract: Tools.Contract), "call-1", "read_page", """{"page":1}""",
            new { toolCallId = "call-1", toolCallName = "read_page", message = "", toolClass = "read", rule = false })
            .Says("Page 1.");
        var tools = new Tools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Read page 1.");

        Assert.Empty(requests);
        Assert.Equal(new[] { "read_page" }, tools.Ran);
    }

    [Fact]
    public async Task A_gateway_tool_the_gateway_holds_open_world_is_offered_for_the_chat_but_not_always()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.approval_required", new { approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.search.web", message = "The tool mode asks.", timeoutS = 300, toolClass = "read", rule = false, openWorld = true }),
                Gateway.Text("Found."), Gateway.Finish("stop"), FakeServer.Done);
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-1", new { ok = true });
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder()
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Search the web.");

        var request = Assert.Single(requests);
        Assert.Equal((ToolClass.Read, true, true, false), (request.ToolClass, request.OpenWorld, request.CanAllowForSession, request.CanAllowAlways));
        Assert.Equal(new[] { "once" }, ApprovalTests.AnswerScopes(gateway)); // "always" was not offered, so it counted once
    }
}

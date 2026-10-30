using System.Text.Json;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// "Allow for 1 hour": the tool in every session of the agent on this device for 60 minutes, in memory. Offered only
/// where "for this chat" is, never for a tool that reaches outside the organisation, and only when the server's profile
/// advertises <c>approval_scopes.time</c> -- else nothing changes and nothing new is sent (the server refuses scopes and
/// fields it does not know).
/// </summary>
public class TimeApprovalTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static Gateway WithTime(Gateway gateway)
    {
        gateway.Features.Add(FadenstackAgent.TimeScopeFeature);
        return gateway;
    }

    /// <summary>An agent whose clock the test moves: <c>now</c> is read at every call.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Start;
    }

    private static Gateway TwoComments(bool feature)
    {
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("1.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("2.");
        return feature ? WithTime(gateway) : gateway;
    }

    private static string? Expires(JsonElement e) => ApprovalTests.Str(e, "expires_at");

    [Fact]
    public async Task Without_the_feature_for_one_hour_is_not_offered_and_an_answer_for_it_counts_once_and_nothing_new_is_sent()
    {
        var gateway = TwoComments(feature: false);
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1");
        await session.SendAsync("2");

        Assert.Equal(2, requests.Count); // asked both times
        Assert.All(requests, r => Assert.Equal((true, true, false), (r.CanAllowForSession, r.CanAllowAlways, r.CanAllowForTime)));
        Assert.Empty(agent.TimeGrants);
        Assert.All(ApprovalTests.Decisions(session), d =>
        {
            Assert.Equal("once", ApprovalTests.Str(d, "scope"));
            Assert.False(d.TryGetProperty("expires_at", out _));
        });
        await agent.FlushAsync();
        Assert.All(ApprovalTests.Decided(gateway), e =>
        {
            Assert.Equal("once", ApprovalTests.Str(e, "scope"));
            Assert.False(e.TryGetProperty("expires_at", out _));
        });
    }

    [Fact]
    public async Task A_grant_that_exists_without_the_feature_is_ignored()
    {
        var gateway = TwoComments(feature: false);
        var asked = 0;
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync();
        agent.GrantTime("add_comment");

        await session.SendAsync("1");

        Assert.Equal(1, asked);
        Assert.Equal("once", ApprovalTests.Str(Assert.Single(ApprovalTests.Decisions(session)), "scope"));
    }

    [Fact]
    public async Task With_the_feature_for_one_hour_is_offered_where_for_this_chat_is()
    {
        var gateway = WithTime(new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("1."));
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("1");

        var comment = Assert.Single(requests);
        Assert.Equal((true, true, true), (comment.CanAllowForSession, comment.CanAllowAlways, comment.CanAllowForTime));
    }

    [Theory]
    [InlineData("delete_page", """{"page":1}""")]
    [InlineData("stamp", "{}")]
    public async Task With_the_feature_a_destructive_or_unclassified_tool_is_never_offered_for_one_hour_and_an_answer_for_it_counts_once(string tool, string arguments)
    {
        var gateway = WithTime(new Gateway().Calls(("call-1", tool, arguments)).Says("Done.").Calls(("call-2", tool, arguments)).Says("Again."));
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await session.SendAsync("1");
        await session.SendAsync("2");

        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal((false, false), (r.CanAllowForSession, r.CanAllowForTime)));
        Assert.Empty(agent.TimeGrants);
        Assert.All(ApprovalTests.Decisions(session), d => Assert.Equal("once", ApprovalTests.Str(d, "scope")));
    }

    [Fact]
    public async Task With_the_feature_a_tool_that_reaches_outside_and_is_destructive_is_not_offered_for_one_hour()
    {
        var gateway = WithTime(new Gateway(contract: WebTools.Contract)
            .Calls(("call-1", "publish_post", """{"query":"x"}""")).Says("Published."));
        ApprovalRequest? asked = null;
        using var agent = gateway.Builder().AddTools(new WebTools())
            .UseApprovals((r, _) => { asked = r; return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Publish.");

        Assert.Equal((true, false, false), (asked!.OpenWorld, asked.CanAllowForSession, asked.CanAllowForTime));
        Assert.Empty(agent.TimeGrants);
    }

    [Fact]
    public async Task With_the_feature_a_tool_that_reaches_outside_is_never_offered_for_one_hour_though_for_this_chat_is()
    {
        var gateway = WithTime(new Gateway(contract: WebTools.Contract)
            .Calls(("call-1", "search_web", """{"query":"x"}""")).Says("Found.")
            .Calls(("call-2", "search_web", """{"query":"y"}""")).Says("Again."));
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new WebTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("1");
        await session.SendAsync("2");

        Assert.Equal(2, requests.Count); // the answer counted once: asked again
        Assert.All(requests, r => Assert.Equal((true, true, false), (r.OpenWorld, r.CanAllowForSession, r.CanAllowForTime)));
        Assert.Empty(agent.TimeGrants);
        Assert.All(ApprovalTests.Decisions(session), d => Assert.Equal("once", ApprovalTests.Str(d, "scope")));
    }

    [Fact]
    public async Task A_grant_never_lets_through_a_call_that_reaches_outside()
    {
        var gateway = WithTime(new Gateway(contract: WebTools.Contract)
            .Calls(("call-1", "search_web", """{"query":"x"}""")).Says("Found."));
        var asked = 0;
        using var agent = gateway.Builder().AddTools(new WebTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        agent.GrantTime("search_web");

        await session.SendAsync("1");

        Assert.Equal(1, asked);
        Assert.Equal("once", ApprovalTests.Str(Assert.Single(ApprovalTests.Decisions(session)), "scope"));
    }

    [Fact]
    public async Task A_rule_covered_call_is_never_offered_for_one_hour()
    {
        var gateway = WithTime(new Gateway());
        ApprovalTests.MarkedCall(gateway, "call-1", "add_comment", """{"page":1,"text":"a"}""",
            new { toolCallId = "call-1", toolCallName = "add_comment", message = "Rule.", rule = true }).Says("1.");
        ApprovalRequest? asked = null;
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { asked = r; return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1");

        Assert.Equal((true, false, false), (asked!.RequiredByRule, asked.CanAllowForSession, asked.CanAllowForTime));
        Assert.Empty(agent.TimeGrants);
    }

    [Fact]
    public async Task A_grant_from_one_session_lets_the_tool_run_in_another_without_asking_recorded_as_time()
    {
        var gateway = TwoComments(feature: true);
        var tools = new PageTools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var clock = new Clock();
        agent.Clock = () => clock.Now;

        var a = await agent.StartSessionAsync();
        await a.SendAsync("1");
        clock.Now = Start.AddMinutes(10);
        var b = await agent.StartSessionAsync();
        var events = new List<AgentEvent>();
        await foreach (var e in b.StreamAsync("2"))
        {
            events.Add(e);
        }

        Assert.Single(requests);
        Assert.Equal(2, tools.Ran.Count);
        Assert.DoesNotContain(events, e => e is ApprovalRequested or ApprovalDecided); // let through: no card
        var grant = Assert.Single(agent.TimeGrants);
        Assert.Equal(("add_comment", Start.AddMinutes(60)), (grant.Tool, grant.Expires));
        Assert.Empty(a.ApprovedForSession); // not "for this chat"
        var asked = Assert.Single(ApprovalTests.Decisions(a));
        Assert.Equal(("time", true), (ApprovalTests.Str(asked, "scope"), asked.GetProperty("asked").GetBoolean()));
        var let = Assert.Single(ApprovalTests.Decisions(b));
        Assert.Equal(("time", false, 0L), (ApprovalTests.Str(let, "scope"), let.GetProperty("asked").GetBoolean(), let.GetProperty("wait_ms").GetInt64()));
    }

    [Fact]
    public async Task The_client_event_carries_scope_time_and_when_it_ends_for_the_asked_call_and_the_call_let_through()
    {
        var gateway = TwoComments(feature: true);
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveForTime())).Build();
        var clock = new Clock();
        agent.Clock = () => clock.Now;
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1");
        clock.Now = Start.AddMinutes(5);
        await session.SendAsync("2");
        await agent.FlushAsync();

        var decided = ApprovalTests.Decided(gateway);
        Assert.Equal(new[] { "time", "time" }, decided.Select(e => ApprovalTests.Str(e, "scope")));
        // The grant ends 60 minutes after the answer; the call let through reports the same end.
        Assert.All(decided, e => Assert.Equal("2026-10-30T13:00:00+00:00", Expires(e)));
        Assert.All(decided, e => Assert.Equal("approved", ApprovalTests.Str(e, "decision")));
        Assert.Equal("2026-10-30T13:00:00+00:00", Expires(Assert.Single(ApprovalTests.Decisions(session), d => !d.GetProperty("asked").GetBoolean())));
    }

    [Fact]
    public async Task A_grant_ends_after_60_minutes_and_the_tool_asks_again()
    {
        var gateway = TwoComments(feature: true);
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var clock = new Clock();
        agent.Clock = () => clock.Now;
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1");
        clock.Now = Start + FadenstackAgent.TimeGrantDuration - TimeSpan.FromSeconds(1);
        Assert.Single(agent.TimeGrants); // still holds one second before
        clock.Now = Start + FadenstackAgent.TimeGrantDuration;
        Assert.Empty(agent.TimeGrants); // over
        await session.SendAsync("2");

        Assert.Equal(60, FadenstackAgent.TimeGrantDuration.TotalMinutes);
        Assert.Equal(2, requests.Count);
        Assert.Equal(Start.AddMinutes(120), Assert.Single(agent.TimeGrants).Expires); // the new answer starts a new hour
    }

    [Fact]
    public async Task Revoking_a_grant_makes_the_tool_ask_again_and_leaves_other_tools_alone()
    {
        // Two tools that stay inside the organisation (a tool that reaches outside never gets a grant).
        const string contract = """[{"name":"fill_field","risk":"write"},{"name":"add_comment","risk":"write"}]""";
        var gateway = WithTime(new Gateway(contract: contract)
            .Calls(("call-1", "fill_field", """{"query":"a"}""")).Says("1.")
            .Calls(("call-2", "add_comment", """{"page":1,"text":"b"}""")).Says("2.")
            .Calls(("call-3", "fill_field", """{"query":"c"}""")).Says("3."));
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new WebTools()).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForTime()); }).Build();
        var clock = new Clock();
        agent.Clock = () => clock.Now;
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("1");
        await session.SendAsync("2");
        Assert.Equal(new[] { "add_comment", "fill_field" }, agent.TimeGrants.Select(g => g.Tool).OrderBy(t => t, StringComparer.Ordinal));
        agent.RevokeTimeGrant("fill_field");
        clock.Now = Start.AddMinutes(1);
        await session.SendAsync("3");

        Assert.Equal(new[] { "call-1", "call-2", "call-3" }, requests.Select(r => r.CallId)); // the third asked again
        Assert.Equal(new[] { "add_comment", "fill_field" }, agent.TimeGrants.OrderBy(g => g.Expires).Select(g => g.Tool));
    }

    [Fact]
    public async Task Resetting_the_approvals_clears_the_grants_of_one_tool_or_all()
    {
        var gateway = WithTime(new Gateway());
        using var agent = gateway.Builder().AddTools(new PageTools()).Build();
        await agent.StartAsync();
        agent.GrantTime("add_comment");
        agent.GrantTime("read_page");

        await agent.ResetApprovalsAsync("add_comment");
        Assert.Equal(new[] { "read_page" }, agent.TimeGrants.Select(g => g.Tool));
        await agent.ResetApprovalsAsync();
        Assert.Empty(agent.TimeGrants);
    }

    [Fact]
    public async Task A_grant_covers_its_tool_only_and_does_not_cover_a_call_a_rule_asks_about()
    {
        var gateway = WithTime(new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("1.")
            .Calls(("call-2", "stamp", "{}")).Says("2."));
        ApprovalTests.MarkedCall(gateway, "call-3", "add_comment", """{"page":3,"text":"c"}""",
            new { toolCallId = "call-3", toolCallName = "add_comment", message = "Rule.", rule = true }).Says("3.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(r.CanAllowForTime ? ApprovalDecision.ApproveForTime() : ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("1"); // asked: add_comment for an hour
        await session.SendAsync("2"); // another tool asks
        await session.SendAsync("3"); // the rule asks, over the grant

        Assert.Equal(new[] { "call-1", "call-2", "call-3" }, requests.Select(r => r.CallId));
        Assert.Equal(new[] { "time", "once", "once" }, ApprovalTests.Decisions(session).Select(d => ApprovalTests.Str(d, "scope")));
    }

    [Fact]
    public async Task What_is_allowed_always_comes_before_a_grant_and_a_grant_before_this_chat()
    {
        var store = new MemoryApprovalStore();
        var gateway = WithTime(new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("1.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("2.")
            .Calls(("call-3", "add_comment", """{"page":3,"text":"c"}""")).Says("3."));
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveForSession())).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1"); // asked: this chat
        agent.GrantTime("add_comment");
        await session.SendAsync("2"); // the grant, over this chat
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        await session.SendAsync("3"); // always, over both

        Assert.Equal(new[] { "chat", "time", "always" }, ApprovalTests.Decisions(session).Select(d => ApprovalTests.Str(d, "scope")));
    }
}

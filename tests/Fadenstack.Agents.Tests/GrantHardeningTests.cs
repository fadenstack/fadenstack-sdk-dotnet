using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;
using Card = Fadenstack.Agents.Tests.BypassTests.Card;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The checks around grants the server holds, so that this SDK fails closed as the TypeScript SDK does (sdk/43 §4, sdk/46
/// C6): a grant is checked against what was asked, a gateway mark covers one call once, a malformed snapshot clears the
/// copy, and the second sign-in's page must be on the API's own origin.
/// </summary>
public class GrantHardeningTests
{
    private static readonly string Comment = """{"page":1,"text":"Fine."}""";

    private static readonly string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":60,"evidence":"calls","step_up":{"required":true,"max_age":300,"start":"/api/agent-tokens/step-ups"}}}""";

    // -- 1: a grant the server returns is checked against what was asked -------------------------------------------

    private static async Task<(FadenstackAgent Agent, FakeGrants Grants)> Started(string? principal = null)
    {
        var gateway = new Gateway { Principal = principal };
        var grants = gateway.UseGrants();
        var agent = gateway.Builder().Build();
        await agent.StartAsync();
        return (agent, grants);
    }

    [Fact]
    public async Task A_grant_that_is_what_was_asked_is_kept()
    {
        var (agent, _) = await Started();
        using var disposed = agent;

        var grant = await agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30), "chat-1");

        Assert.Equal(grant.Id, agent.Grants.Live.Single().Id);
    }

    public static IEnumerable<object[]> Wrong() => new[]
    {
        new object[] { "longer", (Action<FakeGrants, Dictionary<string, object?>>)((g, d) => d["expires_at"] = g.ServerNow.AddMinutes(90)) },
        new object[] { "another tool", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["scope"] = new { tools = new[] { "delete_page" } }) },
        new object[] { "two tools", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["scope"] = new { tools = new[] { "add_comment", "delete_page" } }) },
        new object[] { "another kind", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["kind"] = "bypass") },
        new object[] { "not active", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["status"] = "paused") },
        new object[] { "no time left", (Action<FakeGrants, Dictionary<string, object?>>)((g, d) => d["expires_at"] = g.ServerNow) },
        new object[] { "another conversation", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["conversation_id"] = "chat-2") },
        new object[] { "another agent", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["agent_id"] = "a-2") },
        new object[] { "another agent version", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["agent_version"] = 2) },
        new object[] { "another device", (Action<FakeGrants, Dictionary<string, object?>>)((_, d) => d["device_id"] = "device-8") },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public async Task A_grant_that_exceeds_or_differs_from_the_request_is_refused_and_never_kept(string why, Action<FakeGrants, Dictionary<string, object?>> change)
    {
        Assert.NotEmpty(why);
        // The profile names the device, so a grant of another one is told apart.
        var (agent, grants) = await Started("""{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":"device-7"}""");
        using var disposed = agent;
        grants.Tamper = g => change(grants, g);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30), "chat-1"));

        Assert.Empty(agent.Grants.Live);
        Assert.Null(agent.Grants.Find("g-1"));
    }

    [Fact]
    public async Task A_refused_grant_in_a_chat_is_no_grant_the_call_is_allowed_once_and_the_chat_says_so()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var grants = gateway.UseGrants();
        grants.Tamper = g => g["expires_at"] = grants.ServerNow.AddMinutes(90);
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveFor(TimeSpan.FromMinutes(30)))).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var notices = new List<GovernanceNotice>();

        await foreach (var e in session.StreamAsync("Comment."))
        {
            if (e is NoticeRaised n)
            {
                notices.Add(n.Notice);
            }
        }

        var record = Decisions(session).Single();
        Assert.Equal("once", Str(record, "scope"));
        Assert.False(record.TryGetProperty("grant_confirmed_minutes", out var confirmed) && confirmed.ValueKind == JsonValueKind.Number);
        Assert.Contains(notices, n => n.Outcome == "grant_failed");
        Assert.Empty(agent.Grants.Live);
    }

    [Fact]
    public async Task An_answer_about_a_grant_of_another_agent_version_is_not_kept_the_copy_stays_as_it_was()
    {
        var (agent, grants) = await Started();
        using var disposed = agent;
        var made = await agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30));
        grants.TamperShown = g => g["agent_version"] = 9;

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.PauseAsync(made.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.ExtendAsync(made.Id, TimeSpan.FromMinutes(15)));

        Assert.Equal(("active", 3), (agent.Grants.Find(made.Id)!.Status, agent.Grants.Find(made.Id)!.AgentVersion));
    }

    [Fact]
    public async Task A_work_grant_with_a_place_or_class_that_was_not_asked_is_refused()
    {
        var (agent, grants) = await Started();
        using var disposed = agent;
        var report = GrantPlace.Document(@"C:\Docs\report.docx");
        grants.Tamper = g => g["scope"] = new
        {
            classes = new[] { "read", "write" },
            targets = new[] { new { type = "document", identifiers = new[] { @"C:\Docs\report.docx", @"C:\Docs\secret.docx" } } },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.GrantWorkAsync(new[] { report }, TimeSpan.FromMinutes(30), "chat-1"));
        grants.Tamper = g => g["scope"] = new
        {
            classes = new[] { "read", "write", "destructive" },
            targets = new[] { new { type = "document", identifiers = new[] { @"C:\Docs\report.docx" } } },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.GrantWorkAsync(new[] { report }, TimeSpan.FromMinutes(30), "chat-1"));
        Assert.Empty(agent.Grants.Live);

        grants.Tamper = null;
        var kept = await agent.Grants.GrantWorkAsync(new[] { report }, TimeSpan.FromMinutes(30), "chat-1");
        Assert.Equal(kept.Id, agent.Grants.Live.Single().Id);
    }

    [Fact]
    public async Task A_Bypass_grant_longer_than_the_confirmed_time_is_refused()
    {
        var gateway = new Gateway { MaxLevel = "\"bypass\"" };
        var grants = gateway.UseGrants(Offered);
        grants.Tamper = g => g["expires_at"] = grants.ServerNow.AddMinutes(120);
        var card = new Card();
        using var agent = gateway.Builder().UseApprovals(card).Build();
        agent.StepUps.Delay = (_, _) => Task.CompletedTask;
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartBypassAsync());

        Assert.Null(session.Bypass);
        Assert.Equal(ToolMode.Ask, session.EffectiveToolMode);
    }

    [Fact]
    public async Task The_minutes_the_server_confirmed_are_kept_beside_the_minutes_the_person_picked()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var grants = gateway.UseGrants();
        grants.LongestMinutes = 15; // the server cuts the 30 minutes to 15
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveFor(TimeSpan.FromMinutes(30)))).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var decided = new List<ApprovalDecided>();

        await foreach (var e in session.StreamAsync("Comment."))
        {
            if (e is ApprovalDecided d)
            {
                decided.Add(d);
            }
        }

        var record = Decisions(session).Single();
        Assert.Equal((30, 15), (record.GetProperty("grant_minutes").GetInt32(), record.GetProperty("grant_confirmed_minutes").GetInt32()));
        Assert.Equal(TimeSpan.FromMinutes(15), decided.Single().Decision.ForTime);
    }

    // -- 2: a gateway mark covers one call, once ----------------------------------------------------------------------

    [Fact]
    public async Task A_repeated_call_id_in_one_round_is_covered_by_no_mark_both_calls_ask()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = "call-1", toolCallName = "add_comment", grantId = id }),
            Gateway.ToolCalls(("call-1", "add_comment", Comment), ("call-1", "add_comment", Comment)), Gateway.Finish("tool_calls"), FakeServer.Done)
            .Says("Done.");
        var asked = 0;
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment twice.");

        // This SDK is stricter than the TypeScript one: a word that cannot tell calls apart (one id on two calls) covers neither.
        Assert.Equal(2, asked);
        Assert.Empty(tools.Ran);
    }

    [Fact]
    public async Task A_mark_counts_once_the_same_call_asked_about_again_is_not_covered_again()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "mcp.github.comment" } });
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-1", new { ok = true })
            .Json("POST", "/v1/agents/docs-assistant/approvals/ap-2", new { ok = true });
        object Wait(string approval) => new
        {
            approvalId = approval, toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "The tool mode asks.",
            timeoutS = 300, toolClass = "write", rule = false,
        };
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = "gw-1", toolCallName = "mcp.github.comment", grantId = id }),
            Gateway.Custom("faden.approval_required", Wait("ap-1")), Gateway.Custom("faden.approval_required", Wait("ap-2")),
            Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);
        var asked = 0;
        using var agent = gateway.Builder()
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Equal(new[] { "grant", null }, Decisions(session).Select(d => Str(d, "scope")));
    }

    [Fact]
    public async Task A_mark_for_one_tool_does_not_cover_a_call_of_another_with_the_same_id()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = "call-1", toolCallName = "read_page", grantId = id }),
            Gateway.ToolCalls(("call-1", "add_comment", Comment)), Gateway.Finish("tool_calls"), FakeServer.Done).Says("Done.");
        var asked = 0;
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Empty(tools.Ran);
    }

    // -- 3: a malformed snapshot clears the cached grants ---------------------------------------------------------------

    private static JsonNode ValidSnapshot(FakeGrants grants) => JsonNode.Parse(JsonSerializer.Serialize(grants.Snapshot()))!;

    public static IEnumerable<object[]> Malformed() => new[]
    {
        new object[] { "grants is not a list", (Func<FakeGrants, JsonNode>)(g => { var s = ValidSnapshot(g); s["grants"] = "nope"; return s; }) },
        new object[] { "no list", (Func<FakeGrants, JsonNode>)(g => { var s = ValidSnapshot(g); ((JsonObject)s).Remove("grants"); return s; }) },
        new object[] { "no server time", (Func<FakeGrants, JsonNode>)(g => { var s = ValidSnapshot(g); ((JsonObject)s).Remove("serverNow"); return s; }) },
        new object[] { "not an object", (Func<FakeGrants, JsonNode>)(_ => JsonValue.Create("nope")!) },
        new object[] { "a grant of another agent", (Func<FakeGrants, JsonNode>)(g => { var s = ValidSnapshot(g); s["grants"]![0]!["agent_id"] = "a-2"; return s; }) },
        new object[] { "a grant of another device", (Func<FakeGrants, JsonNode>)(g => { var s = ValidSnapshot(g); s["grants"]![0]!["device_id"] = "device-8"; return s; }) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_snapshot_clears_the_cached_grants_not_just_the_bad_entry(string why, Func<FakeGrants, JsonNode> snapshot)
    {
        Assert.NotEmpty(why);
        var gateway = new Gateway { Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":"device-7"}""" };
        var grants = gateway.UseGrants();
        grants.Hold("time", new { tools = new[] { "add_comment" } });
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        await agent.Grants.RefreshAsync();
        Assert.Single(agent.Grants.Live);
        gateway.Then(Gateway.Custom("faden.grants", snapshot(grants)), Gateway.Text("Hi."), Gateway.Finish("stop"), FakeServer.Done);
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Hello.");

        Assert.Empty(agent.Grants.Live);
        Assert.Null(agent.Grants.Find("g-1"));
    }

    [Fact]
    public async Task A_grant_this_sdk_cannot_read_is_left_out_and_the_others_stand()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.Hold("time", new { tools = new[] { "add_comment" } });
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        var snapshot = ValidSnapshot(grants);
        snapshot["grants"]!.AsArray().Add(JsonNode.Parse("""{"id":5,"kind":"time"}"""));
        gateway.Then(Gateway.Custom("faden.grants", snapshot), Gateway.Text("Hi."), Gateway.Finish("stop"), FakeServer.Done);
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Hello.");

        Assert.Equal("g-1", agent.Grants.Live.Single().Id);
    }

    [Fact]
    public async Task A_list_with_a_grant_of_another_agent_version_clears_the_copy_and_is_refused()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.Hold("time", new { tools = new[] { "add_comment" } });
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        await agent.Grants.RefreshAsync();
        Assert.Single(agent.Grants.Live);
        grants.TamperShown = g => g["agent_version"] = 9;

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Grants.RefreshAsync());

        Assert.Empty(agent.Grants.Live);
    }

    // -- 4: the second sign-in's page must be on the API's own origin ---------------------------------------------------

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("https://faden.test:8443")]
    [InlineData("http://faden.test")]
    [InlineData("https://faden.test.evil.test")]
    [InlineData("https://faden.test@evil.test")]
    public async Task A_second_sign_in_page_on_another_origin_is_never_opened(string origin)
    {
        var gateway = new Gateway { MaxLevel = "\"bypass\"" };
        var grants = gateway.UseGrants(Offered);
        grants.StepUpOrigin = origin;
        var card = new Card();
        using var agent = gateway.Builder().UseApprovals(card).Build();
        agent.StepUps.Delay = (_, _) => Task.CompletedTask;
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var refused = await Assert.ThrowsAsync<BypassSignInException>(() => session.StartBypassAsync());

        Assert.Equal(StepUpStage.Failed, refused.Stage);
        Assert.Empty(card.Opened);
        Assert.Empty(grants.Made);
        Assert.Null(agent.StepUps.Fresh);
    }
}

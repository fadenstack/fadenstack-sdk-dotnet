using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Grants held by the server (sdk/38 §6, §7; sdk/40 A2.2): the card offers "Allow for…" where the agent allows it, the
/// server's answer is the grant, a call the gateway says a grant covers runs when the device's copy holds it too (the
/// second line), and a stretch of work is offered with its places and extended in place.
/// </summary>
public class GrantTests
{
    private static readonly string Comment = """{"page":1,"text":"Fine."}""";

    /// <summary>The gateway says a grant covers the call, then the model calls the tool.</summary>
    private static Gateway CoveredCall(Gateway gateway, string callId, string tool, string arguments, string grantId) =>
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = callId, toolCallName = tool, grantId }),
            Gateway.ToolCalls((callId, tool, arguments)), Gateway.Finish("tool_calls"), FakeServer.Done);

    [Fact]
    public async Task The_card_offers_a_time_within_the_agents_longest_grant_for_reads_and_writes_only()
    {
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", Comment)).Says("Done.")
            .Calls(("call-2", "delete_page", """{"page":1}""")).Says("Done.");
        gateway.UseGrants("""{"standing":["time"],"longest_grant_minutes":60,"grant_classes":["read","write"]}""");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");
        await session.SendAsync("Delete.");

        Assert.Equal(new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1) }, requests[0].GrantDurations);
        Assert.Equal((false, false), (requests[0].CanAllowForSession, requests[0].CanAllowAlways)); // the agent offers only "time"
        Assert.False(requests[1].CanAllowForTime); // never for a destructive tool
    }

    [Fact]
    public async Task Without_grants_on_the_server_the_card_is_as_before()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal((false, true, true), (requests.Single().CanAllowForTime, requests.Single().CanAllowForSession, requests.Single().CanAllowAlways));
        Assert.False(agent.Grants.Available);
    }

    [Fact]
    public async Task Allow_for_a_time_makes_a_grant_on_the_server_and_says_so_in_the_record()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var grants = gateway.UseGrants();
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
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
        await agent.FlushAsync();

        var made = grants.Made.Single().Json;
        Assert.Equal(("time", "add_comment", 30), (made.GetProperty("kind").GetString(), made.GetProperty("tools")[0].GetString(),
            made.GetProperty("minutes").GetInt32()));
        Assert.Contains("add_comment", tools.Ran.Single());
        var record = Decisions(session).Single();
        Assert.Equal(("grant", "g-1", 30), (Str(record, "scope"), Str(record, "grant_id"), record.GetProperty("grant_minutes").GetInt32()));
        Assert.Equal(TimeSpan.FromMinutes(30), decided.Single().Decision.ForTime);
        var reported = Decided(gateway).Single();
        Assert.Equal(("grant", "g-1"), (Str(reported, "scope"), Str(reported, "grant_id")));
        Assert.Equal("g-1", agent.Grants.Live.Single().Id);
    }

    [Fact]
    public async Task A_grant_the_server_refuses_is_no_grant_the_call_is_allowed_once_and_the_chat_says_so()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var grants = gateway.UseGrants();
        grants.Refuse = "grant_not_offered";
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

        Assert.Equal("once", Str(Decisions(session).Single(), "scope"));
        Assert.Contains(notices, n => n.Outcome == "grant_failed");
        Assert.Empty(agent.Grants.Live);
    }

    [Fact]
    public async Task A_call_the_gateway_says_a_grant_covers_runs_without_asking_when_the_copy_holds_the_grant()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        CoveredCall(gateway, "call-1", "add_comment", Comment, id).Says("Done.");
        var asked = 0;
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");
        await agent.FlushAsync();

        Assert.Equal(0, asked);
        var record = Decisions(session).Single();
        Assert.Equal(("grant", id, false), (Str(record, "scope"), Str(record, "grant_id"), record.GetProperty("asked").GetBoolean()));
        Assert.Equal(("grant", id), (Str(Decided(gateway).Single(), "scope"), Str(Decided(gateway).Single(), "grant_id")));
    }

    [Fact]
    public async Task The_gateways_word_on_a_call_holds_for_its_round_only_a_reused_id_later_asks()
    {
        // The gateway's words are kept per round (a model may reuse "call-1" in the next round): a grant the gateway
        // counted for the first call never lets the later one through.
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        CoveredCall(gateway, "call-1", "add_comment", Comment, id).Calls(("call-1", "add_comment", Comment)).Says("Done.");
        var asked = 0;
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment twice.");

        Assert.Equal(1, asked);
        Assert.Single(tools.Ran);
        Assert.Equal(new[] { "grant", null }, Decisions(session).Select(d => Str(d, "scope")));
    }

    [Theory]
    [InlineData("revoked", "add_comment")] // ended elsewhere: the copy, read again, does not hold it
    [InlineData("paused", "add_comment")]
    [InlineData("active", "read_page")] // a grant for another tool
    public async Task The_second_line_asks_when_the_copy_does_not_hold_the_grant_for_this_call(string status, string grantedTool)
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { grantedTool } }, status: status);
        CoveredCall(gateway, "call-1", "add_comment", Comment, id).Says("Done.");
        var asked = 0;
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Equal("once", Str(Decisions(session).Single(), "scope"));
    }

    [Fact]
    public async Task A_stream_replaces_the_copy_so_a_grant_ended_elsewhere_covers_nothing()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        await agent.StartAsync();
        await agent.Grants.RefreshAsync();
        Assert.Equal(id, agent.Grants.Live.Single().Id);
        var snapshot = new { serverNow = grants.ServerNow, grants = Array.Empty<object>() }; // revoked in the console meanwhile
        gateway.Then(Gateway.Custom("faden.grants", snapshot), Gateway.Text("Hi."), Gateway.Finish("stop"), FakeServer.Done);
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Hello.");

        Assert.Empty(agent.Grants.Live);
    }

    [Fact]
    public async Task A_stretch_of_work_covers_only_calls_that_name_their_place()
    {
        var contract = """[{"name":"add_comment","risk":"write","target_argument":"/text","target_type":"document"},{"name":"read_page","risk":"read"}]""";
        var gateway = new Gateway(contract: contract);
        var grants = gateway.UseGrants();
        var id = grants.Hold("work", new { classes = new[] { "write" }, targets = new[] { new { type = "document", identifiers = new[] { "Fine." } } } });
        CoveredCall(gateway, "call-1", "add_comment", Comment, id).Says("Done.");
        CoveredCall(gateway, "call-2", "add_comment", """{"page":1}""", id).Says("Done.");
        var asked = 0;
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");
        await session.SendAsync("Comment without text.");

        Assert.Equal(1, asked);
        Assert.Equal(new[] { "grant", "once" }, Decisions(session).Select(d => Str(d, "scope")));
    }

    [Fact]
    public async Task A_gateway_wait_answered_for_a_time_makes_the_grant_and_answers_the_wait_once()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-2", new { ok = true });
        gateway.Then(Gateway.Custom("faden.approval_required", new
        {
            approvalId = "ap-2", toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "The tool mode asks.",
            timeoutS = 300, toolClass = "write", rule = false,
        }), Gateway.Text("Commented."), Gateway.Finish("stop"), FakeServer.Done);
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveFor(TimeSpan.FromHours(1)))).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment on the issue.");

        Assert.Equal("mcp.github.comment", grants.Made.Single().Json.GetProperty("tools")[0].GetString());
        var answer = gateway.Server.Requests.Single(r => r.Path.EndsWith("/approvals/ap-2")).Json;
        Assert.Equal((true, "once"), (answer.GetProperty("approved").GetBoolean(), answer.GetProperty("scope").GetString()));
    }

    private sealed class Card : IApprovalHandler, IGrantHandler
    {
        private readonly Func<GrantOffer, GrantAnswer> _answer;

        public Card(Func<GrantOffer, GrantAnswer> answer) => _answer = answer;

        public List<GrantOffer> Offers { get; } = new();

        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalDecision.Approve());

        public Task<GrantAnswer> RequestGrantAsync(GrantOffer offer, CancellationToken cancellationToken)
        {
            Offers.Add(offer);
            return Task.FromResult(_answer(offer));
        }
    }

    [Fact]
    public async Task A_stretch_of_work_is_offered_with_its_places_the_person_may_remove_and_belongs_to_the_chat()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var report = GrantPlace.Document(@"C:\Docs\report.docx");
        var site = GrantPlace.Site("https://purchase.example.de");
        var card = new Card(offer => new GrantAnswer
        {
            Allowed = true, Places = new[] { report }, Duration = offer.Preselected, ShownText = "Work alongside you for 30 min?",
        });
        using var agent = gateway.Builder().AddTools(new PageTools()).UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var grant = await session.OfferWorkAsync(new[] { report, site }, "To fill in the order.");

        var offer = card.Offers.Single();
        Assert.Equal((TimeSpan.FromMinutes(30), 4, "To fill in the order."), (offer.Preselected, offer.Durations.Count, offer.Reason));
        Assert.Equal(new[] { "read", "write" }, offer.Classes);
        var made = grants.Made.Single();
        Assert.Equal(session.Id, made.Header("x-faden-conversation-id"));
        var targets = made.Json.GetProperty("targets");
        Assert.Equal(("document", @"C:\Docs\report.docx", 1), (targets[0].GetProperty("type").GetString(),
            targets[0].GetProperty("identifiers")[0].GetString(), targets.GetArrayLength()));
        Assert.Equal(64, made.Json.GetProperty("shown_text_sha256").GetString()!.Length);
        Assert.Equal(("g-1", session.Id), (grant!.Id, grant.ConversationId));
    }

    [Fact]
    public async Task No_stretch_of_work_is_offered_where_the_agent_does_not_offer_it_or_the_person_says_not_now()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants("""{"standing":["chat","time"],"longest_grant_minutes":120,"grant_classes":["read","write"]}""");
        var card = new Card(_ => GrantAnswer.NotNow);
        using var agent = gateway.Builder().UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        Assert.Null(await session.OfferWorkAsync(new[] { GrantPlace.Folder("/srv/share") }));
        Assert.Empty(card.Offers);

        gateway.Approvals = """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"]}""";
        using var other = gateway.Builder().UseApprovals(card).Build();
        var again = await other.StartSessionAsync(ToolMode.Ask);
        Assert.Null(await again.OfferWorkAsync(new[] { GrantPlace.Folder("/srv/share") }));
        Assert.Single(card.Offers);
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task A_running_grant_is_extended_in_place_and_the_cap_is_the_servers()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.LongestMinutes = 60;
        var card = new Card(offer => new GrantAnswer { Allowed = true, Places = offer.Places, Duration = offer.Preselected });
        using var agent = gateway.Builder().UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = await session.OfferWorkAsync(new[] { GrantPlace.Document("/plan.docx") });
        var more = GrantPlace.Document("/budget.xlsx");

        var extended = await session.OfferExtensionAsync(grant!, TimeSpan.FromMinutes(45), new[] { more });

        var offer = card.Offers.Last();
        Assert.Equal((grant!.Id, more), (offer.Extending!.Id, offer.Places.Single()));
        var asked = grants.Extensions.Single().Json;
        Assert.Equal((45, "/budget.xlsx"), (asked.GetProperty("minutes").GetInt32(), asked.GetProperty("targets")[0].GetProperty("identifiers")[0].GetString()));
        Assert.Equal(new[] { "minutes" }, extended!.Narrowed);
        Assert.Equal(grant.IssuedAt!.Value.AddMinutes(60), extended.ExpiresAt);
    }

    [Fact]
    public async Task The_marker_counts_down_by_the_servers_clock_and_stop_ends_every_grant()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.ServerNow = DateTimeOffset.UtcNow.AddHours(3); // the device's clock is three hours behind
        var card = new Card(offer => new GrantAnswer { Allowed = true, Places = offer.Places, Duration = TimeSpan.FromMinutes(30) });
        using var agent = gateway.Builder().UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        await session.OfferWorkAsync(new[] { GrantPlace.Document("/plan.docx") });
        await agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(15));

        var left = agent.Grants.Remaining(agent.Grants.Live.First());
        Assert.InRange(left.TotalMinutes, 14, 15);
        var changes = 0;
        agent.Grants.Changed += (_, _) => changes++;

        await agent.Grants.RevokeAllAsync();

        Assert.Empty(agent.Grants.Live);
        Assert.Equal(("revoked", "revoked"), (grants.Status("g-1"), grants.Status("g-2")));
        Assert.Equal(2, changes);
    }
}

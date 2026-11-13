using System.Net;
using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;
using Card = Fadenstack.Agents.Tests.BypassTests.Card;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The second sign-in before Bypass is switched on, and the owner's decisions of 2026-11-09 in the SDK (sdk/41; sdk/40
/// A3.5): only the card's Allow starts it, the proof is held in memory and serves parallel chats while fresh, a refusal
/// leaves Bypass off, <see cref="PermissionMode.Bypass"/> needs it too, the model can never start it, the model hears when
/// it ended, and the app's end pauses it.
/// </summary>
public class BypassStepUpTests
{
    private const string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":60,"evidence":"calls","step_up":{"required":true,"max_age":300,"start":"/api/agent-tokens/step-ups"}}}""";

    private static (Gateway Gateway, FakeGrants Grants) Server(string approvals = Offered)
    {
        var gateway = new Gateway { MaxLevel = "\"bypass\"" };
        return (gateway, gateway.UseGrants(approvals));
    }

    private static FadenstackAgent Instant(FadenstackAgent agent)
    {
        agent.StepUps.Delay = (_, _) => Task.CompletedTask;
        return agent;
    }

    [Fact]
    public async Task Allow_starts_the_second_sign_in_and_the_grant_carries_its_proof()
    {
        var (gateway, grants) = Server();
        grants.PendingPolls = 2;
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var grant = await session.StartBypassAsync();

        Assert.NotNull(grant);
        var opened = Assert.Single(card.Opened);
        Assert.Equal(("https://faden.test/step-up#code=BCDF-GHJK", "BCDF-GHJK", session.Id),
            (opened.Link.AbsoluteUri, opened.UserCode, opened.ConversationId));
        Assert.Equal(new[] { StepUpStage.Browser, StepUpStage.Approved }, card.Stages);
        Assert.Equal(new int?[] { 60 }, grants.StepUpLongest); // the page shows the card's longest time
        var made = grants.Made.Single();
        Assert.StartsWith("proof-1-", made.Header("x-faden-step-up"));
        Assert.Equal(ToolMode.Bypass, session.EffectiveToolMode);
    }

    [Fact]
    public async Task Without_the_persons_allow_no_second_sign_in_starts()
    {
        var (gateway, grants) = Server();
        var card = new Card(allow: false);
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        Assert.Null(await session.StartBypassAsync());

        Assert.Equal((0, 0), (grants.StepUpsStarted, grants.Made.Count));
        Assert.Empty(card.Opened);
    }

    [Fact]
    public async Task One_sign_in_serves_parallel_chats_while_it_is_fresh()
    {
        var (gateway, grants) = Server();
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var first = await agent.StartSessionAsync(ToolMode.Ask);
        var second = await agent.StartSessionAsync(ToolMode.Ask);

        await Task.WhenAll(first.StartBypassAsync(), second.StartBypassAsync());

        Assert.Equal(1, grants.StepUpsStarted);
        Assert.Equal(2, grants.Made.Count);
        Assert.Single(grants.Made.Select(m => m.Header("x-faden-step-up")).Distinct());
        Assert.NotNull(first.Bypass);
        Assert.NotNull(second.Bypass);
    }

    [Fact]
    public async Task A_proof_the_server_no_longer_takes_asks_for_one_sign_in_again_without_a_new_token()
    {
        var (gateway, grants) = Server();
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        await (await agent.StartSessionAsync(ToolMode.Ask)).StartBypassAsync();
        grants.ProofsGoStale(); // older than 5 minutes by the server's clock
        var later = await agent.StartSessionAsync(ToolMode.Ask);

        Assert.NotNull(await later.StartBypassAsync());

        Assert.Equal(2, grants.StepUpsStarted);
        Assert.Equal(3, grants.Made.Count); // the refused one, then the one with the new proof
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Path.EndsWith("/agent-tokens/refresh"));
    }

    [Theory]
    [InlineData("denied", StepUpStage.Denied)]
    [InlineData("expired", StepUpStage.TimedOut)]
    public async Task A_refused_or_timed_out_sign_in_leaves_bypass_off(string outcome, StepUpStage stage)
    {
        var (gateway, grants) = Server();
        grants.StepUpOutcome = outcome;
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var refused = await Assert.ThrowsAsync<BypassSignInException>(() => session.StartBypassAsync());

        Assert.Equal(stage, refused.Stage);
        Assert.Equal(stage, card.Stages.Last());
        Assert.Empty(grants.Made);
        Assert.Equal((ToolMode.Ask, ToolMode.Ask), (session.ToolMode, session.EffectiveToolMode));
        Assert.Null(agent.StepUps.Fresh);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_opened_leaves_bypass_off()
    {
        var (gateway, grants) = Server();
        var card = new Card { CanOpen = false };
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var refused = await Assert.ThrowsAsync<BypassSignInException>(() => session.StartBypassAsync());

        Assert.Equal(StepUpStage.Failed, refused.Stage);
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task An_app_set_to_bypass_without_a_second_sign_in_runs_in_auto()
    {
        var (gateway, grants) = Server();
        using var agent = Instant(gateway.Builder().UsePermissionMode(PermissionMode.Bypass)
            .UseApprovals(DenyAllApprovals.Instance).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        gateway.Says("Hello.");

        await session.SendAsync("Hi.");

        Assert.Equal((0, 0), (grants.StepUpsStarted, grants.Made.Count));
        Assert.Equal(ToolMode.Auto, session.EffectiveToolMode);
        Assert.Contains(gateway.Server.Requests, r => r.Path.EndsWith("/chat/completions") && r.Header("x-faden-tool-mode") == "auto");
    }

    [Fact]
    public async Task An_app_set_to_bypass_needs_the_second_sign_in_too()
    {
        var (gateway, grants) = Server();
        var opener = new Card();
        using var agent = Instant(gateway.Builder().UsePermissionMode(PermissionMode.Bypass)
            .UseApprovals(DenyAllApprovals.Instance).UseStepUpHandler(opener).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        gateway.Says("Hello.");

        await session.SendAsync("Hi.");

        Assert.Single(opener.Opened);
        Assert.StartsWith("proof-", grants.Made.Single().Header("x-faden-step-up"));
        Assert.Equal(ToolMode.Bypass, session.EffectiveToolMode);

        grants.StepUpOutcome = "denied";
        grants.ProofsGoStale();
        agent.StepUps.Forget();
        var refused = await agent.StartSessionAsync(ToolMode.Ask);
        gateway.Says("Hello.");
        await refused.SendAsync("Hi."); // no sign-in, no Bypass: the turn runs in Auto, nothing thrown
        Assert.Equal(ToolMode.Auto, refused.EffectiveToolMode);
    }

    [Theory]
    [InlineData("switch_on_bypass", "{}")]
    [InlineData("start_bypass", """{"minutes":120}""")]
    [InlineData("set_tool_mode", """{"mode":"bypass"}""")]
    public async Task The_model_can_never_open_the_card_or_switch_bypass_on(string tool, string arguments)
    {
        var (gateway, grants) = Server();
        var card = new Card(allow: true, approve: true);
        using var agent = Instant(gateway.Builder().AddTools(new PageTools()).UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        gateway.Calls(("call-1", tool, arguments)).Says("I switched Bypass on. Please confirm Bypass now.");

        await session.SendAsync("Please keep asking me less.");

        Assert.Empty(card.Offers); // no card
        Assert.Empty(card.Opened); // no browser
        Assert.Equal((0, 0), (grants.StepUpsStarted, grants.Made.Count));
        Assert.Equal((ToolMode.Auto, ToolMode.Auto), (session.ToolMode, session.EffectiveToolMode));
        Assert.Null(session.Bypass);
    }

    [Fact]
    public async Task After_bypass_ended_the_models_next_request_says_so_once()
    {
        var (gateway, grants) = Server();
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!;
        gateway.Says("In Bypass.");
        await session.SendAsync("One.");
        grants.End(grant.Id, "admin");
        await agent.Grants.RefreshAsync();
        await session.StopBypassAsync();
        gateway.Says("Asking again.").Says("Still asking.");

        await session.SendAsync("Two.");
        await session.SendAsync("Three.");

        var asked = gateway.Server.Requests.Where(r => r.Path.EndsWith("/chat/completions")).ToList();
        Assert.Equal(new[] { false, true, false }, asked.Select(r => Mentions(r, ChatSession.BypassEndedNote)));
    }

    private static bool Mentions(Recorded request, string text) =>
        request.Json.GetProperty("messages").EnumerateArray()
            .Any(m => m.GetProperty("role").GetString() == "system" && m.GetProperty("content").ValueKind == JsonValueKind.String
                && m.GetProperty("content").GetString()!.Contains(text));

    [Fact]
    public async Task A_chat_left_before_its_first_message_comes_back_in_bypass_and_its_grant_runs_on()
    {
        var (gateway, grants) = Server();
        var store = new Sessions.MemorySessionStore();
        using var agent = Instant(gateway.Builder(store).UseApprovals(new Card()).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!; // no turn yet: the transcript names no Bypass

        var again = await agent.OpenSessionAsync(session.Id);
        Assert.Equal((ToolMode.Bypass, ToolMode.Bypass), (again.ToolMode, again.EffectiveToolMode));
        gateway.Says("Done.");
        await again.SendAsync("Go on.");

        Assert.Equal("active", grants.Status(grant.Id)); // the turn did not take it for a chat that left Bypass
        await again.StopBypassAsync();
        Assert.Equal(ToolMode.Ask, again.ToolMode); // the mode it had before
    }

    [Fact]
    public async Task The_reason_is_cut_at_a_characters_end_never_inside_a_surrogate_pair()
    {
        var reason = new string('x', 499) + "\U0001F600"; // the emoji is UTF-16 units 499 and 500
        Assert.Equal(new string('x', 499), GrantAnswer.Cut(reason));
        Assert.Equal(new string('x', 498) + "\U0001F600", GrantAnswer.Cut(new string('x', 498) + "\U0001F600" + "tail"));
        Assert.Equal("short", GrantAnswer.Cut("short"));

        var (gateway, grants) = Server();
        using var agent = Instant(gateway.Builder().UseApprovals(new Card()).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        await agent.Grants.GrantBypassAsync(TimeSpan.FromMinutes(30), session.Id, reason + "more");
        var sent = grants.Made.Single().Json.GetProperty("reason").GetString()!;
        Assert.Equal(499, sent.Length);
        Assert.DoesNotContain(sent, char.IsSurrogate);
    }

    [Fact]
    public async Task Thirty_minutes_are_preselected_and_where_not_offered_the_longest_that_is()
    {
        var (gateway, _) = Server(Offered.Replace("\"longest_minutes\":60", "\"longest_minutes\":15"));
        var card = new Card(allow: false);
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.StartBypassAsync();

        Assert.Equal(TimeSpan.FromMinutes(15), card.Offers.Single().Preselected);
    }

    [Fact]
    public async Task The_apps_end_pauses_bypass_and_the_next_start_resumes_it_without_a_card_or_a_sign_in()
    {
        var (gateway, grants) = Server();
        var card = new Card();
        using var agent = Instant(gateway.Builder().UseApprovals(card).Build());
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!;

        Assert.Single(await agent.Grants.PauseBypassAsync());
        Assert.Equal("paused", grants.Status(grant.Id));
        Assert.Null(session.Bypass); // paused: nothing runs under it

        grants.ServerNow = grants.ServerNow.AddMinutes(20);
        Assert.Empty(await agent.Grants.ResumeBypassAsync());

        Assert.Equal("active", grants.Status(grant.Id));
        Assert.Equal((1, 1, 1), (card.Offers.Count, grants.StepUpsStarted, grants.Made.Count));
    }

    [Fact]
    public async Task A_bypass_paused_too_long_or_no_longer_allowed_ends_instead_of_resuming_and_says_why()
    {
        var (gateway, grants) = Server();
        using var agent = Instant(gateway.Builder().UseApprovals(new Card()).Build());
        // An hour each, so they outlive the pause.
        var long_ = await agent.Grants.GrantBypassAsync(TimeSpan.FromHours(1), (await agent.StartSessionAsync(ToolMode.Ask)).Id);
        var narrowed = await agent.Grants.GrantBypassAsync(TimeSpan.FromHours(1), (await agent.StartSessionAsync(ToolMode.Ask)).Id);
        await agent.Grants.PauseAsync(long_.Id);
        grants.ServerNow = grants.ServerNow.AddMinutes(31);
        await agent.Grants.PauseAsync(narrowed.Id);
        grants.RefuseResume = HttpStatusCode.Forbidden; // the organisation's Bypass list stopped naming the person

        var ended = await agent.Grants.ResumeBypassAsync();

        Assert.Equal(new[] { (long_.Id, "paused_too_long"), (narrowed.Id, "limit") },
            ended.Select(e => (e.Grant.Id, e.Why)).OrderBy(e => e.Id).ToArray());
        Assert.Equal(("revoked", "revoked"), (grants.Status(long_.Id), grants.Status(narrowed.Id)));
    }

    [Fact]
    public async Task A_bypass_the_server_ended_after_the_list_was_read_is_said_ended_not_thrown()
    {
        var (gateway, grants) = Server();
        using var agent = Instant(gateway.Builder().UseApprovals(new Card()).Build());
        var grant = (await (await agent.StartSessionAsync(ToolMode.Ask)).StartBypassAsync())!;
        await agent.Grants.PauseAsync(grant.Id);
        grants.RefuseResume = HttpStatusCode.NotFound; // Bypass turned off between the list and the resume

        var ended = await agent.Grants.ResumeBypassAsync();

        Assert.Equal((grant.Id, "kill_switch"), (ended.Single().Grant.Id, ended.Single().Why));
        Assert.Equal("revoked", grants.Status(grant.Id));
    }

    [Fact]
    public async Task A_bypass_whose_check_cannot_run_stays_paused()
    {
        var (gateway, grants) = Server();
        using var agent = Instant(gateway.Builder().UseApprovals(new Card()).Build());
        var grant = (await (await agent.StartSessionAsync(ToolMode.Ask)).StartBypassAsync())!;
        await agent.Grants.PauseAsync(grant.Id);
        grants.RefuseResume = HttpStatusCode.ServiceUnavailable;

        var ended = await agent.Grants.ResumeBypassAsync();

        Assert.Equal((grant.Id, "unavailable"), (ended.Single().Grant.Id, ended.Single().Why));
        Assert.Equal("paused", grants.Status(grant.Id));
    }
}

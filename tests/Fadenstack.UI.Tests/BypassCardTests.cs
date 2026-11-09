using System.Globalization;
using System.Net;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// Bypass in the chat panel (sdk/09 L-58, L-59, L-63; sdk/40 A3.3): the picker offers it only where the server does; the
/// confirmation says what no longer asks, what still asks, what the server records and for how long, and only the
/// person's click switches it on, once the server confirmed it; the marker shows the time left and what is recorded, and
/// Stop ends it; when the server ends it the marker goes and the chat says why; Settings → Approvals lists it; a chat
/// comes back in Bypass while its grant lives (parallel chats never break).
/// </summary>
public class BypassCardTests
{
    private static string Offered(string evidence = "calls", int longest = 60, bool allowed = true) =>
        $$$"""{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":{{{(allowed ? "true" : "false")}}},"longest_minutes":{{{longest}}},"evidence":"{{{evidence}}}"}}""";

    private static (Gateway Gateway, FakeGrants Grants) Server(string approvals, string max = "\"bypass\"", string ceiling = "null")
    {
        var gateway = new Gateway(ceiling: ceiling) { MaxLevel = max };
        return (gateway, gateway.UseGrants(approvals));
    }

    private static (AgentChatViewModel Chat, FadenstackAgent Agent, Fadenstack.Agents.Sessions.MemorySessionStore Sessions) Chat(Gateway gateway,
        ApprovalBroker? broker = null, Fadenstack.Agents.Sessions.MemorySessionStore? sessions = null, List<Uri>? opened = null)
    {
        broker ??= new ApprovalBroker();
        broker.OpenBrowser = link =>
        {
            opened?.Add(link); // never a real browser in a test
            return true;
        };
        sessions ??= new Fadenstack.Agents.Sessions.MemorySessionStore();
        var agent = gateway.Builder(sessions).AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        return (chat, agent, sessions);
    }

    private static async Task<T> Until<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 500; i++)
        {
            if (find() is { } found)
            {
                return found;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("It never came.");
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(done(), "It never came.");
    }

    /// <summary>The chat switched to Bypass through its confirmation: the card allowed, the server's grant made.</summary>
    private static async Task<(BypassPart Card, AgentGrant Grant)> SwitchOnAsync(AgentChatViewModel chat, FadenstackAgent agent, string? reason = null)
    {
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);
        card.Reason = reason ?? "";
        card.AllowCommand.Execute(null);
        Assert.True(await starting);
        return (card, agent.Grants.Live.Single(g => g.Kind == GrantKinds.Bypass));
    }

    [Theory]
    [InlineData("decisions", "Your organisation's server records each tool that runs without asking: which tool, when and where, but not what it was given.")]
    [InlineData("calls", "Your organisation's server records each tool that runs without asking, with what it was given (personal data masked).")]
    [InlineData("results", "Your organisation's server records each tool that runs without asking, with what it was given and a short summary of its result (personal data masked).")]
    public async Task The_confirmation_says_what_no_longer_asks_what_still_asks_what_the_server_records_and_for_how_long(string evidence, string records)
    {
        var (gateway, grants) = Server(Offered(evidence));
        var (chat, _, sessions) = Chat(gateway);
        await chat.InitializeAsync();

        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);

        Assert.Equal(records, card.RecordsText);
        Assert.Equal("Switch approvals off in this chat?", card.Title);
        Assert.Contains("also those that delete, send or cannot be undone", card.NoLongerAsksText);
        Assert.Contains("in this chat", card.NoLongerAsksText);
        Assert.Equal("Still asks: your administrator's \"Ask first\" rules and tools marked to always ask. What is blocked stays blocked.",
            card.StillAsksText);
        Assert.Equal(new[] { "15 min", "30 min", "1 h" }, card.Durations.Select(d => d.Label));
        Assert.Equal("30 min", card.SelectedDuration!.Label); // 30 min first, as the work card (owner, 2026-11-09)
        Assert.StartsWith("When the time is up, Bypass ends. It cannot be extended", card.NoExtensionText);
        Assert.Equal(("Why (optional)", "Your administrators see your reason."), (card.ReasonLabel, card.ReasonSeenText));
        foreach (var line in new[] { card.Title, card.NoLongerAsksText, card.StillAsksText, records, card.NoExtensionText, card.ReasonSeenText })
        {
            Assert.Contains(line, card.ShownText);
        }

        card.NotNowCommand.Execute(null);

        Assert.False(await starting);
        Assert.Empty(grants.Made);
        Assert.Equal((BypassCardState.Declined, false), (card.State, card.IsShown));
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.Null(chat.CurrentSessionId); // a declined confirmation leaves no conversation
        Assert.Empty(await sessions.ListAsync(Gateway.Slug));
    }

    [Fact]
    public async Task Allow_switches_bypass_on_once_the_server_confirmed_it_and_the_marker_shows_the_time_left_and_what_is_recorded()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();

        var (card, grant) = await SwitchOnAsync(chat, agent, reason: "Renaming 400 drawings");

        var made = grants.Made.Single();
        Assert.Equal(("bypass", 30, "Renaming 400 drawings"), (made.Json.GetProperty("kind").GetString(),
            made.Json.GetProperty("minutes").GetInt32(), made.Json.GetProperty("reason").GetString()));
        Assert.Equal(64, made.Json.GetProperty("shown_text_sha256").GetString()!.Length);
        Assert.Equal((BypassCardState.On, false), (card.State, card.IsShown));
        Assert.Equal((ToolMode.Bypass, grant.Id), (chat.SelectedToolMode.Mode, chat.CurrentSessionId is { } id ? agent.Grants.BypassFor(id)?.Id : null));
        Assert.True(chat.Grants.HasBypass);
        Assert.Matches(@"^Bypass on · (29|30) min left$", chat.Grants.BypassText);
        Assert.Equal("Each tool that runs is recorded with what it was given.", chat.Grants.BypassRecordedText);
        Assert.Equal("Stop Bypass", chat.Grants.BypassStopName);
        Assert.False(chat.Grants.HasMarker); // the work marker and its "Another 30 min" are not Bypass's
        Assert.False(chat.Grants.KeepGoingCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_the_click_answers_the_card_once_and_a_reason_is_kept_to_500_characters()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);

        card.Reason = new string('x', 600);
        Assert.Equal(500, card.Reason.Length);
        await Task.Delay(50);
        Assert.Equal(BypassCardState.Pending, card.State); // nothing answers it but the person
        Assert.Empty(grants.Made);
        card.AllowCommand.Execute(null);
        Assert.False(card.AllowCommand.CanExecute(null));
        Assert.False(card.NotNowCommand.CanExecute(null));
        Assert.True(await starting);
        Assert.Equal(500, grants.Made.Single().Json.GetProperty("reason").GetString()!.Length);
    }

    [Theory]
    [InlineData("bypass_not_allowed", HttpStatusCode.Forbidden, false, "Bypass is not on: your organisation does not allow it for this agent now. Approvals ask as before.")]
    [InlineData("grant_limits_unavailable", HttpStatusCode.ServiceUnavailable, false, "Bypass is not on: the server cannot check its limits right now. Approvals ask as before.")]
    [InlineData(null, HttpStatusCode.OK, true, "Bypass is not on: the server could not be reached. Approvals ask as before.")]
    public async Task Without_the_servers_acknowledgement_the_card_says_so_and_the_chat_stays_where_it_was(string? code, HttpStatusCode status,
        bool offline, string said)
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        chat.SelectedToolMode = chat.ToolModes.Single(o => o.Mode == ToolMode.ReadOnly);
        (grants.Refuse, grants.RefuseStatus, grants.Offline) = (code, status, offline);

        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);
        card.AllowCommand.Execute(null);

        Assert.False(await starting);
        Assert.Equal((BypassCardState.NotConfirmed, true, said), (card.State, card.IsShown, card.StateText));
        Assert.Equal(ToolMode.ReadOnly, chat.SelectedToolMode.Mode);
        Assert.False(chat.Grants.HasBypass);
        Assert.Empty(agent.Grants.Live);
        card.CloseCommand.Execute(null);
        Assert.False(card.IsShown);
    }

    [Theory]
    [InlineData(true, "\"bypass\"", "null", true)]
    [InlineData(false, "\"bypass\"", "null", false)]
    [InlineData(true, "\"auto\"", "null", false)]
    [InlineData(true, "\"bypass\"", "\"auto\"", false)]
    public async Task The_mode_picker_offers_bypass_only_where_the_server_offers_it(bool allowed, string max, string ceiling, bool shown)
    {
        var (gateway, grants) = Server(Offered(allowed: allowed), max, ceiling);
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();

        var bypass = chat.ToolModes.SingleOrDefault(o => o.Mode == ToolMode.Bypass);
        Assert.Equal(shown, bypass is not null);
        Assert.Equal(shown, chat.ToolModes.Any(o => o.Mode == ToolMode.Bypass && o.IsAvailable));
        if (bypass is not null)
        {
            Assert.Equal("Bypass", bypass.Label);
            chat.SelectedToolMode = bypass; // never by the setter: only the confirmation switches it on
            Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        }
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task The_pickers_bypass_entry_shows_the_confirmation_and_never_switches_on_by_itself()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();

        chat.SelectToolModeCommand.Execute(chat.ToolModes.Single(o => o.Mode == ToolMode.Bypass));
        var card = await Until(() => chat.Grants.BypassCard);

        Assert.True(card.IsPending);
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.Empty(grants.Made);
        card.NotNowCommand.Execute(null);
        await Until(() => chat.CurrentSessionId is null);
    }

    [Fact]
    public async Task Stop_on_the_marker_ends_bypass_the_chat_goes_back_to_its_mode_and_says_you_ended_it()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);

        await chat.Grants.StopBypassCommand.ExecuteAsync(null);

        Assert.Equal("revoked", grants.Status(grant.Id));
        Assert.False(chat.Grants.HasBypass);
        await Until(() => chat.Grants.BypassEndedText == "Bypass ended: you ended it. Approvals ask again.");
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Method == "GET" && r.Path.EndsWith("/grants/" + grant.Id)); // the copy's own record says why
        chat.Grants.DismissBypassEndedCommand.Execute(null);
        Assert.False(chat.Grants.HasBypassEnded);
    }

    [Theory]
    [InlineData("admin", "Bypass ended: an administrator ended it. Approvals ask again.")]
    [InlineData("kill_switch", "Bypass ended: your organisation turned Bypass off. Approvals ask again.")]
    [InlineData("limit", "Bypass ended: your organisation narrowed who may use it. Approvals ask again.")]
    [InlineData("person", "Bypass ended: you ended it. Approvals ask again.")]
    [InlineData(null, "Bypass ended: the server ended it. Approvals ask again.")]
    public async Task When_the_server_ends_bypass_the_marker_goes_and_the_chat_says_why(string? by, string said)
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        grants.End(grant.Id, by ?? "admin");
        grants.CanSayWhy = by is not null; // an older server cannot say why

        await agent.Grants.RefreshAsync(); // as the marker reads the copy again while Bypass runs

        Assert.False(chat.Grants.HasBypass);
        Assert.False(chat.Grants.StopBypassCommand.CanExecute(null));
        await Until(() => chat.Grants.BypassEndedText == said);
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
    }

    [Fact]
    public async Task When_its_time_is_up_the_marker_goes_and_the_chat_says_so()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        await SwitchOnAsync(chat, agent);
        grants.ServerNow = grants.ServerNow.AddMinutes(31);

        await agent.Grants.RefreshAsync();

        Assert.False(chat.Grants.HasBypass);
        await Until(() => chat.Grants.BypassEndedText == "Bypass ended: its time is up. Approvals ask again.");
    }

    [Fact]
    public async Task Settings_lists_a_running_bypass_with_what_is_recorded_and_revoke_ends_it()
    {
        var (gateway, grants) = Server(Offered("decisions"));
        grants.EvidenceLevel = "decisions"; // as an administrator set it (sdk/40 A4 decision 6)
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        Assert.Equal("Each tool that runs is recorded.", chat.Grants.BypassRecordedText);

        chat.IsSettingsOpen = true;
        var row = await Until(() => chat.Grants.Standing.FirstOrDefault(s => s.IsBypass));

        Assert.Matches(@"^Bypass in a chat, (29|30) min left · Each tool that runs is recorded\.$", row.Title);
        await row.RevokeCommand.ExecuteAsync(null);
        Assert.Equal("revoked", grants.Status(grant.Id));
        Assert.False(chat.Grants.HasBypass);
        await Until(() => chat.Grants.BypassEndedText == "Bypass ended: you ended it. Approvals ask again.");
        Assert.DoesNotContain(chat.Grants.Standing, s => s.IsBypass);
    }

    [Fact]
    public async Task Leaving_a_chat_does_not_end_its_bypass_the_chat_list_shows_it_and_the_chat_comes_back_in_bypass()
    {
        // Parallel chats never break (owner, 2026-11-09): a new chat and another chat leave the Bypass of this one running.
        var (gateway, grants) = Server(Offered());
        var sessions = new Fadenstack.Agents.Sessions.MemorySessionStore();
        var (chat, agent, _) = Chat(gateway, sessions: sessions);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        gateway.Says("Done.");
        await chat.SendAsync("Rename the drawings.");
        var first = chat.CurrentSessionId!;

        chat.NewChat();
        gateway.Says("Other.");
        await chat.SendAsync("Something else.");
        var second = chat.CurrentSessionId!;

        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.False(chat.Grants.HasBypass);
        Assert.Equal("active", grants.Status(grant.Id));
        await chat.RefreshSessionsAsync();
        var running = chat.Sessions.Single(s => s.Id == first);
        var other = chat.Sessions.Single(s => s.Id == second);
        Assert.True(running.HasBypass);
        Assert.Matches(@"^Bypass · (29|30) min left$", running.BypassText);
        Assert.False(other.HasBypass);
        Assert.Equal("", other.BypassText);

        await chat.OpenSessionAsync(first); // another chat opened, then this one again
        Assert.Equal((ToolMode.Bypass, true), (chat.SelectedToolMode.Mode, chat.Grants.HasBypass));
        Assert.Contains(chat.ToolModes, o => o.Mode == ToolMode.Bypass);
        await chat.OpenSessionAsync(second);
        Assert.Equal((ToolMode.Ask, false), (chat.SelectedToolMode.Mode, chat.Grants.HasBypass));

        var (again, _, _) = Chat(gateway, sessions: sessions); // a fresh panel on the same device
        await again.InitializeAsync();
        await again.OpenSessionAsync(first);
        Assert.Equal((ToolMode.Bypass, true), (again.SelectedToolMode.Mode, again.Grants.HasBypass));
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Method == "DELETE"); // nothing revoked the running Bypass
        Assert.Equal("active", grants.Status(grant.Id));
        Assert.Single(grants.Made);
    }

    [Fact]
    public async Task The_chat_list_counts_the_time_left_on_each_tick()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        await SwitchOnAsync(chat, agent);
        gateway.Says("Done.");
        await chat.SendAsync("Rename the drawings.");
        await chat.RefreshSessionsAsync();
        var item = chat.Sessions.Single();
        Assert.Matches(@"^Bypass · (29|30) min left$", item.BypassText);

        grants.ServerNow = grants.ServerNow.AddMinutes(20);
        await agent.Grants.RefreshAsync(); // the copy reads the server's clock; its change ticks the markers

        Assert.Matches(@"^Bypass · (9|10) min left$", item.BypassText);
        grants.ServerNow = grants.ServerNow.AddMinutes(20);
        await agent.Grants.RefreshAsync();
        Assert.False(item.HasBypass);
        Assert.Equal("", item.BypassText);
    }

    [Fact]
    public void The_card_goes_from_pending_to_confirming_to_signing_in_and_back_to_confirming_before_it_is_on()
    {
        var opened = new List<Uri>();
        var (card, _) = Bare(opened);
        Assert.False(card.OpenAgainCommand.CanExecute(null)); // nothing to open yet

        card.AllowCommand.Execute(null);
        Assert.Equal((BypassCardState.Confirming, "Waiting for the server to confirm…"), (card.State, card.StateText));
        var request = Request();
        card.StepUp(request, StepUpStage.Browser);

        Assert.Equal((BypassCardState.SigningIn, true, true), (card.State, card.IsShown, card.IsSigningIn));
        const string signIn = "Sign in again in your browser to switch Bypass on. This app waits until you approve it there.";
        Assert.Equal((signIn, signIn), (card.SignInText, card.StateText));
        Assert.Equal("The page shows the code BCDF-GHJK.", card.SignInCodeText);
        Assert.Equal("Open the page again", card.OpenAgainText);
        Assert.Equal("Open the Bypass sign-in page again", card.OpenAgainName);
        Assert.False(card.AllowCommand.CanExecute(null));
        Assert.False(card.NotNowCommand.CanExecute(null));
        Assert.True(card.OpenAgainCommand.CanExecute(null));
        card.OpenAgainCommand.Execute(null);
        card.OpenAgainCommand.Execute(null);
        Assert.Equal(new[] { request.Link, request.Link }, opened);

        card.StepUp(request, StepUpStage.Approved);
        Assert.Equal((BypassCardState.Confirming, false), (card.State, card.OpenAgainCommand.CanExecute(null)));
        card.Confirmed();
        Assert.Equal((BypassCardState.On, false), (card.State, card.IsShown));
    }

    [Theory]
    [InlineData(StepUpStage.Denied, "Bypass stays off: it was not approved in the browser.")]
    [InlineData(StepUpStage.TimedOut, "Bypass stays off: the sign-in in the browser took too long.")]
    [InlineData(StepUpStage.Failed, "Bypass stays off: the browser could not be opened or the server not reached.")]
    public void A_sign_in_that_comes_to_nothing_ends_the_card_with_its_own_text_whether_reported_or_thrown(StepUpStage stage, string said)
    {
        var (reported, _) = Bare();
        reported.AllowCommand.Execute(null);
        reported.StepUp(Request(), StepUpStage.Browser);
        reported.StepUp(Request(), stage);
        Assert.Equal((BypassCardState.NotConfirmed, true, said), (reported.State, reported.IsShown, reported.StateText));
        Assert.False(reported.OpenAgainCommand.CanExecute(null));
        Assert.True(reported.CloseCommand.CanExecute(null));

        var (thrown, _) = Bare(); // the SDK throws it from StartBypassAsync too
        thrown.AllowCommand.Execute(null);
        thrown.NotConfirmed(new BypassSignInException(stage, "no"));
        Assert.Equal((BypassCardState.NotConfirmed, said), (thrown.State, thrown.StateText));
    }

    [Fact]
    public void A_sign_in_the_app_stopped_waiting_for_withdraws_the_card()
    {
        var (card, _) = Bare();
        card.AllowCommand.Execute(null);
        card.StepUp(Request(), StepUpStage.Browser);

        card.StepUp(Request(), StepUpStage.Cancelled);

        Assert.Equal((BypassCardState.Declined, false), (card.State, card.IsShown));
    }

    [Fact]
    public void A_report_for_a_card_nobody_allowed_changes_nothing()
    {
        var (card, _) = Bare();

        card.StepUp(Request(), StepUpStage.Browser);
        card.StepUp(Request(), StepUpStage.Denied);

        Assert.Equal(BypassCardState.Pending, card.State);
    }

    [Fact]
    public void The_reason_is_cut_at_a_characters_end_never_between_the_halves_of_an_emoji()
    {
        var (card, _) = Bare();

        card.Reason = new string('a', 499) + "\U0001F600" + "bbbb"; // the emoji is at UTF-16 positions 499 and 500

        Assert.Equal(499, card.Reason.Length);
        Assert.DoesNotContain(card.Reason, char.IsSurrogate);
        card.Reason = new string('a', 498) + "\U0001F600" + "bbbb"; // it fits: both halves stay
        Assert.Equal(500, card.Reason.Length);
    }

    [Fact]
    public async Task Allow_opens_the_second_sign_in_the_card_shows_it_and_bypass_is_on_once_the_person_approved_it()
    {
        var (gateway, grants) = Server(Offered());
        grants.PendingPolls = 1;
        var opened = new List<Uri>();
        var (chat, agent, _) = Chat(gateway, opened: opened);
        await chat.InitializeAsync();
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);
        Assert.Empty(opened); // nothing opens before the person's click
        Assert.Equal(0, grants.StepUpsStarted);

        card.AllowCommand.Execute(null);
        await Until(() => card.State == BypassCardState.SigningIn);

        Assert.Equal("The page shows the code BCDF-GHJK.", card.SignInCodeText);
        Assert.Single(opened);
        card.OpenAgainCommand.Execute(null);
        Assert.Equal(2, opened.Count);
        Assert.Equal(opened[0], opened[1]);
        Assert.Empty(grants.Made);
        Assert.True(await starting);
        Assert.Equal((BypassCardState.On, false), (card.State, card.IsShown));
        Assert.True(chat.Grants.HasBypass);
        Assert.Equal(ToolMode.Bypass, chat.SelectedToolMode.Mode);
        Assert.Single(grants.Made);
        Assert.Single(opened.Distinct());
        Assert.NotNull(agent.Grants.Live.Single());
    }

    [Theory]
    [InlineData("denied", "Bypass stays off: it was not approved in the browser.")]
    [InlineData("expired", "Bypass stays off: the sign-in in the browser took too long.")]
    public async Task When_the_person_does_not_approve_in_the_browser_the_card_says_so_and_nothing_is_granted(string outcome, string said)
    {
        var (gateway, grants) = Server(Offered());
        grants.StepUpOutcome = outcome;
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);

        card.AllowCommand.Execute(null);

        Assert.False(await starting);
        Assert.Equal((BypassCardState.NotConfirmed, true, said), (card.State, card.IsShown, card.StateText));
        Assert.Empty(grants.Made);
        Assert.Empty(agent.Grants.Live);
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.Null(chat.Error); // the card says it; no second message
    }

    [Fact]
    public async Task When_the_browser_cannot_be_opened_the_card_says_so()
    {
        var (gateway, grants) = Server(Offered());
        var broker = new ApprovalBroker();
        var (chat, _, _) = Chat(gateway, broker);
        broker.OpenBrowser = _ => false;
        await chat.InitializeAsync();
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);

        card.AllowCommand.Execute(null);

        Assert.False(await starting);
        Assert.Equal((BypassCardState.NotConfirmed, "Bypass stays off: the browser could not be opened or the server not reached."),
            (card.State, card.StateText));
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task Switch_on_again_is_a_button_on_the_ended_line_that_asks_once_and_only_on_its_click()
    {
        var (gateway, grants) = Server(Offered());
        var broker = new ApprovalBroker();
        var asked = 0;
        broker.GrantRequested += _ => asked++;
        var (chat, agent, _) = Chat(gateway, broker);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        Assert.Equal(1, asked);
        Assert.False(chat.Grants.SwitchOnAgainCommand.CanExecute(null)); // Bypass runs: there is no ended line

        grants.End(grant.Id, "admin");
        await agent.Grants.RefreshAsync();
        await Until(() => chat.Grants.HasBypassEnded);

        Assert.Equal(("Switch on again", "Switch Bypass on again"), (chat.Grants.SwitchOnAgainText, chat.Grants.SwitchOnAgainName));
        Assert.True(chat.Grants.SwitchOnAgainCommand.CanExecute(null));
        await Task.Delay(100);
        Assert.Equal(1, asked); // the line alone asks nothing
        var clicking = chat.Grants.SwitchOnAgainCommand.ExecuteAsync(null);
        var card = await Until(() => chat.Grants.BypassCard is { IsPending: true } pending ? pending : null);
        Assert.Equal(2, asked); // one click, one card; Allow and the second sign-in come from the card
        Assert.Single(grants.Made);
        card.NotNowCommand.Execute(null);
        await clicking;
        Assert.Equal(2, asked);
        Assert.Single(grants.Made);
        Assert.False(chat.Grants.HasBypassEnded); // the line goes once Bypass is asked for
    }

    [Fact]
    public async Task The_apps_end_pauses_bypass_and_the_chat_neither_ends_it_nor_says_it_ended()
    {
        var (gateway, grants) = Server(Offered());
        var (chat, agent, _) = Chat(gateway);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);

        Assert.Single(await agent.Grants.PauseBypassAsync()); // the app's end
        await Task.Delay(200); // the chat's handlers (the dispatcher runs them at once)

        Assert.Equal("paused", grants.Status(grant.Id)); // not ended: the next start resumes it
        Assert.False(chat.Grants.HasBypassEnded);
        Assert.False(chat.Grants.HasBypass); // nothing runs under it while paused

        Assert.Empty(await agent.Grants.ResumeBypassAsync()); // the next start
        await Until(() => chat.Grants.HasBypass);
        Assert.Equal(ToolMode.Bypass, chat.SelectedToolMode.Mode);
        Assert.Equal("active", grants.Status(grant.Id));
    }

    [Fact]
    public async Task Switch_on_again_is_off_while_the_server_does_not_offer_bypass()
    {
        var (gateway, grants) = Server(Offered());
        var sessions = new Fadenstack.Agents.Sessions.MemorySessionStore();
        var (chat, agent, _) = Chat(gateway, sessions: sessions);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        gateway.Says("Done.");
        await chat.SendAsync("Rename the drawings.");
        var id = chat.CurrentSessionId!;
        gateway.Approvals = Offered(allowed: false); // a panel that starts now reads a profile that no longer offers Bypass
        var (later, laterAgent, _) = Chat(gateway, sessions: sessions);
        await later.InitializeAsync();
        await later.OpenSessionAsync(id);
        Assert.True(later.Grants.HasBypass); // its grant still lives
        Assert.False(laterAgent.BypassOffered);

        grants.End(grant.Id, "kill_switch");
        await laterAgent.Grants.RefreshAsync();
        await Until(() => later.Grants.HasBypassEnded);

        Assert.False(later.Grants.SwitchOnAgainCommand.CanExecute(null));
    }

    private static readonly UiStrings English = UiStrings.For(CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>A card on its own, with an opener that records instead of opening a browser.</summary>
    private static (BypassPart Card, PendingGrant Pending) Bare(List<Uri>? opened = null)
    {
        var offer = new GrantOffer
        {
            Bypass = true, ConversationId = "c-1", Preselected = TimeSpan.FromMinutes(30),
            Durations = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) },
        };
        var pending = new PendingGrant(offer, CancellationToken.None);
        return (new BypassPart(pending, English, link =>
        {
            opened?.Add(link);
            return true;
        }), pending);
    }

    private static StepUpRequest Request() => new()
    {
        ConversationId = "c-1", Link = new Uri("https://faden.example/step-up#code=BCDF-GHJK"), UserCode = "BCDF-GHJK",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    [Fact]
    public async Task A_confirmation_shows_only_in_the_chat_of_its_conversation_and_one_no_chat_draws_is_never_allowed()
    {
        var (gateway, grants) = Server(Offered());
        var broker = new ApprovalBroker();
        var (first, agent, _) = Chat(gateway, broker);
        var (second, _, _) = Chat(gateway, broker);
        await first.InitializeAsync();
        await second.InitializeAsync();

        var starting = first.StartBypassAsync();
        var card = await Until(() => first.Grants.BypassCard);
        Assert.Null(second.Grants.BypassCard);
        card.NotNowCommand.Execute(null);
        Assert.False(await starting);

        var elsewhere = await agent.StartSessionAsync(ToolMode.Ask); // no chat shows it: nobody could confirm it
        var asking = elsewhere.StartBypassAsync();
        Assert.Same(asking, await Task.WhenAny(asking, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Null(await asking);
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task The_card_follows_the_panels_language()
    {
        var (gateway, _) = Server(Offered("results"));
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var starting = chat.StartBypassAsync();
        var card = await Until(() => chat.Grants.BypassCard);

        await chat.SetLanguageAsync("de");

        Assert.Equal("Freigaben in diesem Chat abschalten?", card.Title);
        Assert.StartsWith("Der Server Ihrer Organisation zeichnet jedes Tool auf", card.RecordsText);
        Assert.Equal("Bypass einschalten", card.AllowText);
        card.NotNowCommand.Execute(null);
        await starting;
    }
}

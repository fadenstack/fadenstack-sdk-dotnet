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
/// never comes back in Bypass.
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
        ApprovalBroker? broker = null, Fadenstack.Agents.Sessions.MemorySessionStore? sessions = null)
    {
        broker ??= new ApprovalBroker();
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
        Assert.Equal("15 min", card.SelectedDuration!.Label); // the shortest first (sdk/40 A3.3, default taken)
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
        Assert.Equal(("bypass", 15, "Renaming 400 drawings"), (made.Json.GetProperty("kind").GetString(),
            made.Json.GetProperty("minutes").GetInt32(), made.Json.GetProperty("reason").GetString()));
        Assert.Equal(64, made.Json.GetProperty("shown_text_sha256").GetString()!.Length);
        Assert.Equal((BypassCardState.On, false), (card.State, card.IsShown));
        Assert.Equal((ToolMode.Bypass, grant.Id), (chat.SelectedToolMode.Mode, chat.CurrentSessionId is { } id ? agent.Grants.BypassFor(id)?.Id : null));
        Assert.True(chat.Grants.HasBypass);
        Assert.Matches(@"^Bypass on · 1[45] min left$", chat.Grants.BypassText);
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
        grants.ServerNow = grants.ServerNow.AddMinutes(16);

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

        Assert.Matches(@"^Bypass in a chat, 1[45] min left · Each tool that runs is recorded\.$", row.Title);
        await row.RevokeCommand.ExecuteAsync(null);
        Assert.Equal("revoked", grants.Status(grant.Id));
        Assert.False(chat.Grants.HasBypass);
        await Until(() => chat.Grants.BypassEndedText == "Bypass ended: you ended it. Approvals ask again.");
        Assert.DoesNotContain(chat.Grants.Standing, s => s.IsBypass);
    }

    [Fact]
    public async Task Bypass_is_never_remembered_a_new_chat_and_a_chat_opened_again_start_without_it()
    {
        var (gateway, grants) = Server(Offered());
        var sessions = new Fadenstack.Agents.Sessions.MemorySessionStore();
        var (chat, agent, _) = Chat(gateway, sessions: sessions);
        await chat.InitializeAsync();
        var (_, grant) = await SwitchOnAsync(chat, agent);
        gateway.Says("Done.");
        await chat.SendAsync("Rename the drawings.");
        var id = chat.CurrentSessionId!;

        chat.NewChat();

        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        await Until(() => grants.Status(grant.Id) == "revoked"); // leaving the chat ends its Bypass
        var (again, _, _) = Chat(gateway, sessions: sessions);
        await again.InitializeAsync();
        await again.OpenSessionAsync(id);
        Assert.Equal(ToolMode.Ask, again.SelectedToolMode.Mode);
        Assert.False(again.Grants.HasBypass);
        Assert.Single(grants.Made);
    }

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

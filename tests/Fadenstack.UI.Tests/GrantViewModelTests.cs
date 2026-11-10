using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// Grants in the chat (sdk/38 §7, desktop 37 §5): "Allow for…" on the card, the grant card with its places, the marker
/// with Stop and "another 30 min", Settings → Approvals with Revoke, and the mode's limit said as what it is.
/// </summary>
public class GrantViewModelTests
{
    private static (AgentChatViewModel Chat, ApprovalBroker Broker, FadenstackAgent Agent) Chat(Gateway gateway, IApprovalStore? approvals = null,
        Fadenstack.Agents.Sessions.ISessionStore? sessions = null)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(sessions, approvals).AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        return (chat, broker, agent);
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

    [Fact]
    public async Task The_card_offers_its_times_and_an_answer_for_a_time_reads_as_such_now_and_when_the_chat_opens_again()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"Fine."}""")).Says("Done.");
        var grants = gateway.UseGrants("""{"standing":null,"longest_grant_minutes":60,"grant_classes":["read","write"]}""");
        var sessions = new Fadenstack.Agents.Sessions.MemorySessionStore();
        var (chat, _, agent) = Chat(gateway, sessions: sessions);
        await chat.InitializeAsync();

        var sending = chat.SendAsync("Comment.");
        var card = await Until(() => chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault());
        Assert.True(card.CanAllowForTime);
        Assert.Equal(new[] { "15 min", "30 min", "1 h" }, card.Durations.Select(d => d.Label));
        card.AllowForTimeCommand.Execute(card.Durations[1]);
        await sending;

        Assert.Equal((ApprovalState.ApprovedForTime, "Allowed for 30 min"), (card.State, card.StateText));
        Assert.Single(grants.Made);
        var (reopened, _, _) = Chat(gateway, sessions: sessions);
        await reopened.InitializeAsync();
        await reopened.OpenSessionAsync(chat.CurrentSessionId!);
        var again = reopened.Turns.SelectMany(t => t.Parts).OfType<ApprovalPart>().Single();
        Assert.Equal("Allowed for 30 min", again.StateText);
        agent.Dispose();
    }

    [Fact]
    public async Task The_grant_card_lists_the_places_one_can_be_taken_out_and_allow_sends_what_was_kept()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var (chat, _, agent) = Chat(gateway);
        await chat.InitializeAsync();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var report = GrantPlace.Document(@"C:\Docs\report.docx");

        var offering = session.OfferWorkAsync(new[] { report, GrantPlace.Site("https://purchase.example.de") }, "To fill in the order.");
        var card = await Until(() => chat.Grants.Cards.FirstOrDefault());
        Assert.Equal(("Work alongside you?", "I'll work in:", "30 min"), (card.Title, card.PlacesHeading, card.SelectedDuration!.Label));
        Assert.Equal("Without asking I may read, write and change things there.", card.MayText);
        card.Places[1].ToggleCommand.Execute(null);
        card.AllowCommand.Execute(null);
        var grant = await offering;

        Assert.Equal((GrantCardState.Allowed, "g-1"), (card.State, grant!.Id));
        var targets = grants.Made.Single().Json.GetProperty("targets");
        Assert.Equal((1, "document"), (targets.GetArrayLength(), targets[0].GetProperty("type").GetString()));
        Assert.Contains("report.docx", card.ShownText);
        Assert.DoesNotContain("purchase.example.de", card.ShownText);
    }

    [Fact]
    public async Task Not_now_makes_no_grant_and_a_card_with_every_place_taken_out_cannot_be_allowed()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var (chat, _, agent) = Chat(gateway);
        await chat.InitializeAsync();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        var offering = session.OfferWorkAsync(new[] { GrantPlace.Folder("/srv/share") });
        var card = await Until(() => chat.Grants.Cards.FirstOrDefault());
        card.Places[0].ToggleCommand.Execute(null);
        Assert.False(card.AllowCommand.CanExecute(null));
        card.NotNowCommand.Execute(null);

        Assert.Null(await offering);
        Assert.Equal(GrantCardState.Declined, card.State);
        Assert.Empty(grants.Made);
    }

    [Fact]
    public async Task The_marker_shows_while_a_stretch_of_work_runs_offers_more_time_near_the_end_and_stop_ends_it()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.Hold("work", new { classes = new[] { "write" }, targets = new[] { new { type = "document", identifiers = new[] { "/plan.docx" } } } }, minutes: 1);
        var (chat, _, agent) = Chat(gateway);

        await chat.InitializeAsync(); // reads the device's grants

        Assert.True(chat.Grants.HasMarker);
        Assert.StartsWith("Working alongside you · ", chat.Grants.MarkerText);
        Assert.True(chat.Grants.IsEnding);
        Assert.Equal("Another 30 min", chat.Grants.KeepGoingText);
        await chat.Grants.KeepGoingCommand.ExecuteAsync(null);
        Assert.Equal(30, grants.Extensions.Single().Json.GetProperty("minutes").GetInt32());
        Assert.False(chat.Grants.IsEnding);

        await chat.Grants.StopCommand.ExecuteAsync(null);

        Assert.Equal("revoked", grants.Status("g-1"));
        Assert.False(chat.Grants.HasMarker);
        Assert.Empty(agent.Grants.Live);
    }

    [Fact]
    public async Task The_marker_never_offers_more_time_for_a_bypass_grant_not_even_one_an_app_made_without_a_card()
    {
        var gateway = new Gateway { MaxLevel = "\"bypass\"" }.Says("Hi.");
        var grants = gateway.UseGrants(
            """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":60,"evidence":"calls"}}""");
        grants.LongestMinutes = 1; // it ends within the marker's "ending soon"
        var broker = new ApprovalBroker { OpenBrowser = _ => true }; // the second sign-in never opens a real browser
        using var agent = gateway.Builder().AddTools(new PageTools()).UsePermissionMode(PermissionMode.Bypass).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        await chat.InitializeAsync();

        await chat.SendAsync("Hi.");

        Assert.Equal(GrantKinds.Bypass, agent.Grants.Live.Single().Kind);
        Assert.False(chat.Grants.HasMarker);
        Assert.False(chat.Grants.KeepGoingCommand.CanExecute(null));
        await chat.Grants.KeepGoingCommand.ExecuteAsync(null);
        Assert.Empty(grants.Extensions);
        Assert.Empty(chat.Grants.Cards);
    }

    [Fact]
    public async Task Settings_lists_what_was_allowed_for_longer_than_one_call_and_revoke_ends_it()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        grants.Hold("time", new { tools = new[] { "add_comment" } });
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));
        var (chat, _, agent) = Chat(gateway, store);
        await chat.InitializeAsync();

        chat.IsSettingsOpen = true;
        await Until(() => chat.Grants.HasStanding ? chat.Grants.Standing : null);

        Assert.Equal(2, chat.Grants.Standing.Count);
        Assert.StartsWith("add_comment, ", chat.Grants.Standing[0].Title);
        Assert.Equal("read_page, always", chat.Grants.Standing[1].Title);
        await chat.Grants.Standing[0].RevokeCommand.ExecuteAsync(null);
        await chat.Grants.Standing.Single().RevokeCommand.ExecuteAsync(null);

        Assert.Equal("revoked", grants.Status("g-1"));
        Assert.Empty(await store.ListAsync(Gateway.Slug));
        Assert.False(chat.Grants.HasStanding);
        Assert.Equal("Nothing is allowed for longer than one call.", chat.Grants.StandingEmpty);
    }

    [Theory]
    [InlineData("\"ask\"", "\"ask\"", "This agent goes no higher than Ask.")]
    [InlineData("\"ask\"", "\"auto\"", "Limited to Ask by your organisation.")]
    public async Task The_limit_on_the_mode_says_whose_it_is(string ceiling, string max, string said)
    {
        var gateway = new Gateway(ceiling: ceiling) { MaxLevel = max }.Says("Hi.");
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();

        chat.SelectedToolMode = chat.ToolModes[2]; // Ask: allowed
        chat.SelectedToolMode = chat.ToolModes[3]; // Auto: not available, the pick stays
        Assert.DoesNotContain("Limited", chat.ToolModeHint);
        var hint = new AgentChatViewModelProbe(chat).HintFor(ToolMode.Auto);

        Assert.EndsWith(said, hint);
    }

    /// <summary>The hint as it reads for a pick above the limit (the picker keeps the pick within it).</summary>
    private sealed class AgentChatViewModelProbe
    {
        private readonly AgentChatViewModel _chat;

        public AgentChatViewModelProbe(AgentChatViewModel chat) => _chat = chat;

        public string HintFor(ToolMode mode)
        {
            var option = _chat.ToolModes.Single(o => o.Mode == mode);
            var field = typeof(AgentChatViewModel).GetField("_selectedMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var kept = field.GetValue(_chat);
            field.SetValue(_chat, option);
            try
            {
                return _chat.ToolModeHint;
            }
            finally
            {
                field.SetValue(_chat, kept);
            }
        }
    }
}

using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>"Stop everything" and the chat of an agent the organisation turned off: the states, the result line and its five languages.</summary>
public class StopEverythingTests
{
    private static UiStrings Strings(string tag) => UiStrings.For(CultureInfo.GetCultureInfo(tag));

    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    private static readonly string[] Keys =
    {
        "Notice_agent_disabled", "AgentTurnedOff", "AgentTurnedOffCheckAgain", "StopEverything", "StopEverythingTip", "StopEverythingBusy",
        "StopEverythingNothing", "StopEverythingChatsOne", "StopEverythingChatsMany", "StopEverythingGrantsOne", "StopEverythingGrantsMany",
        "StopEverythingBoth", "StopEverythingChatsOnly", "StopEverythingGrantsOnly", "StopEverythingNotTold", "GrantsEndedByOff",
        "BypassEnded_agent_turned_off",
    };

    [Fact]
    public void Every_new_text_exists_in_five_languages_and_the_consoles_words_are_kept()
    {
        foreach (var tag in Languages)
        {
            var s = Strings(tag);
            Assert.All(Keys, key => Assert.NotEqual(key, s[key]));
        }
        Assert.Equal("Your organisation turned this agent off.", Strings("en").AgentTurnedOff);
        Assert.Equal("Ihre Organisation hat diesen Agenten ausgeschaltet.", Strings("de").AgentTurnedOff);
        Assert.Equal("Votre organisation a éteint cet agent.", Strings("fr").AgentTurnedOff);
        Assert.Equal("Su organización apagó este agente.", Strings("es").AgentTurnedOff);
        Assert.Equal("您的组织已关闭此智能体。", Strings("zh-Hans").AgentTurnedOff);
    }

    [Theory]
    [InlineData(2, 3, 0, "Stopped 2 chats and ended 3 grants.")]
    [InlineData(1, 1, 0, "Stopped 1 chat and ended 1 grant.")]
    [InlineData(0, 0, 0, "Nothing was running.")]
    [InlineData(2, 0, 0, "Stopped 2 chats.")]
    [InlineData(0, 4, 0, "Ended 4 grants.")]
    [InlineData(1, 2, 1, "Stopped. The server could not be told about 1 grant; it ends at its time.")]
    public void The_result_line_says_what_the_stop_did(int turns, int grants, int notTold, string expected) =>
        Assert.Equal(expected, StopEverythingViewModel.Describe(new StopAllResult { TurnsStopped = turns, GrantsEnded = grants, GrantsNotTold = notTold }, Strings("en")));

    [Fact]
    public void The_result_line_is_empty_before_the_first_press_and_speaks_each_language()
    {
        Assert.Equal("", StopEverythingViewModel.Describe(null, Strings("en")));
        var result = new StopAllResult { TurnsStopped = 2, GrantsEnded = 3 };
        Assert.Equal("2 Chats gestoppt und 3 Freigaben beendet.", StopEverythingViewModel.Describe(result, Strings("de")));
        Assert.Equal("Arrêté : 2 conversations. Terminé : 3 autorisations.", StopEverythingViewModel.Describe(result, Strings("fr")));
        Assert.Equal("Detenido: 2 chats. Terminado: 3 permisos.", StopEverythingViewModel.Describe(result, Strings("es")));
        Assert.Equal("已停止 2 个聊天，并结束 3 项授权。", StopEverythingViewModel.Describe(result, Strings("zh-Hans")));
    }

    [Fact]
    public async Task Pressing_stops_at_once_with_no_confirmation_shows_busy_and_then_the_result()
    {
        var gate = new TaskCompletionSource<StopAllResult>();
        var asked = new List<int>();
        var model = new StopEverythingViewModel(() => new FadenstackAgent[0], list => { asked.Add(list.Count); return gate.Task; }, InlineDispatcher.Instance, () => Strings("en"));
        Assert.Equal("Stop everything", model.Name);
        Assert.Equal("", model.ResultText);

        var pressing = model.StopCommand.ExecuteAsync(null);

        Assert.True(model.IsBusy);
        Assert.False(model.StopCommand.CanExecute(null)); // not twice at once
        Assert.Equal("Stopping...", model.ResultText);
        gate.SetResult(new StopAllResult { TurnsStopped = 1, GrantsEnded = 2 });
        await pressing;
        Assert.False(model.IsBusy);
        Assert.Equal("Stopped 1 chat and ended 2 grants.", model.ResultText);
        Assert.Single(asked); // asked once, with no confirmation step
        Assert.True(model.StopCommand.CanExecute(null));
    }

    private static (StopEverythingViewModel Model, ManualClock Clock) Timed(Queue<StopAllResult> results, TimeSpan? clearAfter = null)
    {
        var clock = new ManualClock();
        var model = new StopEverythingViewModel(() => new FadenstackAgent[0], _ => Task.FromResult(results.Dequeue()), InlineDispatcher.Instance,
            () => Strings("en"), clock, clearAfter);
        return (model, clock);
    }

    [Fact]
    public async Task The_result_line_appears_after_the_stop_and_is_empty_after_8_seconds()
    {
        var (model, clock) = Timed(new Queue<StopAllResult>(new[] { new StopAllResult { TurnsStopped = 2, GrantsEnded = 3 } }));
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await model.StopCommand.ExecuteAsync(null);
        Assert.Equal("Stopped 2 chats and ended 3 grants.", model.ResultText);
        Assert.True(model.HasResult);

        clock.Advance(TimeSpan.FromMilliseconds(7999));
        Assert.True(model.HasResult);
        changed.Clear();
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal("", model.ResultText);
        Assert.False(model.HasResult);
        Assert.Null(model.Result);
        Assert.Contains(nameof(StopEverythingViewModel.ResultText), changed); // the view hears of it
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task A_new_press_shows_the_new_result_and_starts_the_time_again()
    {
        var (model, clock) = Timed(new Queue<StopAllResult>(new[] { new StopAllResult(), new StopAllResult { TurnsStopped = 1 } }));
        await model.StopCommand.ExecuteAsync(null);
        Assert.Equal("Nothing was running.", model.ResultText);
        clock.Advance(TimeSpan.FromSeconds(5));

        await model.StopCommand.ExecuteAsync(null);
        Assert.Equal("Stopped 1 chat.", model.ResultText);
        clock.Advance(TimeSpan.FromSeconds(5)); // 10 s after the first press, 5 s after the second
        Assert.Equal("Stopped 1 chat.", model.ResultText);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("", model.ResultText);
    }

    [Fact]
    public async Task The_time_is_an_option_zero_keeps_the_line_and_disposing_stops_the_timer()
    {
        var (shortModel, shortClock) = Timed(new Queue<StopAllResult>(new[] { new StopAllResult() }), TimeSpan.FromSeconds(1));
        await shortModel.StopCommand.ExecuteAsync(null);
        shortClock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(shortModel.HasResult);

        var (kept, keptClock) = Timed(new Queue<StopAllResult>(new[] { new StopAllResult() }), TimeSpan.Zero);
        await kept.StopCommand.ExecuteAsync(null);
        keptClock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(kept.HasResult);

        var (gone, goneClock) = Timed(new Queue<StopAllResult>(new[] { new StopAllResult() }));
        await gone.StopCommand.ExecuteAsync(null);
        Assert.Equal(1, goneClock.ActiveTimers);
        gone.Dispose();
        Assert.Equal(0, goneClock.ActiveTimers);
    }

    [Fact]
    public async Task A_stop_that_fails_after_the_local_part_still_ends_and_says_nothing_was_counted()
    {
        var model = new StopEverythingViewModel(() => new FadenstackAgent[0], _ => throw new HttpRequestException("down"), InlineDispatcher.Instance, () => Strings("en"));

        await model.StopCommand.ExecuteAsync(null);

        Assert.False(model.IsBusy);
        Assert.Equal("Nothing was running.", model.ResultText);
    }

    private static (AgentChatViewModel Chat, FadenstackAgent Agent, Gateway Gateway) OffChat(Gateway gateway)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("de-DE"),
        });
        return (chat, agent, gateway);
    }

    [Fact]
    public async Task A_chat_of_an_agent_that_is_off_shows_the_notice_disables_the_input_and_check_again_brings_it_back()
    {
        var (chat, agent, gateway) = OffChat(new Gateway().TurnOff().Says("Wieder da."));
        using var _ = agent;
        await chat.InitializeAsync();
        chat.Input = "Hallo";

        Assert.True(chat.IsAgentOff);
        Assert.Equal("Ihre Organisation hat diesen Agenten ausgeschaltet.", chat.AgentOffText);
        Assert.Equal("Erneut prüfen", chat.CheckAgainText);
        Assert.False(chat.IsInputEnabled);
        Assert.False(chat.CanSend);
        Assert.False(chat.SendCommand.CanExecute(null));

        gateway.TurnOn();
        await chat.CheckAgainCommand.ExecuteAsync(null);

        Assert.False(chat.IsAgentOff);
        Assert.True(chat.IsInputEnabled);
        Assert.True(chat.CanSend);
        Assert.Empty(gateway.RefusedWhileOff(0));
    }

    [Fact]
    public async Task The_turn_that_hits_the_switch_ends_with_the_same_sentence_in_the_panels_language_and_the_grants_view_says_they_ended()
    {
        var gateway = new Gateway();
        gateway.UseGrants();
        gateway.Then(_ =>
        {
            gateway.TurnOff();
            return Fadenstack.Client.Tests.FakeServer.Sse(Gateway.ToolCalls(("a", "read_page", """{"page":1}""")), Gateway.Finish("tool_calls"), Fadenstack.Client.Tests.FakeServer.Done);
        }).Says("Nie.");
        var (chat, agent, _) = OffChat(gateway);
        using var _ = agent;
        await chat.InitializeAsync();
        var changed = new List<string?>();
        chat.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await chat.SendAsync("Lies.");

        var notice = chat.Turns.Last().Parts.OfType<NoticePart>().Single();
        Assert.Equal("Ihre Organisation hat diesen Agenten ausgeschaltet.", notice.Message);
        Assert.Equal("Agent ausgeschaltet", notice.Title);
        Assert.Equal(ChatOutcome.AgentDisabled, chat.Turns.Last().Outcome);
        Assert.True(chat.IsAgentOff);
        Assert.Contains(nameof(AgentChatViewModel.IsAgentOff), changed);
        Assert.True(chat.Grants.IsAgentOff);
        Assert.Equal("Ihre Organisation hat diesen Agenten ausgeschaltet; seine Freigaben sind beendet.", chat.Grants.AgentOffGrantsText);
        Assert.False(chat.CanSend);
    }
}

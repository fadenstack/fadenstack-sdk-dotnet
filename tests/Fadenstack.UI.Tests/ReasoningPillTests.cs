using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The thinking pill's view model: the levels the profile offers, the pick, and what the next message carries.</summary>
public class ReasoningPillTests
{
    private static string Ui(string reasoning) => "{\"reasoning\":" + reasoning + "}";

    private static (AgentChatViewModel Chat, FadenstackAgent Agent) Chat(Gateway gateway, Action<AgentBuilder>? configure = null)
    {
        var broker = new ApprovalBroker();
        var builder = gateway.Builder().UseApprovals(broker);
        configure?.Invoke(builder);
        var agent = builder.Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero, AutomaticLanguage = new CultureInfo("en") };
        return (new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options), agent);
    }

    [Fact]
    public async Task Without_a_setting_there_are_five_levels_starting_at_auto()
    {
        var (chat, _) = Chat(new Gateway());
        await chat.InitializeAsync();
        Assert.Equal(new[] { "Auto", "Off", "Low", "Medium", "High" }, chat.ReasoningOptions.Select(o => o.Label));
        Assert.Equal(ReasoningLevel.Auto, chat.SelectedReasoning!.Level);
        Assert.Equal("Auto", chat.ReasoningPillText);
        Assert.Equal("Thinking: Auto", chat.ReasoningAutomationName);
        Assert.Contains("How long it thinks", chat.ReasoningToolTip);
        Assert.Contains("Thinking: Auto", chat.ReasoningToolTip);
        Assert.All(chat.ReasoningOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public async Task A_maximum_hides_the_levels_above_it_and_off_stays()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"max":"low"}""")));
        await chat.InitializeAsync();
        Assert.Equal(new[] { "Auto", "Off", "Low" }, chat.ReasoningOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task The_old_levels_and_default_of_a_profile_change_nothing()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"default":"none","levels":["none","high"]}""")));
        await chat.InitializeAsync();
        Assert.Equal(5, chat.ReasoningOptions.Count);
        Assert.Equal("Auto", chat.ReasoningPillText);
    }

    [Fact]
    public async Task A_stored_pick_above_the_maximum_shows_the_maximum()
    {
        var (chat, agent) = Chat(new Gateway(ui: Ui("""{"max":"low"}""")));
        await agent.StartAsync();
        typeof(FadenstackAgent).GetField("_reasoningPick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(agent, ReasoningLevel.High); // as left by an earlier run with no maximum
        await chat.InitializeAsync();
        Assert.Equal("Low", chat.ReasoningPillText);
        Assert.True(chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Low).IsSelected);
    }

    [Fact]
    public async Task The_pick_is_kept_and_the_next_message_carries_it()
    {
        var gateway = new Gateway().Says("One.").Says("Two.");
        var (chat, agent) = Chat(gateway);
        await chat.InitializeAsync();
        await chat.SendAsync("First");
        chat.SelectReasoningCommand.Execute(chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Low));
        Assert.False(chat.IsReasoningMenuOpen);
        Assert.Equal(ReasoningLevel.Low, agent.ReasoningChoice);
        await chat.SendAsync("Second");
        Assert.False(gateway.ChatRequests[0].Json.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("low", gateway.ChatRequests[1].Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_new_chat_starts_from_the_pick()
    {
        var gateway = new Gateway().Says("One.").Says("Two.");
        var (chat, _) = Chat(gateway);
        await chat.InitializeAsync();
        chat.SelectReasoningCommand.Execute(chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.High));
        await chat.SendAsync("First");
        chat.NewChat();
        Assert.Equal(ReasoningLevel.High, chat.SelectedReasoning!.Level);
        await chat.SendAsync("Second");
        Assert.Equal("high", gateway.ChatRequests[1].Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task The_texts_follow_the_panels_language()
    {
        var (chat, _) = Chat(new Gateway());
        await chat.InitializeAsync();
        chat.SetLanguageCommand.Execute("de");
        Assert.Equal("Auto", chat.ReasoningPillText);
        Assert.Equal("Nachdenken: Auto", chat.ReasoningAutomationName);
        chat.SelectReasoningCommand.Execute(chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Low));
        Assert.Equal("Niedrig", chat.ReasoningPillText);
        Assert.Equal("Niedrig", chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Low).Label);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("zh-Hans")]
    public void Every_level_has_a_label_and_a_line_in_every_language(string language)
    {
        var strings = new UiStrings(new CultureInfo(language));
        foreach (var level in ReasoningLevels.All)
        {
            Assert.DoesNotContain("Reasoning_", strings.ReasoningLabel(level));
            Assert.DoesNotContain("Reasoning_", strings.ReasoningDescription(level));
        }
        Assert.DoesNotContain("Reasoning", strings.ReasoningPill("x"));
        Assert.Contains("x", strings.ReasoningPill("x"));
        Assert.NotEqual("ReasoningMenu", strings.ReasoningMenu);
        Assert.Equal(language == "en", strings.ReasoningMenu == "How long it thinks");
    }
}

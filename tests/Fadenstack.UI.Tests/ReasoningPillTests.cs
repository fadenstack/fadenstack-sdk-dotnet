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
        Assert.False(chat.HasReasoningChoice); // not before it started
        await chat.InitializeAsync();
        Assert.True(chat.HasReasoningChoice);
        Assert.Equal(new[] { "Auto", "Off", "Low", "Medium", "High" }, chat.ReasoningOptions.Select(o => o.Label));
        Assert.Equal(ReasoningLevel.Auto, chat.SelectedReasoning!.Level);
        Assert.Equal("Auto", chat.ReasoningPillText);
        Assert.All(chat.ReasoningOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public async Task With_one_level_the_pill_is_hidden()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"levels":["high"]}""")));
        await chat.InitializeAsync();
        Assert.False(chat.HasReasoningChoice);
    }

    [Fact]
    public async Task With_off_and_one_other_level_the_pill_reads_off_and_on()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"default":"none","levels":["none","high"]}""")));
        await chat.InitializeAsync();
        Assert.True(chat.HasReasoningChoice);
        Assert.Equal(new[] { "Off", "On" }, chat.ReasoningOptions.Select(o => o.Label));
        Assert.Equal("Thinking: Off", chat.ReasoningPillText);
        chat.SelectReasoningCommand.Execute(chat.ReasoningOptions[1]);
        Assert.Equal("Thinking: On", chat.ReasoningPillText);
        Assert.Equal(ReasoningLevel.High, chat.SelectedReasoning!.Level);
    }

    [Fact]
    public async Task Two_levels_without_off_keep_their_own_names()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"levels":["low","high"]}""")));
        await chat.InitializeAsync();
        Assert.Equal(new[] { "Low", "High" }, chat.ReasoningOptions.Select(o => o.Label));
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
    public async Task The_apps_own_level_shows_even_when_the_profile_does_not_list_it()
    {
        var (chat, _) = Chat(new Gateway(ui: Ui("""{"levels":["none","high"]}""")), b => b.UseReasoning(ReasoningEffort.Medium));
        await chat.InitializeAsync();
        Assert.Equal("Medium", chat.ReasoningPillText);
        Assert.Equal(2, chat.ReasoningOptions.Count);
    }

    [Fact]
    public async Task The_texts_follow_the_panels_language()
    {
        var (chat, _) = Chat(new Gateway());
        await chat.InitializeAsync();
        chat.SetLanguageCommand.Execute("de");
        Assert.Equal("Auto", chat.ReasoningPillText);
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
            foreach (var asSwitch in new[] { false, true })
            {
                Assert.DoesNotContain("Reasoning_", strings.ReasoningLabel(level, asSwitch));
                Assert.DoesNotContain("Reasoning_", strings.ReasoningDescription(level, asSwitch));
            }
        }
        Assert.DoesNotContain("Reasoning", strings.ReasoningPill("x"));
        Assert.Contains("x", strings.ReasoningPill("x"));
        Assert.NotEqual("ReasoningMenu", strings.ReasoningMenu);
        Assert.Equal(language == "en", strings.ReasoningMenu == "How long it thinks");
    }
}

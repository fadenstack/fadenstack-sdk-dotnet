using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The approval card's "Allow for 1 hour": offered where the request offers it, answered in place, worded in every language.</summary>
public class TimeApprovalCardTests
{
    private static async Task<(AgentChatViewModel Chat, FadenstackAgent Agent)> OpenAsync(Gateway gateway)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        await chat.InitializeAsync();
        return (chat, agent);
    }

    private static async Task<ApprovalPart> CardAsync(AgentChatViewModel chat)
    {
        ApprovalPart? card = null;
        for (var i = 0; i < 500 && card is null; i++)
        {
            await Task.Delay(10);
            card = chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault();
        }
        return Assert.IsType<ApprovalPart>(card);
    }

    [Fact]
    public async Task With_the_feature_the_card_offers_for_one_hour_and_answers_in_place()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Added.");
        gateway.Features.Add(FadenstackAgent.TimeScopeFeature);
        var (chat, agent) = await OpenAsync(gateway);
        using var _ = agent;

        var sending = chat.SendAsync("Comment page 2.");
        var card = await CardAsync(chat);
        Assert.Equal((true, true, true, true), (card.CanAllowForSession, card.CanAllowForTime, card.CanAllowAlways, card.HasAllowChoices));
        Assert.True(card.AllowForTimeCommand.CanExecute(null));
        card.AllowForTimeCommand.Execute(null);
        await sending;

        Assert.Equal((ApprovalState.ApprovedForTime, "Allowed for 1 hour"), (card.State, card.StateText));
        Assert.False(card.AllowForTimeCommand.CanExecute(null));
        Assert.Equal("add_comment", Assert.Single(agent.TimeGrants).Tool);
    }

    [Fact]
    public async Task Without_the_feature_the_card_does_not_offer_it_and_the_command_does_nothing()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Added.");
        var (chat, agent) = await OpenAsync(gateway);
        using var _ = agent;

        var sending = chat.SendAsync("Comment page 2.");
        var card = await CardAsync(chat);
        Assert.Equal((true, false, true, true), (card.CanAllowForSession, card.CanAllowForTime, card.CanAllowAlways, card.HasAllowChoices));
        Assert.False(card.AllowForTimeCommand.CanExecute(null));
        card.AllowCommand.Execute(null);
        await sending;

        Assert.Equal(ApprovalState.Approved, card.State);
        Assert.Empty(agent.TimeGrants);
    }

    [Fact]
    public async Task A_destructive_call_has_no_drop_down_choices_even_with_the_feature()
    {
        var gateway = new Gateway().Calls(("call-1", "delete_page", """{"page":2}""")).Says("Not deleted.");
        gateway.Features.Add(FadenstackAgent.TimeScopeFeature);
        var (chat, agent) = await OpenAsync(gateway);
        using var _ = agent;

        var sending = chat.SendAsync("Delete page 2.");
        var card = await CardAsync(chat);
        Assert.Equal((false, false, false, false), (card.CanAllowForSession, card.CanAllowForTime, card.CanAllowAlways, card.HasAllowChoices));
        Assert.False(card.AllowForTimeCommand.CanExecute(null));
        card.DenyCommand.Execute(null);
        await sending;
    }

    [Theory]
    [InlineData("en", "Allow for 1 hour", "Allowed for 1 hour")]
    [InlineData("de", "Für 1 Stunde zulassen", "Für 1 Stunde zugelassen")]
    [InlineData("es", "Permitir durante 1 hora", "Permitido durante 1 hora")]
    [InlineData("fr", "Autoriser pendant 1 heure", "Autorisé pendant 1 heure")]
    [InlineData("zh-Hans", "允许 1 小时", "已允许 1 小时")]
    public void The_texts_exist_in_every_language(string language, string button, string answered)
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));

        Assert.Equal((button, answered), (strings.AllowForTime, strings.AllowedForTime));
        Assert.False(string.IsNullOrWhiteSpace(strings.AllowMore));
    }
}

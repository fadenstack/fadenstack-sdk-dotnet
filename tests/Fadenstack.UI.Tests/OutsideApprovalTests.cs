using System.Globalization;
using Fadenstack.Agents.Tests;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>An approval card for a tool that reaches outside the organisation (a web search) says so.</summary>
public class OutsideApprovalTests
{
    private static async Task<(string Label, string Reason)> CardAsync(AgentChatViewModel chat, string message)
    {
        var sending = chat.SendAsync(message);
        ApprovalPart? card = null;
        for (var i = 0; i < 500 && card is null; i++)
        {
            await Task.Delay(10);
            card = chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault();
        }
        Assert.NotNull(card);
        var seen = (card!.ClassLabel, card.Reason);
        card.AllowCommand.Execute(null);
        await sending;
        return seen;
    }

    [Fact]
    public async Task It_says_that_what_it_is_given_leaves_the_organisation_not_what_its_class_means()
    {
        var gateway = new Gateway(contract: WebTools.Contract)
            .Calls(("call-1", "search_web", """{"query":"pumps"}""")).Says("Found some.")
            .Calls(("call-2", "fill_field", """{"query":"pumps"}""")).Says("Filled.")
            .Calls(("call-3", "publish_post", """{"query":"pumps"}""")).Says("Published.");
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTools(new WebTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        await chat.InitializeAsync();

        Assert.Equal(("Goes outside", "What it is given leaves your organisation: it goes to another site."), await CardAsync(chat, "Search the web."));
        Assert.Equal(("Changes data", "It changes something, which can be changed back."), await CardAsync(chat, "Fill it in."));
        // A destructive one still says it cannot be undone.
        Assert.Equal(("Cannot be undone", "It may do something that cannot be undone."), await CardAsync(chat, "Publish it."));

        await chat.SetLanguageAsync("de");
        var card = chat.Turns[0].Parts.OfType<ApprovalPart>().Single();
        Assert.Equal(("Geht nach außen", "Was es bekommt, verlässt Ihre Organisation: Es geht an eine andere Website."), (card.ClassLabel, card.Reason));
    }
}

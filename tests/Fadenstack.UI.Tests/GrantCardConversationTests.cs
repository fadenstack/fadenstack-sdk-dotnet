using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// A grant card names the conversation it was offered for, and a chat gives access to its session (sdk/46 C3): chats sharing
/// one approval broker show only their own cards, and a host offers a grant through <see cref="AgentChatViewModel.Session"/>.
/// </summary>
public class GrantCardConversationTests
{
    private static (AgentChatViewModel Chat, ApprovalBroker Broker) ChatOn(FadenstackAgent agent, ApprovalBroker broker) =>
        (new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        }), broker);

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
    public async Task Chats_sharing_a_broker_show_only_the_cards_offered_for_their_own_conversation()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var broker = new ApprovalBroker();
        using var agent = gateway.Builder(new MemorySessionStore()).UseApprovals(broker).Build();
        var (first, _) = ChatOn(agent, broker);
        var (second, _) = ChatOn(agent, broker);
        await first.InitializeAsync();
        await second.InitializeAsync();
        Assert.Null(first.Session); // before its first message there is no session
        var one = await agent.StartSessionAsync(ToolMode.Ask);
        var two = await agent.StartSessionAsync(ToolMode.Ask);
        await first.OpenSessionAsync(one.Id);
        await second.OpenSessionAsync(two.Id);
        Assert.Equal((one.Id, two.Id), (first.Session!.Id, second.Session!.Id));

        var offering = first.Session.OfferWorkAsync(new[] { GrantPlace.Document(@"C:\Docs\report.docx") });
        var card = await Until(() => first.Grants.Cards.FirstOrDefault());

        Assert.Equal(one.Id, card.ConversationId);
        Assert.Empty(second.Grants.Cards); // the other chat does not show it
        card.AllowCommand.Execute(null);
        var grant = await offering;
        Assert.Equal(one.Id, grant!.ConversationId);
        Assert.Single(grants.Made);
        Assert.Empty(second.Grants.Cards);

        // An extension names the conversation too, and shows in its chat only.
        var extending = first.Session.OfferExtensionAsync(grant, TimeSpan.FromMinutes(30));
        var more = await Until(() => first.Grants.Cards.FirstOrDefault(c => c.IsExtension));
        Assert.Equal(one.Id, more.ConversationId);
        Assert.Empty(second.Grants.Cards);
        more.NotNowCommand.Execute(null);
        Assert.Null(await extending);
    }

    [Fact]
    public async Task A_card_offered_before_its_conversation_existed_shows_in_every_chat()
    {
        var gateway = new Gateway();
        gateway.UseGrants();
        var broker = new ApprovalBroker();
        using var agent = gateway.Builder(new MemorySessionStore()).UseApprovals(broker).Build();
        var (first, _) = ChatOn(agent, broker);
        var (second, _) = ChatOn(agent, broker);
        await first.InitializeAsync();
        await second.InitializeAsync();
        await first.OpenSessionAsync((await agent.StartSessionAsync(ToolMode.Ask)).Id);
        await second.OpenSessionAsync((await agent.StartSessionAsync(ToolMode.Ask)).Id);

        var asking = broker.RequestGrantAsync(new GrantOffer
        {
            Places = new[] { GrantPlace.Folder("/srv/share") }, Classes = new[] { "read" },
            Durations = new[] { TimeSpan.FromMinutes(30) }, Preselected = TimeSpan.FromMinutes(30),
        }, CancellationToken.None);

        var inFirst = await Until(() => first.Grants.Cards.FirstOrDefault());
        var inSecond = await Until(() => second.Grants.Cards.FirstOrDefault());
        Assert.Null(inFirst.ConversationId);
        inFirst.NotNowCommand.Execute(null);
        Assert.False((await asking).Allowed);
        Assert.NotNull(inSecond);
    }
}

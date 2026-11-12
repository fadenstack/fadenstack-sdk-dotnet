using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>A grant card names the conversation it was offered for (sdk/46 C3), so an app with several chats on one handler draws it in that chat only.</summary>
public class GrantOfferConversationTests
{
    private const string Offered = """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"]}""";

    private static readonly GrantPlace Report = GrantPlace.Document(@"C:\Docs\report.docx");

    [Fact]
    public async Task A_work_card_and_an_extension_card_name_the_conversation_they_were_offered_for()
    {
        var gateway = new Gateway { MaxLevel = "\"bypass\"" };
        gateway.UseGrants(Offered);
        var card = new BypassTests.Card();
        using var agent = gateway.Builder().UseApprovals(card).Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = await agent.Grants.GrantWorkAsync(new[] { Report }, TimeSpan.FromMinutes(30), session.Id);

        await session.OfferWorkAsync(new[] { Report });
        await session.OfferExtensionAsync(grant, TimeSpan.FromMinutes(30));

        Assert.Equal(2, card.Offers.Count);
        Assert.All(card.Offers, offer => Assert.Equal(session.Id, offer.ConversationId));
        Assert.NotNull(card.Offers[1].Extending);
    }
}

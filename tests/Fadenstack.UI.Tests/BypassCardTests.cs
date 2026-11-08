using Fadenstack.Agents;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The chat panel draws no Bypass card yet (sdk/40 A3): a Bypass offer is answered "not now", never drawn as a grant card.</summary>
public class BypassCardTests
{
    [Fact]
    public async Task A_bypass_offer_is_not_drawn_and_answered_not_now()
    {
        var broker = new ApprovalBroker();
        var drawn = 0;
        broker.GrantRequested += _ => drawn++;

        var answer = await broker.RequestGrantAsync(new GrantOffer { Bypass = true, Durations = new[] { TimeSpan.FromMinutes(30) } },
            CancellationToken.None);

        Assert.False(answer.Allowed);
        Assert.Equal(0, drawn);
    }
}

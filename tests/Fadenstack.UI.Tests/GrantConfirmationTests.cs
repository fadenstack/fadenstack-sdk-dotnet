using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The card of a grant for a time says what is true (sdk/43 §4, sdk/46 C6): "confirming" until the server answered, the
/// minutes the server confirmed once it did, "Stopped" when the turn was stopped meanwhile.
/// </summary>
public class GrantConfirmationTests
{
    private const string Approvals = """{"standing":null,"longest_grant_minutes":60,"grant_classes":["read","write"]}""";

    private static (AgentChatViewModel Chat, FadenstackAgent Agent) Chat(Gateway gateway, Fadenstack.Agents.Sessions.ISessionStore? sessions = null)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(sessions).AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        return (chat, agent);
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

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        return condition();
    }

    private static Gateway Server(out FakeGrants grants)
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"Fine."}""")).Says("Done.");
        grants = gateway.UseGrants(Approvals);
        return gateway;
    }

    [Fact]
    public async Task The_card_confirms_with_the_server_before_it_says_allowed_for_a_time_and_then_shows_the_minutes_the_server_confirmed()
    {
        var gateway = Server(out var grants);
        grants.LongestMinutes = 15; // the server cuts the 30 minutes picked
        grants.HoldCreate = new ManualResetEventSlim(false);
        var sessions = new Fadenstack.Agents.Sessions.MemorySessionStore();
        var (chat, agent) = Chat(gateway, sessions);
        await chat.InitializeAsync();

        var sending = chat.SendAsync("Comment.");
        var card = await Until(() => chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault());
        card.AllowForTimeCommand.Execute(card.Durations[1]);
        Assert.True(await Until(() => grants.Server.Requests.Any(r => r.Method == "POST" && r.Path.EndsWith("/grants"))));

        // The server has not answered: the card claims no grant, and is not a question any more.
        Assert.Equal((ApprovalState.Confirming, "Confirming permission with the server…", false, true),
            (card.State, card.StateText, card.IsPending, card.IsUnsettled));
        Assert.Equal(Fadenstack.UI.ViewModels.TurnState.WaitingForApproval, chat.Turns.Last().State);
        grants.HoldCreate.Set();
        await sending;

        Assert.Equal((ApprovalState.ApprovedForTime, "Server confirmed up to 15 min"), (card.State, card.StateText));
        var (reopened, _) = Chat(gateway, sessions);
        await reopened.InitializeAsync();
        await reopened.OpenSessionAsync(chat.CurrentSessionId!);
        Assert.Equal("Server confirmed up to 15 min", reopened.Turns.SelectMany(t => t.Parts).OfType<ApprovalPart>().Single().StateText);
        agent.Dispose();
    }

    [Fact]
    public async Task A_turn_stopped_while_the_server_confirms_leaves_the_card_stopped_not_unanswered()
    {
        var gateway = Server(out var grants);
        grants.HoldCreate = new ManualResetEventSlim(false);
        grants.CancelAfterHold = true;
        var (chat, agent) = Chat(gateway);
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Comment.");
        var card = await Until(() => chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault());
        card.AllowForTimeCommand.Execute(card.Durations[1]);
        Assert.True(await Until(() => grants.Server.Requests.Any(r => r.Method == "POST" && r.Path.EndsWith("/grants"))));

        chat.StopCommand.Execute(null);
        grants.HoldCreate.Set();
        await sending;

        Assert.Equal((ApprovalState.Stopped, "Stopped"), (card.State, card.StateText));
        agent.Dispose();
    }

    [Fact]
    public async Task A_card_waiting_for_the_person_when_the_turn_is_stopped_reads_stopped()
    {
        var gateway = Server(out _);
        var (chat, agent) = Chat(gateway);
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Comment.");
        var card = await Until(() => chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault());

        chat.StopCommand.Execute(null);
        await sending;

        Assert.Equal((ApprovalState.Stopped, "Stopped"), (card.State, card.StateText));
        agent.Dispose();
    }

    [Fact]
    public async Task The_card_says_what_a_time_grant_is_in_the_chats_language()
    {
        var gateway = Server(out _);
        var (chat, agent) = Chat(gateway);
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Comment.");
        var card = await Until(() => chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault());

        Assert.StartsWith("For a time allows this tool in your chats on this device", card.TimeGrantHint);
        var german = new UiStrings(CultureInfo.GetCultureInfo("de"));
        Assert.StartsWith("Die zeitlich begrenzte Freigabe", german.TimeGrantHint);
        Assert.Equal("Berechtigung wird beim Server bestätigt…", german.ConfirmingGrant);
        Assert.Equal("Vom Server für bis zu 15 min bestätigt", german.AllowedForConfirmed("15 min"));

        chat.StopCommand.Execute(null);
        await sending;
        agent.Dispose();
    }
}

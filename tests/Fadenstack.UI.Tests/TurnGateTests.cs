using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The host's gate on running turns (AgentChatOptions.TurnGate).</summary>
public class TurnGateTests
{
    private sealed class Lease : IDisposable
    {
        public int Disposed;

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private sealed class Releasing : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly Lease _lease;

        public Releasing(SemaphoreSlim slots, Lease lease)
        {
            _slots = slots;
            _lease = lease;
        }

        public void Dispose()
        {
            _lease.Dispose();
            _slots.Release();
        }
    }

    private static async Task<(AgentChatViewModel Chat, Gateway Gateway)> ChatAsync(Gateway gateway, Func<CancellationToken, Task<IDisposable>>? gate)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(new MemorySessionStore()).AddTools(new PageTools()).UseApprovals(broker).Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero, TurnGate = gate };
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options);
        await chat.InitializeAsync();
        return (chat, gateway);
    }

    private static async Task<T> WaitFor<T>(TurnViewModel turn) where T : ResponsePart
    {
        for (var i = 0; i < 500; i++)
        {
            try
            {
                if (turn.Parts.ToArray().OfType<T>().FirstOrDefault() is { } part)
                {
                    return part;
                }
            }
            catch (InvalidOperationException)
            {
                // changed while read
            }
            await Task.Delay(10);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    [Fact]
    public async Task A_gate_that_holds_keeps_the_turn_waiting_and_sends_nothing_until_it_is_released()
    {
        var release = new TaskCompletionSource<IDisposable>();
        var entered = new TaskCompletionSource();
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), async _ =>
        {
            entered.SetResult();
            return await release.Task;
        });

        var sending = chat.SendAsync("Go.");
        await entered.Task;

        var turn = Assert.Single(chat.Turns);
        Assert.True(chat.IsWaitingForSlot);
        Assert.True(chat.IsBusy);
        Assert.Equal(TurnState.Running, turn.State);
        Assert.Equal(chat.Strings.WaitingForSlot, turn.StatusText);
        Assert.Equal("Waiting for a free slot", chat.Strings.WaitingForSlot);
        Assert.False(sending.IsCompleted);
        await Task.Delay(100);
        Assert.Empty(gateway.ChatRequests);

        var lease = new Lease();
        release.SetResult(lease);
        await sending;

        Assert.False(chat.IsWaitingForSlot);
        Assert.Equal(TurnState.Completed, turn.State);
        Assert.NotEqual(chat.Strings.WaitingForSlot, turn.StatusText);
        Assert.Single(gateway.ChatRequests);
        Assert.Equal(1, lease.Disposed);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task The_lease_is_disposed_once_when_the_turn_completes()
    {
        var lease = new Lease();
        var (chat, _) = await ChatAsync(new Gateway().Says("Ok."), _ => Task.FromResult<IDisposable>(lease));

        await chat.SendAsync("Go.");

        Assert.Equal((TurnState.Completed, 1), (chat.Turns.Single().State, lease.Disposed));
        Assert.False(chat.IsWaitingForSlot);
    }

    [Fact]
    public async Task The_lease_is_disposed_once_when_the_turn_fails()
    {
        var lease = new Lease();
        var (chat, _) = await ChatAsync(new Gateway(), _ => Task.FromResult<IDisposable>(lease));

        await chat.SendAsync("Go.");

        Assert.Equal((TurnState.Failed, 1), (chat.Turns.Single().State, lease.Disposed));
    }

    [Fact]
    public async Task The_lease_is_disposed_once_when_the_turn_is_stopped_while_it_runs()
    {
        var lease = new Lease();
        var (chat, _) = await ChatAsync(new Gateway().Calls(("call-1", "stamp", "{}")), _ => Task.FromResult<IDisposable>(lease));

        var sending = chat.SendAsync("Stamp it.");
        await WaitFor<ApprovalPart>(chat.Turns.Single());
        Assert.Equal(0, lease.Disposed);
        chat.StopCommand.Execute(null);
        await sending;

        Assert.Equal((TurnState.Stopped, 1), (chat.Turns.Single().State, lease.Disposed));
    }

    [Fact]
    public async Task Stop_while_waiting_cancels_the_wait_and_sends_nothing()
    {
        var entered = new TaskCompletionSource();
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new Lease();
        });

        var sending = chat.SendAsync("Go.");
        await entered.Task;
        Assert.True(chat.IsWaitingForSlot);
        Assert.True(chat.StopCommand.CanExecute(null));
        chat.StopCommand.Execute(null);
        await sending;

        var turn = Assert.Single(chat.Turns);
        Assert.Equal(TurnState.Stopped, turn.State);
        Assert.False(chat.IsWaitingForSlot);
        Assert.False(chat.IsBusy);
        Assert.Null(chat.Error);
        Assert.Empty(gateway.ChatRequests);
        Assert.True(turn.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_gate_that_throws_fails_the_turn_and_sends_nothing()
    {
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), _ => throw new InvalidOperationException("No slots."));

        await chat.SendAsync("Go.");

        var turn = Assert.Single(chat.Turns);
        Assert.Equal((TurnState.Failed, "No slots."), (turn.State, turn.ErrorMessage));
        Assert.False(chat.IsWaitingForSlot);
        Assert.Empty(gateway.ChatRequests);
    }

    [Fact]
    public async Task Without_a_gate_nothing_waits()
    {
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), null);

        await chat.SendAsync("Go.");

        Assert.False(chat.IsWaitingForSlot);
        Assert.Equal(TurnState.Completed, chat.Turns.Single().State);
        Assert.Single(gateway.ChatRequests);
    }

    [Fact]
    public async Task Two_chats_sharing_a_gate_of_one_run_one_after_the_other()
    {
        var slots = new SemaphoreSlim(1);
        var leases = new List<Lease>();
        Func<CancellationToken, Task<IDisposable>> gate = async token =>
        {
            await slots.WaitAsync(token);
            var lease = new Lease();
            leases.Add(lease);
            return new Releasing(slots, lease);
        };
        var (first, firstGateway) = await ChatAsync(new Gateway().Calls(("call-1", "stamp", "{}")), gate);
        var (second, secondGateway) = await ChatAsync(new Gateway().Says("Ok."), gate);

        var firstSending = first.SendAsync("Stamp it.");
        await WaitFor<ApprovalPart>(first.Turns.Single());
        var secondSending = second.SendAsync("Go.");
        await Task.Delay(150);

        Assert.True(second.IsWaitingForSlot);
        Assert.False(first.IsWaitingForSlot);
        Assert.Empty(secondGateway.ChatRequests);

        first.StopCommand.Execute(null);
        await firstSending;
        await secondSending;

        Assert.Equal(TurnState.Stopped, first.Turns.Single().State);
        Assert.Equal(TurnState.Completed, second.Turns.Single().State);
        Assert.Single(secondGateway.ChatRequests);
        Assert.False(second.IsWaitingForSlot);
        Assert.Equal(2, leases.Count);
        Assert.All(leases, l => Assert.Equal(1, l.Disposed));
        Assert.Equal(1, slots.CurrentCount);
        Assert.NotEmpty(firstGateway.ChatRequests);
    }

    [Theory]
    [InlineData("en", "Waiting for a free slot")]
    [InlineData("de", "Wartet auf einen freien Platz")]
    [InlineData("es", "Esperando un hueco libre")]
    [InlineData("fr", "En attente d’une place libre")]
    [InlineData("zh-Hans", "等待空闲名额")]
    public void The_waiting_text_is_in_every_language(string language, string text) =>
        Assert.Equal(text, UiStrings.For(new CultureInfo(language)).WaitingForSlot);
}

using System.Diagnostics;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Time-limited content logging (8.1, OWASP ASVS 5.0 V16): content is off by default even where the app offered it, the person's start
/// switches it on for a set time, it ends at its time, and the spans and hooks follow the state at each call.
/// </summary>
[Collection("ActivityListeners")] // the spans test listens to the SDK's sources: no other agent run beside it
public class ContentLoggingTests
{
    private const string Turn1 = "MARBLE-TURN-ONE-81";
    private const string Turn2 = "MARBLE-TURN-TWO-82";
    private const string Turn3 = "MARBLE-TURN-THREE-83";

    /// <summary>A clock the test moves, and a timer it fires.</summary>
    private sealed class Rig
    {
        public DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public List<(TimeSpan After, Action Run, Timer Handle)> Timers { get; } = new();

        public Func<TimeSpan, Action, IDisposable> Schedule => (after, run) =>
        {
            var handle = new Timer();
            Timers.Add((after, run, handle));
            return handle;
        };

        public sealed class Timer : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }

    private static AgentBuilder Offered(Gateway gateway, Rig rig, params IAgentHooks[] hooks)
    {
        var builder = gateway.Builder().UseHookContent().UseOpenTelemetry(o => o.EnableSensitiveData = true);
        builder._contentLoggingClock = () => rig.Now;
        builder._contentLoggingSchedule = rig.Schedule;
        foreach (var hook in hooks)
        {
            builder.AddHooks(hook);
        }
        return builder;
    }

    private sealed class Spy : AgentHooks
    {
        public List<bool> SawMessages { get; } = new();

        public List<bool> SawText { get; } = new();

        public override Task BeforeModelCallAsync(ModelCallContext context, CancellationToken cancellationToken)
        {
            SawMessages.Add(context.Messages is not null);
            return Task.CompletedTask;
        }

        public override Task AfterModelCallAsync(ModelCallResult result, CancellationToken cancellationToken)
        {
            SawText.Add(result.Text is not null);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Offered_at_build_time_is_still_off_until_the_person_starts_it()
    {
        var rig = new Rig();
        using var agent = Offered(new Gateway(), rig).Build();

        Assert.True(agent.ContentLogging.Offered);
        Assert.True(agent.ContentLogging.CanStart);
        Assert.False(agent.ContentLogging.IsOn);
        Assert.Null(agent.ContentLogging.Until);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Start_and_stop_change_the_state_say_so_and_end_the_timer()
    {
        var rig = new Rig();
        using var agent = Offered(new Gateway(), rig).Build();
        var changes = 0;
        agent.ContentLogging.Changed += (_, _) => changes++;

        agent.StartContentLogging(TimeSpan.FromHours(4));
        Assert.True(agent.ContentLogging.IsOn);
        Assert.Equal(rig.Now.AddHours(4), agent.ContentLogging.Until);
        Assert.Equal(1, changes);
        Assert.Equal(TimeSpan.FromHours(4), Assert.Single(rig.Timers).After);

        agent.StopContentLogging();
        Assert.False(agent.ContentLogging.IsOn);
        Assert.Null(agent.ContentLogging.Until);
        Assert.Equal(2, changes);
        Assert.True(rig.Timers[0].Handle.Disposed);

        agent.StopContentLogging(); // off already: nothing happens
        Assert.Equal(2, changes);
        await Task.CompletedTask;
    }

    [Fact]
    public void It_ends_at_Until_by_the_clock_even_when_the_timer_never_fired()
    {
        var rig = new Rig();
        using var agent = Offered(new Gateway(), rig).Build();
        var changes = 0;
        agent.ContentLogging.Changed += (_, _) => changes++;
        agent.StartContentLogging(TimeSpan.FromMinutes(15));

        rig.Now = rig.Now.AddMinutes(14);
        Assert.True(agent.ContentLogging.IsOn);
        rig.Now = rig.Now.AddMinutes(1); // the machine slept: no timer fired, the clock says it is over
        Assert.False(agent.ContentLogging.IsOn);
        Assert.Null(agent.ContentLogging.Until);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void The_timer_ends_it_and_says_so()
    {
        var rig = new Rig();
        using var agent = Offered(new Gateway(), rig).Build();
        var changes = 0;
        agent.ContentLogging.Changed += (_, _) => changes++;
        agent.StartContentLogging(TimeSpan.FromHours(1));

        rig.Now = rig.Now.AddHours(1);
        rig.Timers[0].Run();

        Assert.Equal(2, changes);
        Assert.False(agent.ContentLogging.IsOn);
    }

    [Fact]
    public void A_timer_that_fires_early_waits_again()
    {
        var rig = new Rig();
        using var agent = Offered(new Gateway(), rig).Build();
        agent.StartContentLogging(TimeSpan.FromHours(1));

        rig.Now = rig.Now.AddMinutes(59);
        rig.Timers[0].Run();

        Assert.Equal(2, rig.Timers.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), rig.Timers[1].After);
        Assert.True(agent.ContentLogging.IsOn);
    }

    [Fact]
    public void A_new_agent_starts_off_content_logging_is_not_kept_across_restarts()
    {
        var rig = new Rig();
        using (var first = Offered(new Gateway(), rig).Build())
        {
            first.StartContentLogging(TimeSpan.FromHours(24));
        }
        using var second = Offered(new Gateway(), rig).Build();

        Assert.False(second.ContentLogging.IsOn);
    }

    [Fact]
    public void It_is_refused_when_the_app_did_not_offer_it()
    {
        using var agent = new Gateway().Builder().Build();

        var refused = Assert.Throws<ContentLoggingException>(() => agent.StartContentLogging(TimeSpan.FromHours(1)));
        Assert.Equal(ContentLoggingRefusal.NotOffered, refused.Reason);
        Assert.False(agent.ContentLogging.Offered);
        Assert.False(agent.ContentLogging.CanStart);
        Assert.False(agent.ContentLogging.IsOn);
    }

    [Theory]
    [InlineData("""{"contentLogging":"forbidden"}""")]
    [InlineData("""{"contentLogging":"nonsense"}""")] // an unknown value narrows
    public void It_is_refused_when_the_machine_policy_forbids_it(string policy)
    {
        using var agent = new Gateway().Builder().UseHookContent().UseMachinePolicy(MachinePolicy.FromJson(policy)).Build();

        var refused = Assert.Throws<ContentLoggingException>(() => agent.StartContentLogging(TimeSpan.FromHours(1)));
        Assert.Equal(ContentLoggingRefusal.ForbiddenByPolicy, refused.Reason);
        Assert.True(agent.ContentLogging.Offered);
        Assert.False(agent.ContentLogging.CanStart);
        Assert.False(agent.ContentLogging.IsOn);
    }

    [Fact]
    public void A_policy_that_cannot_be_read_forbids_it()
    {
        using var agent = new Gateway().Builder().UseHookContent().UseMachinePolicy(MachinePolicy.Unreadable).Build();

        Assert.Throws<ContentLoggingException>(() => agent.StartContentLogging(TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(24 * 60 + 1)]
    [InlineData(0)]
    public void The_time_is_fifteen_minutes_to_twenty_four_hours(int minutes)
    {
        using var agent = Offered(new Gateway(), new Rig()).Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => agent.StartContentLogging(TimeSpan.FromMinutes(minutes)));
        Assert.False(agent.ContentLogging.IsOn);
        agent.StartContentLogging(TimeSpan.FromMinutes(15));
        agent.StartContentLogging(TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task Spans_have_content_only_while_content_logging_is_on()
    {
        var rig = new Rig();
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Fadenstack.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                var all = string.Join(" | ", a.TagObjects.Select(t => $"{t.Key}={t.Value}")
                    .Concat(a.Events.SelectMany(e => new[] { e.Name }.Concat(e.Tags.Select(t => $"{t.Key}={t.Value}")))));
                lock (spans)
                {
                    spans.Add(all);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var agent = Offered(new Gateway().Says("a1").Says("a2").Says("a3"), rig).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        string[] Since(ref int from)
        {
            lock (spans)
            {
                var mine = spans.Skip(from).ToArray();
                from = spans.Count;
                return mine;
            }
        }

        var seenSoFar = 0;
        await session.SendAsync(Turn1); // off: the build-time switch alone puts nothing in a span
        var first = Since(ref seenSoFar);
        agent.StartContentLogging(TimeSpan.FromHours(1));
        await session.SendAsync(Turn2);
        var second = Since(ref seenSoFar);
        rig.Now = rig.Now.AddHours(1); // its time is up
        await session.SendAsync(Turn3);
        var third = Since(ref seenSoFar);

        // A later turn's model call carries the earlier messages too, so each turn's spans are looked at on their own.
        Assert.NotEmpty(first);
        Assert.DoesNotContain(first, s => s.Contains("MARBLE"));
        Assert.Contains(second, s => s.Contains(Turn2));
        Assert.NotEmpty(third);
        Assert.DoesNotContain(third, s => s.Contains("MARBLE"));
    }

    [Fact]
    public async Task Hooks_see_message_text_only_while_content_logging_is_on()
    {
        var rig = new Rig();
        var spy = new Spy();
        using var agent = Offered(new Gateway().Says("a1").Says("a2").Says("a3"), rig, spy).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await session.SendAsync(Turn1);
        agent.StartContentLogging(TimeSpan.FromHours(1));
        await session.SendAsync(Turn2);
        agent.StopContentLogging();
        await session.SendAsync(Turn3);

        Assert.Equal(new[] { false, true, false }, spy.SawMessages);
        Assert.Equal(new[] { false, true, false }, spy.SawText);
    }

    [Fact]
    public async Task Each_start_and_stop_is_logged_with_its_end_time_and_no_content()
    {
        var rig = new Rig();
        var logs = new CapturingLoggerFactory();
        using var agent = Offered(new Gateway().Says("a1"), rig).UseLogging(logs).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        agent.StartContentLogging(TimeSpan.FromHours(1));
        await session.SendAsync(Turn2);
        agent.StopContentLogging();

        var started = Assert.Single(logs.Entries, e => e.EventId == 2083);
        var stopped = Assert.Single(logs.Entries, e => e.EventId == 2084);
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Contains(started.Values, v => v.StartsWith("Minutes=60", StringComparison.Ordinal));
        Assert.Contains(started.Values, v => v.StartsWith("Until=", StringComparison.Ordinal) && v.Contains("2026"));
        Assert.Contains(stopped.Values, v => v == "How=stopped");
        Assert.DoesNotContain(logs.Texts, t => t.Contains("MARBLE"));
    }
}

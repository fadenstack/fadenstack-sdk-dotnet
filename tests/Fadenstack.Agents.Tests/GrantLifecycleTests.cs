using System.Net;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The rules that keep a server-held grant from running on unattended (sdk/46 G2; ported from the desktop app's
/// <c>BypassMarkerTests</c> and <c>WorkAlongsideTests</c>): the app's end pauses, the next start resumes under 30 minutes and
/// ends over, the lock pauses and ends after 30 minutes, Stop ends every grant, the end is near once, and the end says why.
/// The server is the fake gateway; time is a fake clock that moves the device's and the server's together.
/// </summary>
public class GrantLifecycleTests
{
    private const string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":120,"evidence":"calls"}}""";

    private const string Route = "/v1/agents/docs-assistant/grants";

    private static readonly GrantPlace Report = GrantPlace.Document(@"C:\Docs\report.docx");

    private sealed class Rig : IDisposable
    {
        private DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        private Rig(FadenstackAgent agent, FakeGrants server)
        {
            Agent = agent;
            Server = server;
            agent.Clock = () => _now;
            server.ServerNow = _now;
            Life = New();
        }

        public FadenstackAgent Agent { get; }

        public FakeGrants Server { get; }

        public GrantLifecycle Life { get; private set; }

        public List<GrantEndedEventArgs> Ended { get; } = new();

        public List<GrantEndingSoonEventArgs> Soon { get; } = new();

        public static async Task<Rig> StartAsync()
        {
            var gateway = new Gateway { MaxLevel = "\"bypass\"" };
            var server = gateway.UseGrants(Offered);
            var agent = gateway.Builder().Build();
            await agent.StartAsync();
            return new Rig(agent, server);
        }

        /// <summary>A lifecycle that ticks only when told, as the app's next run would build one.</summary>
        public GrantLifecycle New()
        {
            var life = new GrantLifecycle(Agent.Grants, new GrantLifecycleOptions { TickEvery = TimeSpan.Zero });
            life.Ended += (_, e) => Ended.Add(e);
            life.EndingSoon += (_, e) => Soon.Add(e);
            return life;
        }

        /// <summary>The app ends and starts again: the old lifecycle goes, a new one is built.</summary>
        public void Restart()
        {
            Life.Dispose();
            Ended.Clear();
            Soon.Clear();
            Life = New();
        }

        /// <summary>The device's clock and the server's move together.</summary>
        public void Advance(TimeSpan by)
        {
            _now += by;
            Server.ServerNow = _now;
        }

        public Task<AgentGrant> WorkAsync(int minutes = 30, string conversation = "c-1") =>
            Agent.Grants.GrantWorkAsync(new[] { Report }, TimeSpan.FromMinutes(minutes), conversation);

        /// <summary>A Bypass the server holds (the step-up is not under test here), read into the copy.</summary>
        public async Task<AgentGrant> BypassAsync(int minutes = 60)
        {
            var id = Server.Hold(GrantKinds.Bypass, new { classes = new[] { "read", "write", "destructive", "unknown" } }, minutes);
            await Agent.Grants.RefreshAsync();
            return Agent.Grants.Live.Single(g => g.Id == id);
        }

        public int Requests(string method, string suffix) =>
            Server.Server.Requests.Count(r => r.Method == method && r.Path.StartsWith(Route, StringComparison.Ordinal) && r.Path.EndsWith(suffix, StringComparison.Ordinal));

        public int Revokes => Server.Server.Requests.Count(r => r.Method == "DELETE");

        public int WhyReads => Server.Server.Requests.Count(r => r.Method == "GET" && r.Path.StartsWith(Route + "/g-", StringComparison.Ordinal));

        public void Dispose()
        {
            Life.Dispose();
            Agent.Dispose();
        }
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(done(), "It never came.");
    }

    // -- the app's end and the next start (Bypass and work alike) ---------------------------------------------------

    [Fact]
    public async Task The_apps_end_pauses_a_running_bypass_it_does_not_end_it()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);

        Assert.Equal(1, await rig.Life.AppEndingAsync());

        Assert.Equal("paused", rig.Server.Status(bypass.Id));
        Assert.Equal(0, rig.Revokes);
    }

    [Fact]
    public async Task Offline_the_apps_end_leaves_the_grant_running_at_the_server()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        rig.Server.PauseOffline = true;

        Assert.Equal(0, await rig.Life.AppEndingAsync());

        Assert.Equal("active", rig.Server.Status(bypass.Id));
        Assert.Equal(0, rig.Revokes);
    }

    [Fact]
    public async Task The_next_start_resumes_a_bypass_paused_for_less_than_30_minutes_with_no_card()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        await rig.Life.AppEndingAsync();
        rig.Advance(TimeSpan.FromMinutes(10));
        rig.Restart();

        var results = await rig.Life.AppStartedAsync();

        Assert.Equal((bypass.Id, GrantResumeOutcome.Resumed), (results.Single().Grant.Id, results.Single().Outcome));
        Assert.Equal("active", rig.Server.Status(bypass.Id));
        Assert.Equal(0, rig.Revokes);
        Assert.Empty(rig.Ended);
        Assert.Equal(TimeSpan.FromMinutes(110), rig.Agent.Grants.Remaining(rig.Agent.Grants.Live.Single()));
    }

    [Fact]
    public async Task The_next_start_ends_a_bypass_paused_for_30_minutes_or_more_and_says_so()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        await rig.Life.AppEndingAsync();
        rig.Advance(GrantLifecycle.PausedLimit);
        rig.Restart();

        var results = await rig.Life.AppStartedAsync();

        Assert.Equal(GrantResumeOutcome.EndedAway, results.Single().Outcome);
        Assert.Equal("revoked", rig.Server.Status(bypass.Id));
        Assert.Equal(0, rig.Requests("POST", "/resume"));
        var ended = Assert.Single(rig.Ended);
        Assert.Equal((GrantEndReason.Away, bypass.Id), (ended.Reason, ended.Grant.Id));
    }

    [Fact]
    public async Task A_work_grant_paused_by_the_apps_end_ends_at_the_next_start_and_says_it_worked_until_then()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        rig.Advance(TimeSpan.FromMinutes(5));
        await rig.Life.AppEndingAsync();
        rig.Advance(GrantLifecycle.PausedLimit);
        rig.Restart();

        await rig.Life.AppStartedAsync();

        Assert.Equal(0, rig.Requests("POST", "/resume"));
        var ended = Assert.Single(rig.Ended);
        Assert.Equal((GrantEndReason.Away, TimeSpan.FromMinutes(5)), (ended.Reason, ended.Ran)); // it worked until the app ended
        Assert.Equal("revoked", rig.Server.Status(grant.Id));
    }

    [Fact]
    public async Task The_apps_end_pauses_a_running_work_grant_and_the_next_start_resumes_it()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        await rig.Life.AppEndingAsync();
        Assert.Equal("paused", rig.Server.Status(grant.Id));
        rig.Advance(TimeSpan.FromMinutes(10));
        rig.Restart();

        await rig.Life.AppStartedAsync();

        Assert.Equal("active", rig.Server.Status(grant.Id));
        Assert.Equal(TimeSpan.FromMinutes(50), rig.Agent.Grants.Remaining(rig.Agent.Grants.Live.Single()));
    }

    [Theory]
    [InlineData("admin", GrantEndReason.Admin)]
    [InlineData("kill_switch", GrantEndReason.KillSwitch)]
    [InlineData("limit", GrantEndReason.Limit)]
    public async Task When_the_server_refuses_the_resume_it_says_why_from_the_servers_word(string by, GrantEndReason expected)
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        await rig.Life.AppEndingAsync();
        rig.Restart();
        // The server ends it as it refuses.
        rig.Server.Server.On("POST", $"{Route}/{bypass.Id}/resume", _ =>
        {
            rig.Server.End(bypass.Id, by);
            return FakeServer.Respond(HttpStatusCode.Forbidden, new { error = new { message = "Not now.", code = "bypass_not_allowed" } });
        });

        var results = await rig.Life.AppStartedAsync();

        Assert.Equal(GrantResumeOutcome.Refused, results.Single().Outcome);
        await UntilAsync(() => rig.Ended.Count == 1);
        Assert.Equal(expected, rig.Ended.Single().Reason);
        Assert.Empty(rig.Agent.Grants.Live); // the grant the server ended left the copy
    }

    [Fact]
    public async Task When_the_server_cannot_say_why_the_refusal_says_the_server_ended_it()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        await rig.Life.AppEndingAsync();
        rig.Restart();
        rig.Server.CanSayWhy = false;
        rig.Server.Server.On("POST", $"{Route}/{bypass.Id}/resume", _ =>
        {
            rig.Server.End(bypass.Id, "admin");
            return FakeServer.Respond(HttpStatusCode.Forbidden, new { error = new { message = "Not now.", code = "bypass_not_allowed" } });
        });

        await rig.Life.AppStartedAsync();

        await UntilAsync(() => rig.Ended.Count == 1);
        Assert.Equal(GrantEndReason.Elsewhere, rig.Ended.Single().Reason);
    }

    [Fact]
    public async Task When_the_server_cannot_check_the_grant_stays_paused_and_is_tried_at_every_tick()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        await rig.Life.AppEndingAsync();
        rig.Restart();
        rig.Server.RefuseResume = HttpStatusCode.ServiceUnavailable;

        var results = await rig.Life.AppStartedAsync();

        Assert.Equal(GrantResumeOutcome.Unavailable, results.Single().Outcome);
        Assert.Equal(1, rig.Life.Waiting);
        Assert.Equal("paused", rig.Server.Status(bypass.Id));
        await rig.Life.TickAsync();
        await rig.Life.TickAsync();
        Assert.Equal(3, rig.Requests("POST", "/resume")); // the start and two ticks

        rig.Server.RefuseResume = null; // the server can check now
        await rig.Life.TickAsync();

        Assert.Equal("active", rig.Server.Status(bypass.Id));
        Assert.Equal(0, rig.Life.Waiting);
        await rig.Life.TickAsync();
        Assert.Equal(4, rig.Requests("POST", "/resume")); // no more tries once it runs
    }

    [Fact]
    public async Task A_grant_found_running_at_the_start_runs_on()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(120);
        rig.Restart();

        var results = await rig.Life.AppStartedAsync();

        Assert.Empty(results);
        Assert.Equal("active", rig.Server.Status(bypass.Id));
        Assert.Equal((0, 0, 0), (rig.Revokes, rig.Requests("POST", "/pause"), rig.Requests("POST", "/resume")));
    }

    [Fact]
    public async Task Started_with_the_screen_locked_a_paused_grant_resumes_with_the_unlock()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        await rig.Life.AppEndingAsync();
        rig.Restart();
        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);

        var results = await rig.Life.AppStartedAsync();

        Assert.Equal(GrantResumeOutcome.WaitsForUnlock, results.Single().Outcome);
        Assert.Equal(0, rig.Requests("POST", "/resume"));
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);
        Assert.Equal("active", rig.Server.Status(grant.Id));
    }

    // -- Stop ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_ends_every_grant_of_the_device_a_paused_one_too_and_says_stopped_without_asking_the_server()
    {
        using var rig = await Rig.StartAsync();
        var first = await rig.WorkAsync(30, "c-1");
        var second = await rig.WorkAsync(30, "c-2");
        var bypass = await rig.BypassAsync(60);
        await rig.Agent.Grants.PauseAsync(second.Id);
        rig.Advance(TimeSpan.FromMinutes(5));

        var result = await rig.Life.StopAsync();

        Assert.Equal((3, 3, true), (result.Grants, result.Ended, result.Complete));
        Assert.All(new[] { first.Id, second.Id, bypass.Id }, id => Assert.Equal("revoked", rig.Server.Status(id)));
        Assert.Empty(rig.Agent.Grants.Live);
        Assert.Equal(3, rig.Ended.Count);
        Assert.All(rig.Ended, e => Assert.Equal(GrantEndReason.Stopped, e.Reason));
        Assert.Equal(TimeSpan.FromMinutes(5), rig.Ended.First(e => e.Grant.Id == first.Id).Ran);
        Assert.Equal(0, rig.WhyReads);
    }

    [Fact]
    public async Task Stop_still_ends_the_rest_when_one_grant_already_ended_at_the_server()
    {
        using var rig = await Rig.StartAsync();
        var first = await rig.WorkAsync(30, "c-1");
        var second = await rig.WorkAsync(30, "c-2");
        rig.Server.Server.On("DELETE", $"{Route}/{first.Id}", _ =>
            FakeServer.Respond(HttpStatusCode.NotFound, new { error = new { message = "You have no such grant.", code = "grant_not_found" } }));

        var result = await rig.Life.StopAsync();

        Assert.True(result.Complete);
        Assert.Equal("revoked", rig.Server.Status(second.Id));
    }

    [Fact]
    public async Task Stop_drops_a_grant_the_server_already_ended_from_the_running_list_at_once()
    {
        using var rig = await Rig.StartAsync();
        var first = await rig.WorkAsync(30, "c-1");
        var second = await rig.WorkAsync(30, "c-2");
        rig.Server.End(first.Id, "person");
        rig.Server.Server.On("DELETE", $"{Route}/{first.Id}", _ =>
            FakeServer.Respond(HttpStatusCode.NotFound, new { error = new { message = "You have no such grant.", code = "grant_not_found" } }));

        await rig.Life.StopAsync();

        // The device's copy reads the server's list again: no marker shows it until the next look (desktop app, 12 Nov 2026).
        Assert.DoesNotContain(rig.Life.Running, g => g.Id == first.Id);
        Assert.DoesNotContain(rig.Life.Running, g => g.Id == second.Id);
    }

    [Fact]
    public async Task A_stop_the_server_cannot_take_says_so_and_ends_nothing_said()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(30);
        rig.Server.RevokeOffline = true;

        var result = await rig.Life.StopAsync();

        Assert.Equal((1, 0, false), (result.Grants, result.Ended, result.Complete));
        Assert.Equal("active", rig.Server.Status(grant.Id));
        Assert.Empty(rig.Ended);
        Assert.Single(rig.Life.Running);
    }

    // -- near the end, and the end ---------------------------------------------------------------------------------

    [Fact]
    public async Task Two_minutes_before_the_end_it_says_so_once_and_again_only_after_more_time_was_added()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(30);
        rig.Advance(TimeSpan.FromMinutes(27));
        await rig.Life.TickAsync();
        Assert.Empty(rig.Soon);

        rig.Advance(TimeSpan.FromMinutes(1)); // two minutes left
        await rig.Life.TickAsync();
        var soon = Assert.Single(rig.Soon);
        Assert.Equal((grant.Id, TimeSpan.FromMinutes(2)), (soon.Grant.Id, soon.Left));

        rig.Advance(TimeSpan.FromSeconds(30));
        await rig.Life.TickAsync();
        await rig.Life.TickAsync();
        Assert.Single(rig.Soon); // once per end time

        var longer = await rig.Life.KeepGoingAsync(grant.Id);
        Assert.Equal(TimeSpan.FromMinutes(31.5), rig.Agent.Grants.Remaining(longer));
        await rig.Life.TickAsync();
        Assert.Single(rig.Soon);
        rig.Advance(TimeSpan.FromMinutes(30));
        await rig.Life.TickAsync();
        Assert.Equal(2, rig.Soon.Count); // the new end time asks once more
    }

    [Fact]
    public async Task Bypass_is_never_offered_more_time()
    {
        using var rig = await Rig.StartAsync();
        var bypass = await rig.BypassAsync(30);
        rig.Advance(TimeSpan.FromMinutes(29));

        await rig.Life.TickAsync();

        Assert.Empty(rig.Soon);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Life.KeepGoingAsync(bypass.Id));
    }

    [Fact]
    public async Task At_its_time_it_ends_with_one_event_and_no_revoke()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(15);

        rig.Advance(TimeSpan.FromMinutes(15));
        await rig.Life.TickAsync();
        await rig.Life.TickAsync();

        var ended = Assert.Single(rig.Ended);
        Assert.Equal((grant.Id, GrantEndReason.Time, TimeSpan.FromMinutes(15)), (ended.Grant.Id, ended.Reason, ended.Ran));
        Assert.Equal(0, rig.Revokes);
    }

    [Theory]
    [InlineData("person", GrantEndReason.Stopped)]
    [InlineData("admin", GrantEndReason.Admin)]
    [InlineData("kill_switch", GrantEndReason.KillSwitch)]
    [InlineData("limit", GrantEndReason.Limit)]
    public async Task A_grant_the_server_ended_says_why_by_the_servers_word_at_the_next_read_of_its_list(string by, GrantEndReason expected)
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(30);
        rig.Server.End(grant.Id, by);
        await rig.Life.TickAsync();
        Assert.Empty(rig.Ended); // the copy has not read the list yet

        rig.Advance(GrantLifecycle.RefreshEvery);
        await rig.Life.TickAsync();

        await UntilAsync(() => rig.Ended.Count == 1);
        Assert.Equal(expected, rig.Ended.Single().Reason);
        Assert.Empty(rig.Life.Running);
    }

    [Fact]
    public async Task When_the_server_cannot_say_why_the_end_says_the_server_ended_it()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(30);
        rig.Server.End(grant.Id, "admin");
        rig.Server.CanSayWhy = false; // offline, an older server

        rig.Advance(GrantLifecycle.RefreshEvery);
        await rig.Life.TickAsync();

        await UntilAsync(() => rig.Ended.Count == 1);
        Assert.Equal(GrantEndReason.Elsewhere, rig.Ended.Single().Reason);
    }

    // -- the lock --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Locking_the_screen_pauses_a_work_grant_and_unlocking_resumes_it()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(30);

        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);
        Assert.Equal("paused", rig.Server.Status(grant.Id));
        Assert.True(rig.Life.IsLocked);

        rig.Advance(TimeSpan.FromMinutes(10)); // its clock runs on while paused
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);

        Assert.Equal("active", rig.Server.Status(grant.Id));
        Assert.False(rig.Life.IsLocked);
        Assert.Equal(TimeSpan.FromMinutes(20), rig.Agent.Grants.Remaining(rig.Agent.Grants.Live.Single()));
    }

    [Fact]
    public async Task Locked_for_30_minutes_a_work_grant_ends_and_unlocking_does_not_resume_it()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);

        rig.Advance(GrantLifecycle.LockedFor - TimeSpan.FromSeconds(1));
        await rig.Life.TickAsync();
        Assert.Empty(rig.Ended);

        rig.Advance(TimeSpan.FromSeconds(1));
        await rig.Life.TickAsync();

        Assert.Equal("revoked", rig.Server.Status(grant.Id));
        var ended = Assert.Single(rig.Ended);
        Assert.Equal((GrantEndReason.Locked, TimeSpan.FromMinutes(30)), (ended.Reason, ended.Ran));
        Assert.True(rig.Life.IsLocked); // the host holds the summary until the person is back
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);
        Assert.Equal(0, rig.Requests("POST", "/resume"));
    }

    [Fact]
    public async Task What_the_lock_ended_but_could_not_tell_the_server_ends_at_the_unlock()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);
        rig.Server.RevokeOffline = true;
        rig.Advance(GrantLifecycle.LockedFor);
        await rig.Life.TickAsync();
        Assert.Equal("paused", rig.Server.Status(grant.Id));
        Assert.Empty(rig.Ended);

        rig.Server.RevokeOffline = false;
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);

        Assert.Equal("revoked", rig.Server.Status(grant.Id));
        Assert.Equal(GrantEndReason.Locked, Assert.Single(rig.Ended).Reason);
    }

    [Fact]
    public async Task A_lock_that_can_no_longer_be_read_keeps_the_grant_paused_until_the_screen_reads_unlocked()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);
        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);

        await rig.Life.SetScreenLockAsync(ScreenLockState.Unknown); // the bus went while the person is away

        Assert.True(rig.Life.IsLocked);
        Assert.Equal("paused", rig.Server.Status(grant.Id));
        Assert.Equal(0, rig.Requests("POST", "/resume"));
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);
        Assert.Equal("active", rig.Server.Status(grant.Id));
    }

    [Fact]
    public async Task A_lock_state_it_cannot_read_never_pauses_and_never_ends()
    {
        using var rig = await Rig.StartAsync();
        var grant = await rig.WorkAsync(60);

        await rig.Life.SetScreenLockAsync(ScreenLockState.Unknown);
        rig.Advance(GrantLifecycle.LockedFor);
        await rig.Life.TickAsync();

        Assert.Equal(0, rig.Requests("POST", "/pause"));
        Assert.Equal("active", rig.Server.Status(grant.Id));
        Assert.Empty(rig.Ended);
    }

    [Fact]
    public async Task The_lock_pauses_and_ends_a_stretch_of_work_but_leaves_bypass_to_the_apps_end()
    {
        using var rig = await Rig.StartAsync();
        var work = await rig.WorkAsync(120);
        var bypass = await rig.BypassAsync(120);

        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);
        Assert.Equal(("paused", "active"), (rig.Server.Status(work.Id), rig.Server.Status(bypass.Id)));

        rig.Advance(GrantLifecycle.LockedFor);
        await rig.Life.TickAsync();

        Assert.Equal(("revoked", "active"), (rig.Server.Status(work.Id), rig.Server.Status(bypass.Id)));
        Assert.Equal(GrantEndReason.Locked, Assert.Single(rig.Ended).Reason);
    }

    [Fact]
    public async Task What_the_host_adds_to_the_options_is_looked_after_too()
    {
        using var rig = await Rig.StartAsync();
        var time = await rig.Agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30));
        using var life = new GrantLifecycle(rig.Agent.Grants, new GrantLifecycleOptions
        {
            TickEvery = TimeSpan.Zero, Manages = g => g.Kind == GrantKinds.Time, PausedByLock = g => g.Kind == GrantKinds.Time,
        });

        await life.SetScreenLockAsync(ScreenLockState.Locked);

        Assert.Equal("paused", rig.Server.Status(time.Id));
        Assert.Equal(time.Id, Assert.Single(life.Running).Id);
    }
}

using System.Globalization;
using System.Net;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The marker for every grant of the device (sdk/46 G2; ported from the desktop's <c>BypassMarkerTests</c> and
/// <c>WorkAlongsideTests</c>, the parts that are not its own views): the time left by the server's clock, Stop, "another
/// 30 min" near the end, one summary per grant that says why, and the cards of C3. The rules themselves are tested in
/// <c>GrantLifecycleTests</c>.
/// </summary>
public class GrantMarkerTests
{
    private const string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":120,"evidence":"calls"}}""";

    private static readonly object WorkScope = new
    {
        classes = new[] { "read", "write" }, targets = new[] { new { type = "document", identifiers = new[] { @"C:\Docs\report.docx" } } },
    };

    private static readonly object BypassScope = new { classes = new[] { "read", "write", "destructive", "unknown" } };

    private sealed class Rig : IDisposable
    {
        public Gateway Gateway { get; } = new() { MaxLevel = "\"bypass\"" };

        public FakeGrants Server { get; }

        public FadenstackAgent Agent { get; }

        public GrantLifecycle Life { get; }

        public GrantMarkerViewModel Marker { get; }

        public List<string> StoppedTurns { get; } = new();

        public Rig(string language = "en", Func<string, string?>? chatTitle = null)
        {
            Server = Gateway.UseGrants(Offered);
            Agent = Gateway.Builder().Build();
            Life = new GrantLifecycle(Agent.Grants, new GrantLifecycleOptions { TickEvery = TimeSpan.Zero });
            Marker = new GrantMarkerViewModel(Life, InlineDispatcher.Instance, UiStrings.For(CultureInfo.GetCultureInfo(language)), chatTitle,
                StoppedTurns.Add);
        }

        public async Task StartAsync()
        {
            await Agent.StartAsync();
        }

        public async Task<string> HoldAsync(string kind, object scope, int minutes)
        {
            var id = Server.Hold(kind, scope, minutes);
            await Agent.Grants.RefreshAsync();
            return id;
        }

        public void Dispose()
        {
            Marker.Dispose();
            Life.Dispose();
            Agent.Dispose();
        }
    }

    [Fact]
    public async Task The_marker_shows_the_time_left_by_the_servers_clock_for_work_and_for_bypass()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        Assert.False(rig.Marker.IsOn);
        Assert.Equal(("", ""), (rig.Marker.MarkerText, rig.Marker.BadgeText));

        await rig.HoldAsync(GrantKinds.Work, WorkScope, 30);
        Assert.True(rig.Marker.IsOn);
        Assert.Equal(("Working alongside you · 30 min left", "30 min"), (rig.Marker.MarkerText, rig.Marker.BadgeText));
        Assert.True(rig.Marker.StopCommand.CanExecute(null));

        await rig.Life.StopAsync();
        await rig.HoldAsync(GrantKinds.Bypass, BypassScope, 60);
        Assert.Equal(("Bypass on · 1 h left", "1 h"), (rig.Marker.MarkerText, rig.Marker.BadgeText));
    }

    [Fact]
    public async Task With_several_grants_the_marker_counts_them_and_the_lines_name_each_with_its_chat()
    {
        using var rig = new Rig(chatTitle: id => id == "c-1" ? "Order form" : null);
        await rig.StartAsync();
        await rig.Agent.Grants.GrantWorkAsync(new[] { GrantPlace.Document(@"C:\Docs\report.docx") }, TimeSpan.FromMinutes(30), "c-1");
        await rig.HoldAsync(GrantKinds.Bypass, BypassScope, 60);

        Assert.Equal("2 grants running · first ends in 30 min", rig.Marker.MarkerText);
        Assert.Equal(new[] { "Working alongside you · Order form · 30 min left", "Bypass · A chat · 1 h left" }, rig.Marker.Entries.Select(e => e.Text));
        Assert.Equal(new[] { "c-1", null }, rig.Marker.Entries.Select(e => e.ConversationId));
    }

    [Fact]
    public async Task Stop_ends_every_grant_and_the_running_turns_and_leaves_one_summary_each_that_says_so()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.Agent.Grants.GrantWorkAsync(new[] { GrantPlace.Document(@"C:\Docs\report.docx") }, TimeSpan.FromMinutes(30), "c-1");
        var bypass = await rig.HoldAsync(GrantKinds.Bypass, BypassScope, 60);

        await rig.Marker.StopCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "c-1" }, rig.StoppedTurns);
        Assert.Empty(rig.Agent.Grants.Live);
        Assert.False(rig.Marker.IsOn);
        Assert.Equal(("", ""), (rig.Marker.MarkerText, rig.Marker.BadgeText));
        Assert.Equal(2, rig.Marker.Summaries.Count);
        var work = rig.Marker.Summaries.Single(s => s.Kind == GrantKinds.Work);
        Assert.Equal("Worked alongside you for 1 min", work.Title);
        Assert.Equal("Where: report.docx\nRan without asking: 0\nYou stopped it.", work.Detail);
        var ended = rig.Marker.Summaries.Single(s => s.GrantId == bypass);
        Assert.Equal(("Bypass ran for 1 min", "You stopped it."), (ended.Title, ended.Detail));

        work.DismissCommand.Execute(null);
        Assert.DoesNotContain(work, rig.Marker.Summaries);
    }

    [Fact]
    public async Task A_stop_the_server_cannot_take_is_said_and_the_marker_stays()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.HoldAsync(GrantKinds.Work, WorkScope, 30);
        rig.Server.RevokeOffline = true;

        await rig.Marker.StopCommand.ExecuteAsync(null);

        Assert.True(rig.Marker.IsOn);
        Assert.Equal("Stop did not reach the server. Try again; the grants end at their time in any case.", rig.Marker.Message);
        Assert.Empty(rig.Marker.Summaries);
        rig.Server.RevokeOffline = false;
        await rig.Marker.StopCommand.ExecuteAsync(null);
        Assert.Equal("", rig.Marker.Message);
        Assert.False(rig.Marker.IsOn);
    }

    [Fact]
    public async Task Near_the_end_it_offers_another_30_min_once_and_never_for_bypass()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var work = await rig.HoldAsync(GrantKinds.Work, WorkScope, 1);
        await rig.HoldAsync(GrantKinds.Bypass, BypassScope, 1);

        Assert.True(rig.Marker.IsEnding);
        Assert.Equal("Another 30 min", rig.Marker.KeepGoingText);
        Assert.Equal(new[] { true, false }, rig.Marker.Entries.OrderBy(e => e.Kind == GrantKinds.Bypass).Select(e => e.IsEnding));

        await rig.Marker.KeepGoingCommand.ExecuteAsync(null);

        Assert.Equal(work, rig.Server.Extensions.Single().Path.Split('/')[^2]);
        Assert.Equal(30, rig.Server.Extensions.Single().Json.GetProperty("minutes").GetInt32());
        Assert.False(rig.Marker.IsEnding);
        await rig.Marker.KeepGoingCommand.ExecuteAsync(null);
        Assert.Single(rig.Server.Extensions);
    }

    [Fact]
    public async Task More_time_the_server_refuses_is_said_in_words()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var work = await rig.HoldAsync(GrantKinds.Work, WorkScope, 1);
        rig.Server.Server.On("POST", $"/v1/agents/docs-assistant/grants/{work}/extend", _ => FakeServer.Respond(HttpStatusCode.Conflict,
            new { error = new { message = "At the longest.", type = "invalid_request_error", code = "grant_at_longest" } }));

        await rig.Marker.KeepGoingCommand.ExecuteAsync(work);

        Assert.Equal("This cannot run any longer. To keep going, ask again and answer the card.", rig.Marker.Message);
        rig.Marker.DismissMessageCommand.Execute(null);
        Assert.False(rig.Marker.HasMessage);
    }

    [Fact]
    public async Task A_paused_grant_shows_as_paused_and_a_summary_that_follows_the_lock_waits_until_the_person_is_back()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.HoldAsync(GrantKinds.Work, WorkScope, 60);

        await rig.Life.SetScreenLockAsync(ScreenLockState.Locked);
        Assert.True(rig.Marker.IsPaused);
        Assert.Equal(("Working alongside you · paused", "Paused"), (rig.Marker.MarkerText, rig.Marker.BadgeText));

        // 31 minutes pass by the server's clock; its next answer shows it.
        rig.Server.ServerNow += TimeSpan.FromMinutes(31);
        await rig.Agent.Grants.RefreshAsync();
        await rig.Life.TickAsync();

        Assert.False(rig.Marker.IsOn);
        Assert.Empty(rig.Marker.Summaries); // the screen is locked: nobody reads it
        await rig.Life.SetScreenLockAsync(ScreenLockState.Unlocked);
        var summary = Assert.Single(rig.Marker.Summaries);
        Assert.Equal(GrantEndReason.Locked, summary.Reason);
        Assert.EndsWith("It ended because the screen stayed locked for 30 minutes.", summary.Detail);
    }

    [Fact]
    public async Task A_grant_that_ends_by_its_time_says_so_and_the_texts_follow_the_language()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.HoldAsync(GrantKinds.Work, WorkScope, 15);
        rig.Marker.Retext(UiStrings.For(CultureInfo.GetCultureInfo("de")));

        Assert.Equal(("Arbeitet mit Ihnen · noch 15 Min.", "Stoppen", "Weitere 30 Min."), (rig.Marker.MarkerText, rig.Marker.StopText, rig.Marker.KeepGoingText));

        rig.Server.ServerNow += TimeSpan.FromMinutes(16);
        await rig.Agent.Grants.RefreshAsync();
        await rig.Life.TickAsync();

        var summary = Assert.Single(rig.Marker.Summaries);
        Assert.Equal("Sie endete, als ihre Zeit um war.", summary.Detail.Split('\n')[^1]);
    }
}

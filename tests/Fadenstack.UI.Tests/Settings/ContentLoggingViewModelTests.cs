using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Logging;
using Fadenstack.Storage;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>Content logging's setting and band (8.1): only when offered and allowed, a time to choose, the end time, Start and Stop.</summary>
public class ContentLoggingViewModelTests
{
    private static readonly UiStrings English = UiStrings.For(new CultureInfo("en"));

    private static string TimeOf(DateTimeOffset until, DateTimeOffset now) =>
        until.ToLocalTime().Date == now.ToLocalTime().Date ? until.ToLocalTime().ToString("t", English.FormatCulture) : until.ToLocalTime().ToString("g", English.FormatCulture);

    [Fact]
    public void The_setting_is_there_only_when_the_app_offers_it_and_the_policy_allows_it()
    {
        Assert.False(new ContentLoggingSettingsViewModel(FakeContentLogging.Of("unavailable"), strings: () => English).IsAvailable);
        Assert.True(new ContentLoggingSettingsViewModel(FakeContentLogging.Of("off"), strings: () => English).IsAvailable);

        using var notOffered = new Gateway().Builder().Build();
        using var forbidden = new Gateway().Builder().UseHookContent().UseMachinePolicy(MachinePolicy.FromJson("""{"contentLogging":"forbidden"}""")).Build();
        using var offered = new Gateway().Builder().UseHookContent().Build();
        Assert.False(new ContentLoggingSettingsViewModel(notOffered, strings: () => English).IsAvailable);
        Assert.False(new ContentLoggingSettingsViewModel(forbidden, strings: () => English).IsAvailable);
        Assert.True(new ContentLoggingSettingsViewModel(offered, strings: () => English).IsAvailable);
    }

    [Fact]
    public void It_offers_one_hour_four_hours_and_a_day_and_starts_with_one_hour()
    {
        var source = FakeContentLogging.Of("off");
        var model = new ContentLoggingSettingsViewModel(source, strings: () => English);

        Assert.Equal(new[] { "1 hour", "4 hours", "24 hours" }, model.Choices.Select(c => c.Text));
        Assert.Equal(TimeSpan.FromHours(1), model.SelectedChoice.Duration);
        Assert.Equal(English.ContentLoggingIntro, model.IntroText);
        Assert.Contains("logs", model.IntroText);
        Assert.Contains("by itself", model.IntroText);
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.False(model.StopCommand.CanExecute(null));
        Assert.Equal("", model.OnUntilText);
    }

    [Fact]
    public void Start_switches_it_on_for_the_chosen_time_and_says_until_when_and_Stop_switches_it_off()
    {
        var source = FakeContentLogging.Of("off");
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var model = new ContentLoggingSettingsViewModel(source, strings: () => English, clock: () => now);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.SelectedChoice = model.Choices[1];
        model.StartCommand.Execute(null);

        Assert.Equal(TimeSpan.FromHours(4), Assert.Single(source.Started));
        Assert.True(model.IsOn);
        Assert.False(model.CanStart);
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.True(model.StopCommand.CanExecute(null));
        Assert.Equal(English.ContentLoggingOnUntil(TimeOf(source.Until!.Value, now)), model.OnUntilText);
        Assert.Contains(nameof(ContentLoggingSettingsViewModel.IsOn), changed);

        model.StopCommand.Execute(null);

        Assert.False(model.IsOn);
        Assert.Equal("", model.OnUntilText);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public void An_end_that_is_not_today_is_said_with_its_date()
    {
        var source = FakeContentLogging.Of("off");
        var now = DateTimeOffset.UtcNow;
        var model = new ContentLoggingSettingsViewModel(source, strings: () => English, clock: () => now.AddDays(-2));

        model.StartCommand.Execute(null);

        Assert.Equal(English.ContentLoggingOnUntil(source.Until!.Value.ToLocalTime().ToString("g", English.FormatCulture)), model.OnUntilText);
    }

    [Fact]
    public void A_change_from_another_thread_goes_through_the_dispatcher()
    {
        var source = FakeContentLogging.Of("on");
        var dispatcher = new QueuedDispatcher();
        var model = new ContentLoggingSettingsViewModel(source, dispatcher, () => English);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        source.EndByItself();
        Assert.Empty(raised); // nothing yet: the UI thread has not run it
        dispatcher.Pump();

        Assert.Contains(nameof(ContentLoggingSettingsViewModel.IsOn), raised);
        Assert.False(model.IsOn);
    }

    [Fact]
    public void A_new_language_says_every_text_again_and_keeps_the_choice()
    {
        var language = English;
        var model = new ContentLoggingSettingsViewModel(FakeContentLogging.Of("off"), strings: () => language);
        model.SelectedChoice = model.Choices[2];

        language = UiStrings.For(new CultureInfo("de"));
        model.Retext();

        Assert.Equal(new[] { "1 Stunde", "4 Stunden", "24 Stunden" }, model.Choices.Select(c => c.Text));
        Assert.Equal("24 Stunden", model.SelectedChoice.Text);
        Assert.Equal(language.ContentLoggingIntro, model.IntroText);
    }

    [Fact]
    public void The_band_shows_only_while_it_is_on_and_says_until_when_with_a_Stop()
    {
        var source = FakeContentLogging.Of("off");
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var band = new ContentLoggingBandViewModel(source, strings: () => English, clock: () => now);
        Assert.False(band.IsShown);
        Assert.Equal("", band.Text);
        Assert.False(band.StopCommand.CanExecute(null));

        source.Start(TimeSpan.FromHours(1));

        Assert.True(band.IsShown);
        Assert.Equal($"Content logging is on until {TimeOf(source.Until!.Value, now)}. Prompts and answers are written to the app's logs.", band.Text);
        Assert.Equal("Stop", band.StopText);
        Assert.Equal("Stop content logging", band.StopName);
        Assert.True(band.StopCommand.CanExecute(null));

        band.StopCommand.Execute(null);

        Assert.Equal(1, source.Stops);
        Assert.False(band.IsShown);
    }

    [Fact]
    public void The_band_goes_when_the_time_runs_out()
    {
        var source = FakeContentLogging.Of("on");
        var band = new ContentLoggingBandViewModel(source, strings: () => English);
        Assert.True(band.IsShown);

        source.EndByItself();

        Assert.False(band.IsShown);
    }

    [Fact]
    public async Task The_chat_has_the_band_and_it_follows_the_agent()
    {
        var broker = new ApprovalBroker();
        using var agent = new Gateway().Builder().UseHookContent().UseApprovals(broker).Build();
        using var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero, Strings = English });
        Assert.False(chat.ContentLogging.IsShown);

        agent.StartContentLogging(TimeSpan.FromHours(1));
        Assert.True(chat.ContentLogging.IsShown);
        Assert.StartsWith("Content logging is on until ", chat.ContentLogging.Text);
        chat.ContentLogging.StopCommand.Execute(null);
        Assert.False(agent.ContentLogging.IsOn);
        Assert.False(chat.ContentLogging.IsShown);
        await Task.CompletedTask;
    }

    [Fact]
    public void Every_text_exists_in_every_language()
    {
        foreach (var tag in new[] { "en", "de", "fr", "es", "zh-Hans" })
        {
            var s = UiStrings.For(new CultureInfo(tag));
            foreach (var text in new[]
            {
                s.ContentLoggingTitle, s.ContentLoggingBand("14:30"), s.ContentLoggingIntro, s.ContentLoggingDuration, s.ContentLoggingOneHour,
                s.ContentLoggingFourHours, s.ContentLoggingDay, s.ContentLoggingStart, s.ContentLoggingStartName, s.ContentLoggingStopName,
                s.ContentLoggingOnUntil("14:30"), s.MarkerStop,
            })
            {
                Assert.False(string.IsNullOrWhiteSpace(text), tag);
            }
            Assert.Contains("14:30", s.ContentLoggingBand("14:30"));
            Assert.Contains("14:30", s.ContentLoggingOnUntil("14:30"));
            // WCAG 2.5.3: the button's name contains its visible text.
            Assert.Contains(s.MarkerStop, s.ContentLoggingStopName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(s.ContentLoggingStart, s.ContentLoggingStartName, StringComparison.OrdinalIgnoreCase);
            if (tag != "en")
            {
                Assert.NotEqual(UiStrings.For(new CultureInfo("en")).ContentLoggingIntro, s.ContentLoggingIntro);
            }
        }
    }

    /// <summary>A file logger alive in the process, in a folder of its own that goes with the test.</summary>
    private sealed class Logger : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-cl-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable _root;
        private readonly List<FadenstackFileLoggerProvider> _providers = new();

        public Logger() => _root = FadenstackPaths.UseRootForTests(_folder);

        public void Add(string app, TimeSpan? maxAge = null) =>
            _providers.Add(new FadenstackFileLoggerProvider(app, maxAge is null ? null : new FadenstackFileLoggerOptions { MaxAge = maxAge.Value }));

        public void DisposeAll()
        {
            foreach (var provider in _providers)
            {
                provider.Dispose();
            }
            _providers.Clear();
        }

        public void Dispose()
        {
            DisposeAll();
            _root.Dispose();
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void With_a_file_log_in_the_process_the_text_says_how_many_days_what_was_written_stays()
    {
        using var logs = new Logger();
        logs.Add("one");
        var model = new ContentLoggingSettingsViewModel(FakeContentLogging.Of("off"), strings: () => English);

        Assert.Equal(English.ContentLoggingIntroDays(14), model.IntroText);
        Assert.Contains("stays in the app's logs for up to 14 days.", model.IntroText);
        Assert.Contains("by itself", model.IntroText);

        logs.Add("two", TimeSpan.FromDays(30.9)); // the longest of them, in whole days
        Assert.Contains("for up to 30 days.", model.IntroText);

        logs.DisposeAll();
        logs.Add("three", TimeSpan.FromDays(3));
        Assert.Contains("for up to 3 days.", model.IntroText);

        logs.DisposeAll();
        logs.Add("four", TimeSpan.FromDays(1)); // one day reads "up to a day", not "1 days"
        Assert.Equal(English.ContentLoggingIntroOneDay, model.IntroText);
        Assert.Contains("for up to a day.", model.IntroText);
        Assert.DoesNotContain("1 days", model.IntroText);

        logs.DisposeAll(); // the logger goes: nothing says how long it stays any more
        Assert.Equal(English.ContentLoggingIntro, model.IntroText);
    }

    [Fact]
    public void Without_a_file_log_the_text_says_it_stays_until_the_app_deletes_it()
    {
        var model = new ContentLoggingSettingsViewModel(FakeContentLogging.Of("off"), strings: () => English);

        Assert.Equal(English.ContentLoggingIntro, model.IntroText);
        Assert.EndsWith("What was written stays in the app's logs until the app deletes it.", model.IntroText);
        Assert.DoesNotContain("{0}", model.IntroText);
    }

    [Fact]
    public void The_German_text_says_it_in_both_cases()
    {
        var german = UiStrings.For(new CultureInfo("de"));
        var model = new ContentLoggingSettingsViewModel(FakeContentLogging.Of("off"), strings: () => german);
        Assert.EndsWith("Was geschrieben wurde, bleibt in den Protokollen der App, bis die App es löscht.", model.IntroText);

        using var logs = new Logger();
        logs.Add("one");
        Assert.EndsWith("Was geschrieben wurde, bleibt bis zu 14 Tage in den Protokollen der App.", model.IntroText);
    }

    [Fact]
    public void Every_language_has_both_sentences_and_the_days_one_has_the_number()
    {
        foreach (var tag in new[] { "en", "de", "fr", "es", "zh-Hans" })
        {
            var s = UiStrings.For(new CultureInfo(tag));
            Assert.Contains("14", s.ContentLoggingIntroDays(14));
            Assert.DoesNotContain("{", s.ContentLoggingIntroDays(14));
            Assert.DoesNotContain("{", s.ContentLoggingIntro);
            Assert.NotEqual(s.ContentLoggingIntro, s.ContentLoggingIntroDays(14));
            Assert.False(string.IsNullOrWhiteSpace(s.ContentLoggingIntroOneDay), tag);
            Assert.NotEqual(s.ContentLoggingIntroDays(1), s.ContentLoggingIntroOneDay);
            Assert.DoesNotContain("{", s.ContentLoggingIntroOneDay);
            Assert.Equal(s.ContentLoggingIntro.Substring(0, 30), s.ContentLoggingIntroOneDay.Substring(0, 30));
            // The common start is the same words in both.
            Assert.Equal(s.ContentLoggingIntro.Substring(0, 30), s.ContentLoggingIntroDays(14).Substring(0, 30));
        }
    }
}

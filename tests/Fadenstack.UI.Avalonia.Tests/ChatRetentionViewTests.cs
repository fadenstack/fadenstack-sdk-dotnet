using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Settings;
using Fadenstack.UI.Tests.Settings;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The Avalonia view of the setting for old chats: every state in every language without a binding error, with the names screen readers need.</summary>
public class ChatRetentionViewTests
{
    public static TheoryData<string, string> States()
    {
        var data = new TheoryData<string, string>();
        foreach (var language in new[] { "en", "de", "fr", "es", "zh-Hans" })
        {
            foreach (var state in FakeChatRetention.Names)
            {
                data.Add(state, language);
            }
        }
        return data;
    }

    private sealed class Host : IDisposable
    {
        private readonly ILogSink? _sink = Logger.Sink;
        private readonly Capture _capture = new();

        public Host(string state, string language)
        {
            Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
            Source = FakeChatRetention.Of(state);
            Model = new ChatRetentionSettingsViewModel(Source, new AvaloniaUiDispatcher(), () => Strings);
            View = new ChatRetentionSettingsView { DataContext = Model };
            Window = new Window { Width = 560, Height = 700, Content = View };
            Logger.Sink = _capture;
        }

        public UiStrings Strings { get; }

        public FakeChatRetention Source { get; }

        public ChatRetentionSettingsViewModel Model { get; }

        public ChatRetentionSettingsView View { get; }

        public Window Window { get; }

        public IReadOnlyList<string> BindingErrors => _capture.Errors;

        public static async Task<Host> OpenAsync(string state, string language)
        {
            var host = new Host(state, language);
            host.Window.Show();
            await host.PumpAsync();
            return host;
        }

        public async Task PumpAsync(int rounds = 6)
        {
            for (var i = 0; i < rounds; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(5);
            }
        }

        public IEnumerable<Control> Visible() => View.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible);

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

        public T Find<T>(string name) where T : Control => View.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        public void Dispose()
        {
            Logger.Sink = _sink;
            Window.Close();
        }

        private sealed class Capture : ILogSink
        {
            public List<string> Errors { get; } = new();

            public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

            public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            {
                if (IsEnabled(level, area))
                {
                    Errors.Add(messageTemplate);
                }
            }

            public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            {
                if (IsEnabled(level, area))
                {
                    Errors.Add(messageTemplate + " " + string.Join(", ", propertyValues));
                }
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(States))]
    public async Task Every_state_draws_in_every_language_without_a_binding_error(string state, string language)
    {
        using var host = await Host.OpenAsync(state, language);
        var s = host.Strings;
        var texts = host.Texts();

        Assert.Empty(host.BindingErrors);
        Assert.Contains(s.ChatRetentionTitle, texts);
        var governed = state.StartsWith("organisation", StringComparison.Ordinal);
        var policy = state == "policy";
        Assert.Equal(!governed, host.Visible().OfType<CheckBox>().Any());
        Assert.Equal(!governed, texts.Contains(s.ChatRetentionMaxAge) && texts.Contains(s.ChatRetentionMaxSessions));
        Assert.Equal(governed || policy, host.Find<Border>("NoteBanner").IsEffectivelyVisible);
        if (governed || policy)
        {
            Assert.Contains(host.Model.Note, texts);
            Assert.NotEqual("", host.Model.Note);
        }
        Assert.Equal(!governed && !policy, host.Find<Button>("SaveButton").IsEffectivelyVisible);
        Assert.Equal(state == "person", host.Find<TextBox>("DaysBox").IsEffectivelyEnabled);
        Assert.Equal(state == "person", host.Find<TextBox>("ChatsBox").IsEffectivelyEnabled);
        Assert.Equal(state is "person" or "person-off", host.Find<CheckBox>("AutoDeleteBox").IsEffectivelyEnabled);
        Assert.Equal(state != "person-off", host.Find<CheckBox>("AutoDeleteBox").IsChecked);

        foreach (var control in host.Visible().Where(c => c is Button or TextBox or CheckBox))
        {
            var name = AutomationProperties.GetName(control);
            Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} {control.Name} has no automation name");
            if (control is Button { Content: string shown })
            {
                Assert.True(name.Contains(shown, StringComparison.OrdinalIgnoreCase), $"{language}: '{name}' does not contain '{shown}'");
            }
        }
    }

    [AvaloniaFact]
    public async Task Typing_a_bad_number_shows_the_error_and_stops_Save_and_Save_runs_the_view_models_command()
    {
        using var host = await Host.OpenAsync("person", "en");
        var days = host.Find<TextBox>("DaysBox");
        var save = host.Find<Button>("SaveButton");
        Assert.Same(host.Model.SaveCommand, save.Command);
        Assert.False(host.Model.SaveCommand.CanExecute(null));

        days.Text = "0";
        await host.PumpAsync();
        Assert.Equal("0", host.Model.MaxAgeDaysText);
        Assert.True(host.Find<TextBlock>("DaysErrorText").IsEffectivelyVisible);
        Assert.Contains("3650", host.Find<TextBlock>("DaysErrorText").Text);
        Assert.False(host.Model.SaveCommand.CanExecute(null));

        days.Text = "30";
        await host.PumpAsync();
        Assert.False(host.Find<TextBlock>("DaysErrorText").IsEffectivelyVisible);
        Assert.True(host.Model.SaveCommand.CanExecute(null));
        await host.Model.SaveCommand.ExecuteAsync(null);
        await host.PumpAsync();

        Assert.Equal(30, Assert.Single(host.Source.Saved).MaxAgeDays);
        Assert.Equal("Saved.", host.Find<TextBlock>("StatusLine").Text);
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaFact]
    public async Task The_switch_turns_the_fields_off_and_Cancel_puts_things_back()
    {
        using var host = await Host.OpenAsync("person", "en");
        var box = host.Find<CheckBox>("AutoDeleteBox");
        box.IsChecked = false;
        await host.PumpAsync();
        Assert.False(host.Model.AutoDelete);
        Assert.False(host.Find<TextBox>("DaysBox").IsEffectivelyEnabled);

        host.Model.CancelCommand.Execute(null);
        await host.PumpAsync();
        Assert.True(host.Model.AutoDelete);
        Assert.True(box.IsChecked);
        Assert.True(host.Find<TextBox>("DaysBox").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task The_governed_note_names_the_limits()
    {
        using var host = await Host.OpenAsync("organisation", "en");
        Assert.Contains("Your organisation keeps at most 30 chats for 14 days on this device.", host.Texts());
    }
}

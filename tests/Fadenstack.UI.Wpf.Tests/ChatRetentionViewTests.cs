using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Fadenstack.UI.Settings;
using Fadenstack.UI.Tests.Settings;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>The WPF view of the setting for old chats: every state in every language without a binding error, with the names screen readers need.</summary>
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

    /// <summary>The view in a window far off screen; binding errors WPF reports while it runs are kept.</summary>
    private sealed class Host : IDisposable
    {
        private readonly Trace _trace = new();
        private readonly SynchronizationContext? _previous;

        public Host(string state, string language)
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
            Source = FakeChatRetention.Of(state);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Model = new ChatRetentionSettingsViewModel(Source, new WpfDispatcher(), () => Strings);
            View = new ChatRetentionSettingsView { DataContext = Model };
            Window = new Window
            {
                Width = 560, Height = 700, Content = View, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            Window.Show();
            Pump();
        }

        public UiStrings Strings { get; }

        public FakeChatRetention Source { get; }

        public ChatRetentionSettingsViewModel Model { get; }

        public ChatRetentionSettingsView View { get; }

        public Window Window { get; }

        public IReadOnlyList<string> BindingErrors => _trace.Errors;

        public void Pump()
        {
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
        }

        public T Find<T>(string name) where T : FrameworkElement => Descendants(View).OfType<T>().First(e => e.Name == name);

        public IEnumerable<FrameworkElement> Visible() => Descendants(View).OfType<FrameworkElement>().Where(e => e.IsVisible);

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var inner in Descendants(child))
                {
                    yield return inner;
                }
            }
        }

        public void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;
            Window.Close();
            SynchronizationContext.SetSynchronizationContext(_previous);
        }

        private sealed class Trace : TraceListener
        {
            private string _line = "";

            public List<string> Errors { get; } = new();

            public override void Write(string? message) => _line += message;

            public override void WriteLine(string? message)
            {
                Errors.Add(_line + message);
                _line = "";
            }
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Every_state_draws_in_every_language_without_a_binding_error(string state, string language) => Sta.Run(() =>
    {
        using var host = new Host(state, language);
        var s = host.Strings;
        var texts = host.Texts();

        Assert.Empty(host.BindingErrors);
        Assert.Contains(s.ChatRetentionTitle, texts);
        var governed = state.StartsWith("organisation", StringComparison.Ordinal);
        var policy = state == "policy";
        Assert.Equal(!governed, texts.Contains(s.ChatRetentionAutoDelete));
        Assert.Equal(!governed, texts.Contains(s.ChatRetentionMaxAge) && texts.Contains(s.ChatRetentionMaxSessions));
        Assert.Equal(governed || policy, host.Find<Border>("NoteBanner").IsVisible);
        if (governed || policy)
        {
            Assert.Contains(host.Model.Note, texts);
            Assert.NotEqual("", host.Model.Note);
        }
        // The buttons are there for the person only; the fields can be typed in only when it is theirs and on.
        Assert.Equal(!governed && !policy, host.Find<Button>("SaveButton").IsVisible);
        Assert.Equal(state == "person", host.Find<TextBox>("DaysBox").IsEnabled);
        Assert.Equal(state == "person", host.Find<TextBox>("ChatsBox").IsEnabled);
        Assert.Equal(state is "person" or "person-off", host.Find<CheckBox>("AutoDeleteBox").IsEnabled);
        Assert.Equal(state != "person-off", host.Find<CheckBox>("AutoDeleteBox").IsChecked);

        // Every control on screen has a name for screen readers, and a button's name says what the button says (WCAG 2.5.3).
        foreach (var control in host.Visible().Where(e => e is ButtonBase or TextBoxBase))
        {
            var name = System.Windows.Automation.AutomationProperties.GetName(control);
            Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} {control.Name} has no automation name");
            if (control is Button { Content: string shown })
            {
                Assert.True(name.IndexOf(shown, StringComparison.OrdinalIgnoreCase) >= 0, $"{language}: '{name}' does not contain '{shown}'");
            }
        }
    });

    [Fact]
    public void Typing_a_bad_number_shows_the_error_and_stops_Save_and_Save_runs_the_view_models_command() => Sta.Run(() =>
    {
        using var host = new Host("person", "en");
        var days = host.Find<TextBox>("DaysBox");
        var save = host.Find<Button>("SaveButton");
        Assert.Same(host.Model.SaveCommand, save.Command);
        Assert.False(save.IsEnabled);

        days.Text = "0";
        host.Pump();
        Assert.Equal("0", host.Model.MaxAgeDaysText);
        Assert.True(host.Find<TextBlock>("DaysErrorText").IsVisible);
        Assert.Contains("3650", host.Find<TextBlock>("DaysErrorText").Text);
        Assert.False(save.IsEnabled);

        days.Text = "30";
        host.Pump();
        Assert.False(host.Find<TextBlock>("DaysErrorText").IsVisible);
        Assert.True(save.IsEnabled);
        save.Command!.Execute(null);
        host.Pump();

        Assert.Equal(30, Assert.Single(host.Source.Saved).MaxAgeDays);
        Assert.Equal("Saved.", host.Find<TextBlock>("StatusLine").Text);
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void The_switch_turns_the_fields_off_and_Cancel_puts_things_back() => Sta.Run(() =>
    {
        using var host = new Host("person", "en");
        var box = host.Find<CheckBox>("AutoDeleteBox");
        box.IsChecked = false;
        host.Pump();
        Assert.False(host.Model.AutoDelete);
        Assert.False(host.Find<TextBox>("DaysBox").IsEnabled);

        host.Find<Button>("CancelButton").Command!.Execute(null);
        host.Pump();
        Assert.True(host.Model.AutoDelete);
        Assert.True(box.IsChecked);
        Assert.True(host.Find<TextBox>("DaysBox").IsEnabled);
    });

    [Fact]
    public void The_governed_note_names_the_limits() => Sta.Run(() =>
    {
        using var host = new Host("organisation", "en");
        Assert.Contains("Your organisation keeps at most 30 chats for 14 days on this device.", host.Texts());
    });
}

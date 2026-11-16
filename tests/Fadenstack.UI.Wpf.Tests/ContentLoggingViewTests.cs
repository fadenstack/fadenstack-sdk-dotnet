using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.Tests.Settings;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>The WPF views of content logging (8.1): the setting in every state and language without a binding error, and the band in the chat.</summary>
public class ContentLoggingViewTests
{
    public static TheoryData<string, string> States()
    {
        var data = new TheoryData<string, string>();
        foreach (var language in new[] { "en", "de", "fr", "es", "zh-Hans" })
        {
            foreach (var state in FakeContentLogging.Names)
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
            Source = FakeContentLogging.Of(state);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Model = new ContentLoggingSettingsViewModel(Source, new WpfDispatcher(), () => Strings);
            View = new ContentLoggingSettingsView { DataContext = Model };
            Window = new Window
            {
                Width = 560, Height = 700, Content = View, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            Window.Show();
            Pump();
        }

        public UiStrings Strings { get; }

        public FakeContentLogging Source { get; }

        public ContentLoggingSettingsViewModel Model { get; }

        public ContentLoggingSettingsView View { get; }

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
        var available = state != "unavailable";

        Assert.Empty(host.BindingErrors);
        Assert.Equal(available, host.Find<StackPanel>("Column").IsVisible);
        if (available)
        {
            Assert.Contains(s.ContentLoggingTitle, texts);
            Assert.Contains(s.ContentLoggingIntro, texts);
            Assert.Contains(s.ContentLoggingDuration, texts);
            Assert.Equal(state == "off", host.Find<ComboBox>("DurationBox").IsEnabled);
            Assert.Equal(new[] { s.ContentLoggingOneHour, s.ContentLoggingFourHours, s.ContentLoggingDay }, host.Model.Choices.Select(c => c.Text));
            Assert.Equal(state == "off", host.Find<Button>("StartButton").IsEnabled);
            Assert.Equal(state == "on", host.Find<Button>("StopButton").IsEnabled);
            Assert.Equal(state == "on", host.Model.OnUntilText.Length > 0);
            if (state == "on")
            {
                Assert.Contains(host.Model.OnUntilText, texts);
            }
        }

        // Every control on screen has a name for screen readers, and a button's name says what the button says (WCAG 2.5.3).
        foreach (var control in host.Visible().Where(e => e is Button or ComboBox))
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
    public void Start_and_Stop_run_the_view_models_commands_with_the_chosen_time() => Sta.Run(() =>
    {
        using var host = new Host("off", "en");
        var start = host.Find<Button>("StartButton");
        Assert.Same(host.Model.StartCommand, start.Command);

        host.Find<ComboBox>("DurationBox").SelectedIndex = 1;
        host.Pump();
        start.Command!.Execute(null);
        host.Pump();

        Assert.Equal(TimeSpan.FromHours(4), Assert.Single(host.Source.Started));
        Assert.True(host.Find<Button>("StopButton").IsEnabled);
        Assert.False(start.IsEnabled);
        Assert.StartsWith("On until ", host.Find<TextBlock>("StatusLine").Text);

        host.Find<Button>("StopButton").Command!.Execute(null);
        host.Pump();
        Assert.Equal(1, host.Source.Stops);
        Assert.Equal("", host.Find<TextBlock>("StatusLine").Text);
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void The_chat_shows_the_band_only_while_content_logging_is_on_and_its_Stop_ends_it() => Sta.Run(() =>
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var broker = new ApprovalBroker();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new NoNetwork()))
            .UseAgent("word").UseHookContent()
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, new WpfDispatcher(),
            new AgentChatOptions { AutomaticLanguage = new CultureInfo("en"), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero });
        var view = new ChatView { DataContext = chat, Width = 480, Height = 560 };
        view.Measure(new Size(480, 560));
        view.Arrange(new Rect(0, 0, 480, 560));
        view.UpdateLayout();
        var band = (Border)view.FindName("ContentLoggingBand");
        var stop = (Button)view.FindName("ContentLoggingStop");
        void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
        }

        Assert.Equal(Visibility.Collapsed, band.Visibility); // offered is not on

        agent.StartContentLogging(TimeSpan.FromHours(1));
        Pump();
        Assert.Equal(Visibility.Visible, band.Visibility);
        Assert.StartsWith("Content logging is on until ", ((TextBlock)((DockPanel)band.Child).Children.OfType<TextBlock>().Single()).Text);
        Assert.EndsWith("Prompts and answers are written to the app's logs.", ((TextBlock)((DockPanel)band.Child).Children.OfType<TextBlock>().Single()).Text);
        Assert.Same(chat.ContentLogging.StopCommand, stop.Command);
        Assert.Equal("Stop", stop.Content);
        Assert.Equal("Stop content logging", System.Windows.Automation.AutomationProperties.GetName(stop));

        stop.Command!.Execute(null);
        Pump();
        Assert.False(agent.ContentLogging.IsOn);
        Assert.Equal(Visibility.Collapsed, band.Visibility);
        chat.Dispose();
    });

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in a view test");
    }
}

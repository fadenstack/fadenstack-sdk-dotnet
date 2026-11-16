using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.Tests.Settings;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The Avalonia views of content logging (8.1): the setting in every state and language without a binding error, and the band in the chat.</summary>
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

    private sealed class Host : IDisposable
    {
        private readonly ILogSink? _sink = Logger.Sink;
        private readonly Capture _capture = new();

        public Host(string state, string language)
        {
            Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
            Source = FakeContentLogging.Of(state);
            Model = new ContentLoggingSettingsViewModel(Source, new AvaloniaUiDispatcher(), () => Strings);
            View = new ContentLoggingSettingsView { DataContext = Model };
            Window = new Window { Width = 560, Height = 700, Content = View };
            Logger.Sink = _capture;
        }

        public UiStrings Strings { get; }

        public FakeContentLogging Source { get; }

        public ContentLoggingSettingsViewModel Model { get; }

        public ContentLoggingSettingsView View { get; }

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
        var available = state != "unavailable";

        Assert.Empty(host.BindingErrors);
        Assert.Equal(available, host.Find<StackPanel>("Column").IsEffectivelyVisible);
        if (available)
        {
            Assert.Contains(s.ContentLoggingTitle, texts);
            Assert.Contains(s.ContentLoggingIntro, texts);
            Assert.Contains(s.ContentLoggingDuration, texts);
            Assert.Equal(state == "off", host.Find<ComboBox>("DurationBox").IsEffectivelyEnabled);
            Assert.Equal(new[] { s.ContentLoggingOneHour, s.ContentLoggingFourHours, s.ContentLoggingDay }, host.Model.Choices.Select(c => c.Text));
            Assert.Equal(state == "off", host.Find<Button>("StartButton").IsEffectivelyEnabled);
            Assert.Equal(state == "on", host.Find<Button>("StopButton").IsEffectivelyEnabled);
            if (state == "on")
            {
                Assert.NotEqual("", host.Model.OnUntilText);
                Assert.Contains(host.Model.OnUntilText, texts);
            }
        }

        foreach (var control in host.Visible().Where(c => c is Button or ComboBox))
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
    public async Task Start_and_Stop_run_the_view_models_commands_with_the_chosen_time()
    {
        using var host = await Host.OpenAsync("off", "en");
        var start = host.Find<Button>("StartButton");
        Assert.Same(host.Model.StartCommand, start.Command);

        host.Find<ComboBox>("DurationBox").SelectedIndex = 1;
        await host.PumpAsync();
        host.Model.StartCommand.Execute(null);
        await host.PumpAsync();

        Assert.Equal(TimeSpan.FromHours(4), Assert.Single(host.Source.Started));
        Assert.True(host.Find<Button>("StopButton").IsEffectivelyEnabled);
        Assert.False(start.IsEffectivelyEnabled);
        Assert.StartsWith("On until ", host.Find<TextBlock>("StatusLine").Text);

        host.Model.StopCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(1, host.Source.Stops);
        Assert.Equal("", host.Find<TextBlock>("StatusLine").Text);
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaFact]
    public async Task The_chat_shows_the_band_only_while_content_logging_is_on_and_its_Stop_ends_it()
    {
        var broker = new ApprovalBroker();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new HarnessGateway()))
            .UseAgent("demo-assistant").UseHookContent()
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).UseApprovals(broker).Build();
        using var chat = new AgentChatViewModel(agent, broker, new AvaloniaUiDispatcher(),
            new AgentChatOptions { AutomaticLanguage = CultureInfo.GetCultureInfo("en"), Preferences = new MemoryPreferenceStore() });
        var view = new ChatView { DataContext = chat };
        var window = new Window { Width = 480, Height = 560, Content = view };
        window.Show();
        async Task Pump()
        {
            for (var i = 0; i < 6; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(5);
            }
        }
        await Pump();
        var band = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ContentLoggingBand");
        Assert.False(band.IsEffectivelyVisible); // offered is not on

        agent.StartContentLogging(TimeSpan.FromHours(1));
        await Pump();
        Assert.True(band.IsEffectivelyVisible);
        var text = band.GetVisualDescendants().OfType<TextBlock>().First(b => b.Text!.StartsWith("Content", StringComparison.Ordinal)).Text!;
        Assert.StartsWith("Content logging is on until ", text);
        Assert.EndsWith("Prompts and answers are written to the app's logs.", text);
        var stop = band.GetVisualDescendants().OfType<Button>().Single();
        Assert.Same(chat.ContentLogging.StopCommand, stop.Command);
        Assert.Equal("Stop", stop.Content);
        Assert.Equal("Stop content logging", AutomationProperties.GetName(stop));

        chat.ContentLogging.StopCommand.Execute(null);
        await Pump();
        Assert.False(agent.ContentLogging.IsOn);
        Assert.False(band.IsEffectivelyVisible);
        window.Close();
    }
}

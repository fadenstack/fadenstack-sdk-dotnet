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

/// <summary>The Avalonia view of "Your data on this device": every state in every language without a binding error, with the names screen readers need.</summary>
public class MyDataViewTests
{
    public static TheoryData<string, string> States()
    {
        var data = new TheoryData<string, string>();
        foreach (var language in new[] { "en", "de", "fr", "es", "zh-Hans" })
        {
            foreach (var state in FakeMyData.Names)
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
            Source = FakeMyData.Of(state);
            Model = new MyDataSettingsViewModel(Source, Source, new AvaloniaUiDispatcher(), () => Strings);
            View = new MyDataSettingsView { DataContext = Model };
            Window = new Window { Width = 560, Height = 700, Content = View };
            Logger.Sink = _capture;
        }

        public UiStrings Strings { get; }

        public FakeMyData Source { get; }

        public MyDataSettingsViewModel Model { get; }

        public MyDataSettingsView View { get; }

        public Window Window { get; }

        public IReadOnlyList<string> BindingErrors => _capture.Errors;

        public static async Task<Host> OpenAsync(string state, string language)
        {
            var host = new Host(state, language);
            host.Window.Show();
            await host.PumpAsync();
            foreach (var step in FakeMyData.Steps(state))
            {
                FakeMyData.Press(host.Model, step);
                await host.PumpAsync();
            }
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

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text ?? "").Concat(Visible().OfType<Button>().Select(b => b.Content as string ?? ""))
            .Where(t => t.Length > 0).ToList();

        /// <summary>The control that has the keyboard focus.</summary>
        public object? Focused => TopLevel.GetTopLevel(View)?.FocusManager?.GetFocusedElement();

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
        Assert.Contains(s.MyDataTitle, texts);
        Assert.Contains(s.MyDataIntro, texts);
        // No text is missing: nothing on screen is empty or a text's key.
        Assert.All(texts, t => Assert.DoesNotContain("MyData", t));
        Assert.All(host.Visible().OfType<Button>(), b => Assert.False(string.IsNullOrWhiteSpace(b.Content as string), $"{b.Name} has no text"));

        var idle = state is "idle" or "exported";
        var confirming = state.StartsWith("confirm", StringComparison.Ordinal);
        var done = state.StartsWith("done", StringComparison.Ordinal);
        Assert.Equal(idle, host.Find<StackPanel>("Actions").IsEffectivelyVisible);
        Assert.Equal(confirming, host.Find<Border>("ConfirmBanner").IsEffectivelyVisible);
        Assert.Equal(done, host.Find<Border>("DoneBanner").IsEffectivelyVisible);
        Assert.Equal(state == "error", host.Find<Border>("ErrorBanner").IsEffectivelyVisible);
        Assert.Equal(done || state == "error", host.Find<Button>("DismissButton").IsEffectivelyVisible);
        Assert.Equal(state is "busy" or "exported", host.Find<TextBlock>("StatusLine").IsEffectivelyVisible);
        if (idle)
        {
            Assert.Contains(s.MyDataExport, texts);
            Assert.Contains(s.MyDataExportAll, texts);
            Assert.Contains(s.MyDataRemove, texts);
            Assert.Contains(s.MyDataRemoveAll, texts);
            // The record of tool calls: its line (12 entries, intact) and the button that saves it as a web page.
            Assert.Contains(s.MyDataAuditExport, texts);
            Assert.NotEqual("", host.Model.AuditLine);
            Assert.Equal(host.Model.AuditLine, host.Find<TextBlock>("AuditLine").Text);
        }
        if (state == "exported")
        {
            Assert.Equal(s.MyDataExported, host.Find<TextBlock>("StatusLine").Text);
        }
        if (state == "busy")
        {
            Assert.Equal(s.MyDataBusy, host.Find<TextBlock>("StatusLine").Text);
            Assert.Empty(host.Visible().OfType<Button>());
        }
        if (confirming)
        {
            Assert.Equal(host.Model.ConfirmText, host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.NotEqual("", host.Model.ConfirmText);
            Assert.Equal(s.MyDataConfirmUndo, host.Find<TextBlock>("UndoLabel").Text);
            Assert.Equal(host.Model.ConfirmButtonText, host.Find<Button>("ConfirmButton").Content);
            Assert.Contains(s.MyDataCancel, texts);
        }
        if (state == "confirm-shared")
        {
            Assert.Contains(s.AppQuoted("Desktop agent") + s.MyDataListSeparator + s.AppQuoted("Excel add-in"), host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.Equal(s.MyDataConfirmThisAppShared(s.AppQuoted("Desktop agent") + s.MyDataListSeparator + s.AppQuoted("Excel add-in")), host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.False(host.Find<ItemsControl>("AppList").IsEffectivelyVisible);
        }
        if (state == "confirm-everything")
        {
            Assert.Equal(s.MyDataConfirmThisAppEverything, host.Find<TextBlock>("ConfirmLabel").Text);
        }
        if (state == "confirm-all")
        {
            Assert.Equal(s.MyDataConfirmAll, host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.True(host.Find<ItemsControl>("AppList").IsEffectivelyVisible);
            Assert.Contains(s.MyDataThisApp(s.AppQuoted("Word add-in")), texts);
            Assert.Contains(s.AppQuoted("Desktop agent"), texts);
            Assert.Contains(s.AppQuoted("old-assistant"), texts);
        }
        if (done)
        {
            Assert.Equal(host.Model.StatusText, host.Find<TextBlock>("DoneText").Text);
            Assert.NotEqual("", host.Model.StatusText);
        }
        if (state == "done-kept")
        {
            Assert.Equal(s.MyDataRemovedKept(s.AppQuoted("Desktop agent") + s.MyDataListSeparator + s.AppQuoted("Excel add-in")), host.Find<TextBlock>("DoneText").Text);
        }
        if (state is "done-everything" or "done-all")
        {
            Assert.Equal(s.MyDataRemovedEverything, host.Find<TextBlock>("DoneText").Text);
        }
        if (state == "error")
        {
            Assert.Equal(s.MyDataErrorRead, host.Find<TextBlock>("ErrorLabel").Text);
            Assert.DoesNotContain("secret", string.Join("|", texts));
        }

        // Every control on screen has a name for screen readers, and a button's name says what the button says (WCAG 2.5.3).
        foreach (var control in host.Visible().Where(e => e is Button))
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
    public async Task The_buttons_run_the_view_models_commands()
    {
        using var host = await Host.OpenAsync("idle", "en");
        Assert.Same(host.Model.ExportCommand, host.Find<Button>("ExportButton").Command);
        Assert.Same(host.Model.ExportAllCommand, host.Find<Button>("ExportAllButton").Command);
        Assert.Same(host.Model.RemoveCommand, host.Find<Button>("RemoveButton").Command);
        Assert.Same(host.Model.RemoveAllCommand, host.Find<Button>("RemoveAllButton").Command);
        Assert.Same(host.Model.ExportAuditCommand, host.Find<Button>("ExportAuditButton").Command);
        Assert.Same(host.Model.ConfirmCommand, host.Find<Button>("ConfirmButton").Command);
        Assert.Same(host.Model.CancelCommand, host.Find<Button>("CancelButton").Command);
        Assert.Same(host.Model.CancelCommand, host.Find<Button>("DismissButton").Command);
        Assert.All(new[] { "ExportButton", "ExportAllButton", "ExportAuditButton", "RemoveButton", "RemoveAllButton" }, n => Assert.True(host.Find<Button>(n).IsEffectivelyEnabled, n));
    }

    [AvaloniaFact]
    public async Task Remove_asks_first_moves_the_focus_to_Cancel_and_Cancel_puts_the_buttons_back()
    {
        using var host = await Host.OpenAsync("idle", "en");
        host.Find<Button>("RemoveButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.True(host.Find<Border>("ConfirmBanner").IsEffectivelyVisible);
        Assert.False(host.Find<StackPanel>("Actions").IsEffectivelyVisible);
        Assert.Empty(host.Source.Deleted);
        Assert.Same(host.Find<Button>("CancelButton"), host.Focused);

        host.Find<Button>("CancelButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.True(host.Find<StackPanel>("Actions").IsEffectivelyVisible);
        Assert.False(host.Find<Border>("ConfirmBanner").IsEffectivelyVisible);
        Assert.Empty(host.Source.Deleted);
    }

    [AvaloniaFact]
    public async Task Confirm_removes_and_the_result_says_what_stayed()
    {
        using var host = await Host.OpenAsync("confirm-shared", "en");
        host.Find<Button>("ConfirmButton").Command!.Execute(null);
        await host.PumpAsync();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.ThisApp }, host.Source.Deleted);
        Assert.True(host.Find<Border>("DoneBanner").IsEffectivelyVisible);
        Assert.Equal("Removed. Shared data kept for: \"Desktop agent\", \"Excel add-in\".", host.Find<TextBlock>("DoneText").Text);
        Assert.Same(host.Find<Button>("DismissButton"), host.Focused);
        Assert.Empty(host.BindingErrors);

        host.Find<Button>("DismissButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.True(host.Find<StackPanel>("Actions").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Remove_all_removes_everything()
    {
        using var host = await Host.OpenAsync("idle", "en");
        host.Find<Button>("RemoveAllButton").Command!.Execute(null);
        await host.PumpAsync();
        host.Find<Button>("ConfirmButton").Command!.Execute(null);
        await host.PumpAsync();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.AllApps }, host.Source.Deleted);
        Assert.Equal("Removed everything.", host.Find<TextBlock>("DoneText").Text);
    }

    [AvaloniaFact]
    public async Task Export_asks_the_save_target_and_says_when_it_is_saved()
    {
        using var host = await Host.OpenAsync("idle", "en");
        host.Find<Button>("ExportAllButton").Command!.Execute(null);
        await host.PumpAsync();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.AllApps }, host.Source.Exported);
        Assert.Equal("Your data was saved to the file you chose.", host.Find<TextBlock>("StatusLine").Text);
    }
}

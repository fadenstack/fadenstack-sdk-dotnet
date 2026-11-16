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

/// <summary>The WPF view of "Your data on this device": every state in every language without a binding error, with the names screen readers need.</summary>
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
            Source = FakeMyData.Of(state);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Model = new MyDataSettingsViewModel(Source, Source, new WpfDispatcher(), () => Strings);
            View = new MyDataSettingsView { DataContext = Model };
            Window = new Window
            {
                Width = 560, Height = 700, Content = View, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            Window.Show();
            Pump();
            foreach (var step in FakeMyData.Steps(state))
            {
                FakeMyData.Press(Model, step);
                Pump();
            }
        }

        public UiStrings Strings { get; }

        public FakeMyData Source { get; }

        public MyDataSettingsViewModel Model { get; }

        public MyDataSettingsView View { get; }

        public Window Window { get; }

        public IReadOnlyList<string> BindingErrors => _trace.Errors;

        public void Pump()
        {
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
        }

        public T Find<T>(string name) where T : FrameworkElement => Descendants(View).OfType<T>().First(e => e.Name == name);

        /// <summary>The control that has the keyboard focus inside the view's window.</summary>
        public IInputElement? Focused => System.Windows.Input.FocusManager.GetFocusedElement(Window);

        public IEnumerable<FrameworkElement> Visible() => Descendants(View).OfType<FrameworkElement>().Where(e => e.IsVisible);

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Concat(Visible().OfType<Button>().Select(b => b.Content as string ?? ""))
            .Where(t => t.Length > 0).ToList();

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
        Assert.Contains(s.MyDataTitle, texts);
        Assert.Contains(s.MyDataIntro, texts);
        // No text is missing: nothing on screen is empty or a text's key.
        Assert.All(texts, t => Assert.DoesNotContain("MyData", t));
        Assert.All(host.Visible().OfType<Button>(), b => Assert.False(string.IsNullOrWhiteSpace(b.Content as string), $"{b.Name} has no text"));

        var idle = state is "idle" or "exported";
        var confirming = state.StartsWith("confirm", StringComparison.Ordinal);
        var done = state.StartsWith("done", StringComparison.Ordinal);
        Assert.Equal(idle, host.Find<StackPanel>("Actions").IsVisible);
        Assert.Equal(confirming, host.Find<Border>("ConfirmBanner").IsVisible);
        Assert.Equal(done, host.Find<Border>("DoneBanner").IsVisible);
        Assert.Equal(state == "error", host.Find<Border>("ErrorBanner").IsVisible);
        Assert.Equal(done || state == "error", host.Find<Button>("DismissButton").IsVisible);
        Assert.Equal(state is "busy" or "exported", host.Find<TextBlock>("StatusLine").IsVisible);
        if (idle)
        {
            Assert.Contains(s.MyDataExport, texts);
            Assert.Contains(s.MyDataExportAll, texts);
            Assert.Contains(s.MyDataRemove, texts);
            Assert.Contains(s.MyDataRemoveAll, texts);
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
            Assert.Contains("Desktop agent" + s.MyDataListSeparator + "Excel add-in", host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.Equal(s.MyDataConfirmThisAppShared("Desktop agent" + s.MyDataListSeparator + "Excel add-in"), host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.False(host.Find<ItemsControl>("AppList").IsVisible);
        }
        if (state == "confirm-everything")
        {
            Assert.Equal(s.MyDataConfirmThisAppEverything, host.Find<TextBlock>("ConfirmLabel").Text);
        }
        if (state == "confirm-all")
        {
            Assert.Equal(s.MyDataConfirmAll, host.Find<TextBlock>("ConfirmLabel").Text);
            Assert.True(host.Find<ItemsControl>("AppList").IsVisible);
            Assert.Contains(s.MyDataThisApp("Word add-in"), texts);
            Assert.Contains("Desktop agent", texts);
            Assert.Contains("old-assistant", texts);
        }
        if (done)
        {
            Assert.Equal(host.Model.StatusText, host.Find<TextBlock>("DoneText").Text);
            Assert.NotEqual("", host.Model.StatusText);
        }
        if (state == "done-kept")
        {
            Assert.Equal(s.MyDataRemovedKept("Desktop agent" + s.MyDataListSeparator + "Excel add-in"), host.Find<TextBlock>("DoneText").Text);
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
        foreach (var control in host.Visible().Where(e => e is ButtonBase))
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
    public void The_buttons_run_the_view_models_commands() => Sta.Run(() =>
    {
        using var host = new Host("idle", "en");
        Assert.Same(host.Model.ExportCommand, host.Find<Button>("ExportButton").Command);
        Assert.Same(host.Model.ExportAllCommand, host.Find<Button>("ExportAllButton").Command);
        Assert.Same(host.Model.RemoveCommand, host.Find<Button>("RemoveButton").Command);
        Assert.Same(host.Model.RemoveAllCommand, host.Find<Button>("RemoveAllButton").Command);
        Assert.Same(host.Model.ConfirmCommand, host.Find<Button>("ConfirmButton").Command);
        Assert.Same(host.Model.CancelCommand, host.Find<Button>("CancelButton").Command);
        Assert.Same(host.Model.CancelCommand, host.Find<Button>("DismissButton").Command);
        Assert.All(new[] { "ExportButton", "ExportAllButton", "RemoveButton", "RemoveAllButton" }, n => Assert.True(host.Find<Button>(n).IsEnabled, n));
    });

    [Fact]
    public void Remove_asks_first_moves_the_focus_to_Cancel_and_Cancel_puts_the_buttons_back() => Sta.Run(() =>
    {
        using var host = new Host("idle", "en");
        host.Find<Button>("RemoveButton").Command!.Execute(null);
        host.Pump();
        Assert.True(host.Find<Border>("ConfirmBanner").IsVisible);
        Assert.False(host.Find<StackPanel>("Actions").IsVisible);
        Assert.Empty(host.Source.Deleted);
        Assert.Same(host.Find<Button>("CancelButton"), host.Focused);

        host.Find<Button>("CancelButton").Command!.Execute(null);
        host.Pump();
        Assert.True(host.Find<StackPanel>("Actions").IsVisible);
        Assert.False(host.Find<Border>("ConfirmBanner").IsVisible);
        Assert.Empty(host.Source.Deleted);
    });

    [Fact]
    public void Confirm_removes_and_the_result_says_what_stayed() => Sta.Run(() =>
    {
        using var host = new Host("confirm-shared", "en");
        host.Find<Button>("ConfirmButton").Command!.Execute(null);
        host.Pump();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.ThisApp }, host.Source.Deleted);
        Assert.True(host.Find<Border>("DoneBanner").IsVisible);
        Assert.Equal("Removed. Shared data kept for: Desktop agent, Excel add-in.", host.Find<TextBlock>("DoneText").Text);
        Assert.Same(host.Find<Button>("DismissButton"), host.Focused);
        Assert.Empty(host.BindingErrors);

        host.Find<Button>("DismissButton").Command!.Execute(null);
        host.Pump();
        Assert.True(host.Find<StackPanel>("Actions").IsVisible);
    });

    [Fact]
    public void Remove_all_removes_everything() => Sta.Run(() =>
    {
        using var host = new Host("idle", "en");
        host.Find<Button>("RemoveAllButton").Command!.Execute(null);
        host.Pump();
        host.Find<Button>("ConfirmButton").Command!.Execute(null);
        host.Pump();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.AllApps }, host.Source.Deleted);
        Assert.Equal("Removed everything.", host.Find<TextBlock>("DoneText").Text);
    });

    [Fact]
    public void Export_asks_the_save_target_and_says_when_it_is_saved() => Sta.Run(() =>
    {
        using var host = new Host("idle", "en");
        host.Find<Button>("ExportAllButton").Command!.Execute(null);
        host.Pump();

        Assert.Equal(new[] { Fadenstack.Agents.DataScope.AllApps }, host.Source.Exported);
        Assert.Equal("Your data was saved to the file you chose.", host.Find<TextBlock>("StatusLine").Text);
    });
}

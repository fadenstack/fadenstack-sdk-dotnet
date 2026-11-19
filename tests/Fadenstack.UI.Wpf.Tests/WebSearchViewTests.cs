using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;
using Fadenstack.UI.Wpf.Views;
using Fadenstack.WebSearch;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The web search settings (sdk/51 F06) in the WPF skin: the row on "In this app" only where the app gave the page the person's settings,
/// the sheet it opens, the view on its own, the key typed into a password box that never shows a kept key, and no binding error in any of
/// it. The view model's own behaviour is tested in Fadenstack.UI.Tests.
/// </summary>
public class WebSearchViewTests
{
    private static PasswordBox KeyBox(ToolsHost host) => host.Find<PasswordBox>("WebSearchKeyBox");

    [Fact]
    public void The_row_is_on_In_this_app_only_where_the_app_gave_a_store() => Sta.Run(() =>
    {
        using (var without = new ToolsHost("app"))
        {
            Assert.False(without.Has("WebSearchRow"));
            Assert.False(without.Has("WebSearchButton"));
        }
        using var host = new ToolsHost("app-websearch");
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.True(host.Has("WebSearchRow"));
        Assert.Contains("Web search", host.Texts());
        Assert.Equal("Off", host.Find<TextBlock>("WebSearchState").Text);
        var button = host.Find<Button>("WebSearchButton");
        Assert.Equal("Set up", button.Content);
        Assert.Equal("Set up", AutomationProperties.GetName(button));
        Assert.True(button.Focusable && button.IsTabStop);
        // The computer's tab does not have it.
        host.Page.ShowsLocal = true;
        host.Pump();
        Assert.False(host.Has("WebSearchRow"));
    });

    [Fact]
    public void The_button_opens_the_sheet_and_the_sheet_draws_with_every_control_named() => Sta.Run(() =>
    {
        using var host = new ToolsHost("app-websearch");
        host.Find<Button>("WebSearchButton").Command.Execute(null);
        host.Pump();
        Assert.IsType<WebSearchSettingsViewModel>(host.Page.Sheet);
        Assert.True(host.Has("WebSearchSheet"));
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.False(host.Has("WebSearchKeyBox")); // Off: no key box
        foreach (var control in host.Controls().Where(c => c.Name.StartsWith("WebSearch", StringComparison.Ordinal)))
        {
            Assert.True(AutomationProperties.GetName(control).Length > 0, $"{control.Name} has no automation name");
        }
        Assert.Equal(["WebSearchOffButton", "WebSearchBraveButton", "WebSearchSearxngButton"],
            host.Tree().OfType<RadioButton>().Where(r => r.Name.StartsWith("WebSearch", StringComparison.Ordinal)).Select(r => r.Name));
        Assert.True(host.Find<RadioButton>("WebSearchOffButton").IsChecked);
    });

    [Fact]
    public void Brave_shows_a_password_box_that_carries_the_key_and_Save_hands_it_to_the_key_store_and_empties_the_box() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch");
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        host.Pump();
        var box = KeyBox(host);
        Assert.True(box.IsVisible);
        box.Password = "BSA-typed-key";
        host.Pump();
        var sheet = (WebSearchSettingsViewModel)host.Page.Sheet!;
        Assert.Equal("BSA-typed-key", sheet.Key);

        host.Find<Button>("WebSearchSaveButton").Command.Execute(null);
        host.Pump();

        Assert.Null(host.Page.Sheet);
        Assert.Equal(["BSA-typed-key"], host.Scene.WebSearch!.Protector.Protected);
        Assert.Equal("", box.Password);
        Assert.Equal("", sheet.Key);
        Assert.Equal("Brave", host.Find<TextBlock>("WebSearchState").Text);
        Assert.Equal("Change", host.Find<Button>("WebSearchButton").Content);
    });

    [Fact]
    public void A_kept_key_shows_a_line_with_Replace_and_Remove_never_the_key_and_Replace_gives_an_empty_box() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch-kept");
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.False(host.Has("WebSearchKeyBox"));
        Assert.True(host.Has("WebSearchKeyKept"));
        Assert.True(host.Has("WebSearchReplaceKeyButton"));
        Assert.True(host.Has("WebSearchRemoveKeyButton"));
        Assert.DoesNotContain(host.Texts(), t => t.Contains("brave-key-123", StringComparison.Ordinal));
        Assert.Contains("The key is kept encrypted on this device and is never shown again.", host.Texts());

        host.Find<Button>("WebSearchReplaceKeyButton").Command.Execute(null);
        host.Pump();
        Assert.True(KeyBox(host).IsVisible);
        Assert.Equal("", KeyBox(host).Password);
        Assert.False(host.Has("WebSearchKeyKept"));
    });

    [Fact]
    public void Remove_key_shows_the_line_that_it_goes_on_Save() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch-kept");
        host.Find<Button>("WebSearchRemoveKeyButton").Command.Execute(null);
        host.Pump();
        Assert.True(host.Has("WebSearchKeyRemoved"));
        Assert.False(host.Has("WebSearchRemoveKeyButton"));
        Assert.True(host.Has("WebSearchReplaceKeyButton"));
    });

    [Fact]
    public void A_refused_address_says_why_and_the_sheet_stays_open() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch-refused");
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.True(host.Has("WebSearchAddressBox"));
        Assert.True(host.Has("WebSearchError"));
        Assert.Equal(host.Page.Strings.WebSearchUrlRefused, host.Find<TextBlock>("WebSearchError").Text);
        Assert.NotNull(host.Page.Sheet);
        Assert.Equal(WebSearchProvider.None, host.Scene.WebSearch!.Store.Settings.Provider);
    });

    [Fact]
    public void Esc_closes_the_sheet_and_the_typed_key_is_gone() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch");
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        host.Pump();
        KeyBox(host).Password = "typed";
        host.Pump();
        var sheet = (WebSearchSettingsViewModel)host.Page.Sheet!;

        host.Press(host.View, Key.Escape);

        Assert.Null(host.Page.Sheet);
        Assert.Equal("", sheet.Key);
    });

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_sheet_draws_in_every_language_without_a_binding_error(string language) => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch-kept", language);
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.Contains(host.Page.Strings.WebSearchTitle, host.Texts());
        Assert.Contains(host.Page.Strings.WebSearchKeyKept, host.Texts());
    });

    [Fact]
    public void Retext_says_the_open_sheet_again_in_the_new_language() => Sta.Run(() =>
    {
        using var host = new ToolsHost("websearch");
        Assert.Contains("Save", host.Texts());
        host.Page.Retext();
        host.Pump();
        Assert.Contains("Save", host.Texts());
    });

    // ---- the view on its own ------------------------------------------------------------------------------------------

    [Fact]
    public void The_view_on_its_own_renders_without_a_binding_error_and_works() => Sta.Run(() =>
    {
        using var fixture = new WebSearchFixture();
        using var host = new OwnHost(fixture);
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.Contains("Web search", host.Texts());
        Assert.True(host.Has("WebSearchSaveButton"));
        Assert.True(host.Has("WebSearchCancelButton"));
        Assert.False(host.Has("WebSearchKeyBox"));

        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        host.Pump();
        var box = host.Find<PasswordBox>("WebSearchKeyBox");
        box.Password = "own-key";
        host.Pump();
        Assert.Equal("own-key", host.Sheet.Key);
        host.Find<Button>("WebSearchSaveButton").Command.Execute(null);
        host.Pump();

        Assert.Equal(1, host.Closed);
        Assert.Equal(["own-key"], fixture.Protector.Protected);
        Assert.Equal("", box.Password);
        Assert.True(host.Has("WebSearchKeyKept")); // what it shows now is "kept"
        Assert.True(host.Has("WebSearchSaved"));
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
    });

    [Fact]
    public void The_view_on_its_own_cancels_on_Esc_and_says_its_texts_in_the_view_models_language() => Sta.Run(() =>
    {
        using var fixture = new WebSearchFixture();
        using var host = new OwnHost(fixture, "de");
        Assert.Contains("Websuche", host.Texts());
        Assert.Contains("Speichern", host.Texts());
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        host.Pump();
        host.Find<PasswordBox>("WebSearchKeyBox").Password = "dropped";
        host.Pump();

        host.Press(host.View, Key.Escape);

        Assert.Equal(1, host.Closed);
        Assert.Equal("", host.Sheet.Key);
        Assert.False(fixture.Store.HasBraveKey);
    });

    /// <summary>The view on its own in a window far off screen; binding errors WPF reports are kept.</summary>
    private sealed class OwnHost : IDisposable
    {
        private readonly BindingTrace _trace = new();
        private readonly SynchronizationContext? _previousContext;

        public OwnHost(WebSearchFixture fixture, string language = "en")
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(_trace);
            source.Switch.Level = SourceLevels.Warning;
            var strings = UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language));
            Sheet = new WebSearchSettingsViewModel(fixture.Store, new FakeUi(), () => strings);
            Sheet.Closed += (_, _) => Closed++;
            View = new WebSearchSettingsView { DataContext = Sheet };
            Window = new Window
            {
                Width = 420, Height = 900, Content = View, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            Window.Show();
            Pump();
        }

        public WebSearchSettingsViewModel Sheet { get; }

        public WebSearchSettingsView View { get; }

        public Window Window { get; }

        public int Closed { get; private set; }

        public IReadOnlyList<string> BindingErrors => _trace.Errors;

        public void Pump()
        {
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
        }

        private IEnumerable<FrameworkElement> Visible() => Descendants(View).OfType<FrameworkElement>().Where(e => e.IsVisible);

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

        public T Find<T>(string name) where T : FrameworkElement => Descendants(View).OfType<T>().First(e => e.Name == name);

        public bool Has(string name) => Visible().Any(e => e.Name == name);

        public void Press(UIElement target, Key key)
        {
            var source = PresentationSource.FromVisual(target)!;
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var inner in Descendants(child))
                {
                    yield return inner;
                }
            }
        }

        public void Dispose()
        {
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Remove(_trace);
            source.Switch.Level = SourceLevels.Off;
            Window.Close();
            Sheet.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        private sealed class BindingTrace : TraceListener
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
}

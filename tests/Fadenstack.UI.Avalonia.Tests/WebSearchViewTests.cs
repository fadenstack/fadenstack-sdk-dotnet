using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;
using Fadenstack.WebSearch;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The web search settings (sdk/51 F06) in the Avalonia skin: the row on "In this app" only where the app gave the page the person's
/// settings, the sheet it opens, the view on its own, the key typed into a masked box that never shows a kept key, and no binding error
/// in any of it. The view model's own behaviour is tested in Fadenstack.UI.Tests.
/// </summary>
public class WebSearchViewTests
{
    private static TextBox KeyBox(ToolsHost host) => host.Find<TextBox>("WebSearchKeyBox");

    [AvaloniaFact]
    public async Task The_row_is_on_In_this_app_only_where_the_app_gave_a_store()
    {
        using (var without = await ToolsHost.OpenAsync("app"))
        {
            Assert.False(without.Has("WebSearchRow"));
        }
        using var host = await ToolsHost.OpenAsync("app-websearch");
        Assert.Empty(host.BindingErrors);
        Assert.True(host.Has("WebSearchRow"));
        Assert.Contains("Web search", host.Texts());
        Assert.Equal("Off", host.Find<TextBlock>("WebSearchState").Text);
        var button = host.Find<Button>("WebSearchButton");
        Assert.Equal("Set up", button.Content);
        Assert.Equal("Set up", AutomationProperties.GetName(button));
        Assert.True(button.Focusable && button.IsTabStop);

        host.Page.ShowsLocal = true;
        await host.PumpAsync();
        Assert.False(host.Has("WebSearchRow"));
    }

    [AvaloniaFact]
    public async Task The_button_opens_the_sheet_and_the_sheet_draws_with_every_control_named()
    {
        using var host = await ToolsHost.OpenAsync("app-websearch");
        host.Find<Button>("WebSearchButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.IsType<WebSearchSettingsViewModel>(host.Page.Sheet);
        Assert.True(host.Has("WebSearchSheet"));
        Assert.Empty(host.BindingErrors);
        Assert.False(host.Has("WebSearchKeyBox"));
        foreach (var control in host.Visible().Where(c => c.Name is { } n && n.StartsWith("WebSearch", StringComparison.Ordinal) && c is Button or RadioButton or TextBox))
        {
            Assert.True((AutomationProperties.GetName(control) ?? "").Length > 0, $"{control.Name} has no automation name");
        }
        Assert.True(host.Find<RadioButton>("WebSearchOffButton").IsChecked);
    }

    [AvaloniaFact]
    public async Task Brave_shows_a_masked_box_that_carries_the_key_and_Save_hands_it_to_the_key_store_and_empties_the_box()
    {
        using var host = await ToolsHost.OpenAsync("websearch");
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        await host.PumpAsync();
        var box = KeyBox(host);
        Assert.Equal('\u25CF', box.PasswordChar);
        Assert.True(box.IsEffectivelyVisible);
        box.Text = "BSA-typed-key";
        await host.PumpAsync();
        var sheet = (WebSearchSettingsViewModel)host.Page.Sheet!;
        Assert.Equal("BSA-typed-key", sheet.Key);

        host.Find<Button>("WebSearchSaveButton").Command!.Execute(null);
        await host.PumpAsync();

        Assert.Null(host.Page.Sheet);
        Assert.Equal(["BSA-typed-key"], host.Scene.WebSearch!.Protector.Protected);
        Assert.True(string.IsNullOrEmpty(box.Text), "the box still holds the key");
        Assert.Equal("", sheet.Key);
        Assert.Equal("Brave", host.Find<TextBlock>("WebSearchState").Text);
        Assert.Equal("Change", host.Find<Button>("WebSearchButton").Content);
    }

    [AvaloniaFact]
    public async Task A_kept_key_shows_a_line_with_Replace_and_Remove_never_the_key_and_Replace_gives_an_empty_box()
    {
        using var host = await ToolsHost.OpenAsync("websearch-kept");
        Assert.Empty(host.BindingErrors);
        Assert.False(host.Has("WebSearchKeyBox"));
        Assert.True(host.Has("WebSearchKeyKept"));
        Assert.True(host.Has("WebSearchReplaceKeyButton"));
        Assert.True(host.Has("WebSearchRemoveKeyButton"));
        Assert.DoesNotContain(host.Texts(), t => t.Contains("brave-key-123", StringComparison.Ordinal));
        Assert.Contains("The key is kept encrypted on this device and is never shown again.", host.Texts());

        host.Find<Button>("WebSearchReplaceKeyButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.True(KeyBox(host).IsEffectivelyVisible);
        Assert.True(string.IsNullOrEmpty(KeyBox(host).Text));
        Assert.False(host.Has("WebSearchKeyKept"));
    }

    [AvaloniaFact]
    public async Task Remove_key_shows_the_line_that_it_goes_on_Save()
    {
        using var host = await ToolsHost.OpenAsync("websearch-kept");
        host.Find<Button>("WebSearchRemoveKeyButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.True(host.Has("WebSearchKeyRemoved"));
        Assert.False(host.Has("WebSearchRemoveKeyButton"));
        Assert.True(host.Has("WebSearchReplaceKeyButton"));
    }

    [AvaloniaFact]
    public async Task A_refused_address_says_why_and_the_sheet_stays_open()
    {
        using var host = await ToolsHost.OpenAsync("websearch-refused");
        Assert.Empty(host.BindingErrors);
        Assert.True(host.Has("WebSearchAddressBox"));
        Assert.True(host.Has("WebSearchError"));
        Assert.Equal(host.Page.Strings.WebSearchUrlRefused, host.Find<TextBlock>("WebSearchError").Text);
        Assert.NotNull(host.Page.Sheet);
        Assert.Equal(WebSearchProvider.None, host.Scene.WebSearch!.Store.Settings.Provider);
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_sheet_and_the_typed_key_is_gone()
    {
        using var host = await ToolsHost.OpenAsync("websearch");
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        await host.PumpAsync();
        KeyBox(host).Text = "typed";
        await host.PumpAsync(12);
        var sheet = (WebSearchSettingsViewModel)host.Page.Sheet!;

        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await host.PumpAsync();

        Assert.Null(host.Page.Sheet);
        Assert.Equal("", sheet.Key);
    }

    [AvaloniaTheory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public async Task The_sheet_draws_in_every_language_without_a_binding_error(string language)
    {
        using var host = await ToolsHost.OpenAsync("websearch-kept", language);
        Assert.Empty(host.BindingErrors);
        Assert.Contains(host.Page.Strings.WebSearchTitle, host.Texts());
        Assert.Contains(host.Page.Strings.WebSearchKeyKept, host.Texts());
    }

    // ---- the view on its own ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_view_on_its_own_renders_without_a_binding_error_and_works()
    {
        using var fixture = new WebSearchFixture();
        using var host = await OwnHost.OpenAsync(fixture);
        Assert.Empty(host.BindingErrors);
        Assert.Contains("Web search", host.Texts());
        Assert.True(host.Has("WebSearchSaveButton"));
        Assert.True(host.Has("WebSearchCancelButton"));
        Assert.False(host.Has("WebSearchKeyBox"));

        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        await host.PumpAsync();
        var box = host.Find<TextBox>("WebSearchKeyBox");
        Assert.Equal('\u25CF', box.PasswordChar);
        box.Text = "own-key";
        await host.PumpAsync();
        Assert.Equal("own-key", host.Sheet.Key);
        host.Find<Button>("WebSearchSaveButton").Command!.Execute(null);
        await host.PumpAsync();

        Assert.Equal(1, host.Closed);
        Assert.Equal(["own-key"], fixture.Protector.Protected);
        Assert.True(string.IsNullOrEmpty(box.Text), "the box still holds the key");
        Assert.True(host.Has("WebSearchKeyKept"));
        Assert.True(host.Has("WebSearchSaved"));
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaFact]
    public async Task The_view_on_its_own_cancels_on_Esc_and_says_its_texts_in_the_view_models_language()
    {
        using var fixture = new WebSearchFixture();
        using var host = await OwnHost.OpenAsync(fixture, "de");
        Assert.Contains("Websuche", host.Texts());
        Assert.Contains("Speichern", host.Texts());
        host.Find<RadioButton>("WebSearchBraveButton").IsChecked = true;
        await host.PumpAsync();
        host.Find<TextBox>("WebSearchKeyBox").Text = "dropped";
        await host.PumpAsync(12);

        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await host.PumpAsync();

        Assert.Equal(1, host.Closed);
        Assert.Equal("", host.Sheet.Key);
        Assert.False(fixture.Store.HasBraveKey);
    }

    /// <summary>The view on its own in a headless window; binding errors it logs are kept.</summary>
    private sealed class OwnHost : IDisposable
    {
        private readonly ILogSink? _sink;
        private readonly CaptureSink _capture = new();

        private OwnHost(WebSearchFixture fixture, string language)
        {
            var strings = UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language));
            Sheet = new WebSearchSettingsViewModel(fixture.Store, new FakeUi(), () => strings);
            Sheet.Closed += (_, _) => Closed++;
            View = new WebSearchSettingsView { DataContext = Sheet };
            Window = new Window { Width = 420, Height = 900, Content = View };
            _sink = Logger.Sink;
            Logger.Sink = _capture;
        }

        public WebSearchSettingsViewModel Sheet { get; }

        public WebSearchSettingsView View { get; }

        public Window Window { get; }

        public int Closed { get; private set; }

        public IReadOnlyList<string> BindingErrors => _capture.Errors;

        public static async Task<OwnHost> OpenAsync(WebSearchFixture fixture, string language = "en")
        {
            var host = new OwnHost(fixture, language);
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

        private IEnumerable<Control> Visible() => View.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible);

        public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

        public T Find<T>(string name) where T : Control => View.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        public bool Has(string name) => Visible().Any(c => c.Name == name);

        public void Dispose()
        {
            Logger.Sink = _sink;
            Window.Close();
            Sheet.Dispose();
        }

        private sealed class CaptureSink : ILogSink
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
}

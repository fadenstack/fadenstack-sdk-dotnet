using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The palettes of the Avalonia skin: the light and dark theme variants and high contrast (no DarkGray here, owner 15 Nov).</summary>
public enum StylePalette
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>
/// One view in one state, open in a headless window at the width of Office's task pane, on the palette's page colour
/// (<c>Fadenstack.Background</c>, as a host draws it behind the view).
/// </summary>
internal sealed class StyleHost : IDisposable
{
    /// <summary>The width the views get: Office's task pane (about 380 px), as in the WPF skin's renders.</summary>
    public const double PaneWidth = 380;

    private readonly IDisposable _inner;
    private readonly Func<IReadOnlyList<string>> _bindingErrors;

    public StyleHost(IDisposable inner, Window window, Func<IReadOnlyList<string>> bindingErrors)
    {
        _inner = inner;
        Window = window;
        _bindingErrors = bindingErrors;
        Window.Bind(TemplatedControl.BackgroundProperty, Window.GetResourceObservable("Fadenstack.Background"));
    }

    public Window Window { get; }

    /// <summary>The view the window shows (the editor of a proposal replaces the draft view in its window).</summary>
    public Control View => (Control)Window.Content!;

    public IReadOnlyList<string> BindingErrors => _bindingErrors();

    /// <summary>Everything the view shows right now: its controls that are effectively visible.</summary>
    public IEnumerable<Control> Visible() => View.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible);

    public static async Task PumpAsync(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Gives the window the pane's width and the height the view needs, measured with no height limit (a scroll viewer then measures
    /// all of its content). The height is set, not sized to the content: a headless window sized to its content stops at the screen's
    /// height (1080), which cuts the editor off.
    /// </summary>
    public async Task FitToPaneAsync()
    {
        Window.SizeToContent = SizeToContent.Manual;
        Window.Width = PaneWidth;
        await PumpAsync();
        View.Measure(new Size(PaneWidth, double.PositiveInfinity));
        Window.Height = Math.Clamp(Math.Ceiling(View.DesiredSize.Height), 40, 6000);
        await PumpAsync();
    }

    /// <summary>Draws the window at twice the size (as the WPF renders, 192 dpi) to a PNG.</summary>
    public async Task RenderAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Window.SetRenderScaling(2);
        await PumpAsync();
        Window.CaptureRenderedFrame()!.Save(path, new PngBitmapEncoderOptions());
        Window.SetRenderScaling(1);
        await PumpAsync();
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Every state of every view the styling pass covers: the wizard's steps and the states of the library, the editor and the draft.</summary>
internal static class StyleCases
{
    public static readonly StylePalette[] Palettes = { StylePalette.Light, StylePalette.Dark, StylePalette.HighContrast };

    public static readonly string[] LibraryStates = { "list", "empty", "delete", "import", "import-replaces", "import-refused", "message", "editor-new", "editor-view" };

    public static readonly string[] EditorStates = { "new", "edit", "view", "proposal", "errors", "replace", "tested", "asked", "files" };

    public static readonly string[] DraftStates = { "idle", "working", "ready", "ready-with-problems", "failed", "unreadable", "no-chat", "no-model" };

    private const string Proposal = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n\n## Goal\nReview the contract.\n";

    /// <summary>(view, state) for each; the wizard's views are named by the step.</summary>
    public static IEnumerable<(string View, string State)> All()
    {
        foreach (var step in Enum.GetValues<WizardStep>().Where(s => s != WizardStep.Finished))
        {
            yield return ("wizard", step.ToString());
        }
        foreach (var state in LibraryStates)
        {
            yield return ("library", state);
        }
        foreach (var state in EditorStates)
        {
            yield return ("editor", state);
        }
        foreach (var state in DraftStates)
        {
            yield return ("draft", state);
        }
    }

    public static async Task<StyleHost> OpenAsync(string view, string state, string language = "en")
    {
        switch (view)
        {
            case "wizard":
            {
                var host = await WizardViewTests.AtAsync(Enum.Parse<WizardStep>(state), language);
                return new StyleHost(host, host.Window, () => host.BindingErrors);
            }
            case "library":
                return Wrap(await SkillViewTests.LibraryAtAsync(state, language));
            case "editor":
            {
                if (state == "proposal")
                {
                    var host = await SkillHost.OpenDraftAsync(language, new ScriptedChat().Says(Proposal));
                    await host.Draft.DraftCommand.ExecuteAsync(null);
                    await host.PumpAsync();
                    host.Window.Content = new SkillEditorView { DataContext = host.Draft.Proposal };
                    await host.PumpAsync();
                    return Wrap(host);
                }
                return Wrap(await SkillViewTests.EditorAtAsync(state, language));
            }
            case "draft":
                return Wrap(await SkillViewTests.DraftAtAsync(state, language));
            default:
                throw new ArgumentException(view);
        }
    }

    /// <summary>Puts the application in the palette (the theme variant, and high contrast through FadenstackTheme as the gallery does).</summary>
    public static async Task UseAsync(StylePalette palette)
    {
        var app = Application.Current!;
        FadenstackTheme.Apply(app, new ThemeChoices());
        app.RequestedThemeVariant = palette == StylePalette.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (palette == StylePalette.HighContrast)
        {
            FadenstackTheme.Apply(app, new ThemeChoices { HighContrast = true });
        }
        await StyleHost.PumpAsync();
    }

    /// <summary>Back to the test application's light theme with the default choices.</summary>
    public static async Task ResetAsync()
    {
        var app = Application.Current!;
        FadenstackTheme.Apply(app, new ThemeChoices());
        app.RequestedThemeVariant = ThemeVariant.Light;
        await StyleHost.PumpAsync();
    }

    private static StyleHost Wrap(SkillHost host) => new(host, host.Window, () => host.BindingErrors);
}

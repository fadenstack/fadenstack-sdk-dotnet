using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>A fact that runs only when the environment variable is set (the render tests write files, so they are opt-in).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"Opt-in: set {variable}.";
        }
    }
}

/// <summary>One view in one state, open in a window at the width of Office's task pane, in a palette of the test's choice.</summary>
internal sealed class StyleHost : IDisposable
{
    /// <summary>The width the views get: Office's task pane (about 380 px).</summary>
    public const double PaneWidth = 380;

    private readonly IDisposable _inner;

    public StyleHost(IDisposable inner, Window window, Func<IEnumerable<FrameworkElement>> visible, Func<IEnumerable<FrameworkElement>> controls,
        Action pump, Func<IReadOnlyList<string>> bindingErrors)
    {
        _inner = inner;
        Window = window;
        VisibleCore = visible;
        ControlsCore = controls;
        PumpCore = pump;
        BindingErrorsCore = bindingErrors;
    }

    public Window Window { get; }

    private Func<IEnumerable<FrameworkElement>> VisibleCore { get; }

    private Func<IEnumerable<FrameworkElement>> ControlsCore { get; }

    private Action PumpCore { get; }

    private Func<IReadOnlyList<string>> BindingErrorsCore { get; }

    public FrameworkElement View => (FrameworkElement)Window.Content;

    public IEnumerable<FrameworkElement> Visible() => VisibleCore();

    public IEnumerable<FrameworkElement> Controls() => ControlsCore();

    public IReadOnlyList<string> BindingErrors => BindingErrorsCore();

    public void Pump() => PumpCore();

    /// <summary>Sets the palette of the view (and, through it, the views inside it).</summary>
    public void SetTheme(ChatTheme theme)
    {
        switch (View)
        {
            case WizardView w: w.Theme = theme; break;
            case SkillLibraryView l: l.Theme = theme; break;
            case SkillEditorView e: e.Theme = theme; break;
            case SkillDraftView d: d.Theme = theme; break;
        }
        PumpCore();
    }

    /// <summary>Gives the window the pane's width and the height the view's content needs.</summary>
    public void FitToPane()
    {
        Window.SizeToContent = SizeToContent.Manual;
        Window.Width = PaneWidth + 40;
        Window.Height = 6000;
        PumpCore();
        Window.Width = PaneWidth + (Window.ActualWidth - View.ActualWidth);
        PumpCore();
        var scroller = Visible().OfType<ScrollViewer>().FirstOrDefault();
        var needed = scroller is null ? View.ActualHeight : scroller.ExtentHeight + (View.ActualHeight - scroller.ViewportHeight);
        Window.Height = Math.Min(6000, Math.Max(40, needed) + (Window.ActualHeight - View.ActualHeight));
        PumpCore();
    }

    /// <summary>Draws the view to a PNG at twice the size (192 dpi).</summary>
    public void Render(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var view = View;
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Every state of every view the styling pass covers: the wizard's steps and the states of the library, the editor and the draft.</summary>
internal static class StyleCases
{
    public static readonly ChatTheme[] Palettes = { ChatTheme.Light, ChatTheme.Dark, ChatTheme.DarkGray };

    public static readonly string[] LibraryStates = { "list", "empty", "delete", "import", "import-replaces", "import-refused", "message", "editor-new", "editor-view" };

    public static readonly string[] EditorStates = { "new", "edit", "view", "proposal", "errors", "replace", "tested", "asked", "files" };

    public static readonly string[] DraftStates = { "idle", "working", "ready", "ready-with-problems", "failed", "unreadable", "no-chat", "no-model" };

    /// <summary>(view, state) for each; the wizard's views are named by the step.</summary>
    public static IEnumerable<(string View, string State)> All()
    {
        foreach (var step in Enum.GetValues(typeof(WizardStep)).Cast<WizardStep>().Where(s => s != WizardStep.Finished))
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

    public static StyleHost Open(string view, string state, string language = "en")
    {
        switch (view)
        {
            case "wizard":
            {
                var host = WizardViewTests.At((WizardStep)Enum.Parse(typeof(WizardStep), state), language);
                return new StyleHost(host, host.Window, host.Visible, host.Controls, host.Pump, () => host.BindingErrors);
            }
            case "library":
            {
                var host = SkillViewTests.LibraryAt(state, language);
                return Wrap(host);
            }
            case "editor":
            {
                if (state == "proposal")
                {
                    var host = SkillHost.OpenDraft(language, new ScriptedChat().Says("---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n\n## Goal\nReview the contract.\n"));
                    host.Await(host.Draft.DraftCommand.ExecuteAsync(null));
                    host.Window.Content = new SkillEditorView { DataContext = host.Draft.Proposal };
                    host.Pump();
                    return Wrap(host);
                }
                return Wrap(SkillViewTests.EditorAt(state, language));
            }
            case "draft":
                return Wrap(SkillViewTests.DraftAt(state, language));
            default:
                throw new ArgumentException(view);
        }
    }

    private static StyleHost Wrap(SkillHost host) => new(host, host.Window, host.Visible, host.Controls, host.Pump, () => host.BindingErrors);
}

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// One of the skill views (the library, the editor or the draft) in a window far off screen on the real view models over a real workshop in
/// a temporary folder (<see cref="SkillRig"/>), in a language of the test's choice. Binding errors WPF reports while it runs are kept in
/// <see cref="BindingErrors"/>. Make and use one on the STA thread (<see cref="Sta.Run"/>).
/// </summary>
internal sealed class SkillHost : IDisposable
{
    private readonly BindingTrace _trace = new();
    private readonly SynchronizationContext? _previousContext;

    private SkillHost(string language, Func<SkillHost, FrameworkElement> view, SkillRig rig)
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        // As in an app: the UI thread has its dispatcher's context, so an async command that was started there ends there (its CanExecuteChanged
        // would otherwise be raised on a pool thread, which WPF does not allow).
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Rig = rig;
        Language = language;
        Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(_trace);
        source.Switch.Level = SourceLevels.Warning;
        View = view(this);
        Window = new Window
        {
            Width = 560, Height = 1400, Content = View, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
    }

    public SkillRig Rig { get; }

    public string Language { get; }

    public UiStrings Strings { get; }

    public Window Window { get; }

    public FrameworkElement View { get; }

    public SkillLibraryViewModel Library { get; private set; } = null!;

    public SkillEditorViewModel Editor { get; private set; } = null!;

    public SkillDraftViewModel Draft { get; private set; } = null!;

    public IReadOnlyList<string> BindingErrors => _trace.Errors;

    private SkillLibraryViewModel NewLibrary(IChatClient? model) => new(Rig.Workshop, Rig.Files, model, new WpfDispatcher(), () => Strings);

    /// <summary>The library view over a library that has read its skills (the bundled one and, if asked, the person's own).</summary>
    public static SkillHost OpenLibrary(string language = "en", bool own = true, bool bundled = true, IChatClient? model = null)
    {
        var rig = new SkillRig();
        if (!bundled)
        {
            rig.Options.Bundled.Clear();
        }
        if (own)
        {
            rig.Real.SaveAsync(SkillRig.Doc("contract-review", "Reviews a contract clause by clause.")).GetAwaiter().GetResult();
        }
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            return new SkillLibraryView { DataContext = h.Library, Strings = h.Strings };
        }, rig);
        host.Await(host.Library.RefreshAsync());
        host.WaitUntil(() => host.Library.Rows.Count > 0 || host.Library.IsEmpty);
        host.Window.Show();
        host.Pump();
        return host;
    }

    /// <summary>The editor view over a new skill, one of the person's own ("edit"), or the bundled one to read ("view").</summary>
    public static SkillHost OpenEditor(string language = "en", string mode = "new", IChatClient? model = null)
    {
        var rig = new SkillRig();
        if (mode == "edit")
        {
            rig.Real.SaveAsync(SkillRig.Doc("contract-review")).GetAwaiter().GetResult();
        }
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            return new SkillEditorView { Strings = h.Strings };
        }, rig);
        host.Await(host.Library.RefreshAsync());
        host.WaitUntil(() => host.Library.Rows.Count > 0);
        switch (mode)
        {
            case "edit":
                host.Await(host.Library.EditCommand.ExecuteAsync(host.Library.Rows.First(r => r.Name == "contract-review")));
                break;
            case "view":
                host.Await(host.Library.EditCommand.ExecuteAsync(host.Library.Rows.First(r => r.Name == "slide-design")));
                break;
            default:
                host.Library.NewCommand.Execute(null);
                break;
        }
        host.WaitUntil(() => host.Library.Editor is not null);
        host.Editor = host.Library.Editor!;
        host.View.DataContext = host.Editor;
        host.Window.Show();
        host.Pump();
        return host;
    }

    /// <summary>The draft view over a draft from a short chat; the model answers as scripted (null: none).</summary>
    public static SkillHost OpenDraft(string language = "en", IChatClient? model = null, bool chat = true)
    {
        var rig = new SkillRig();
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            h.Draft = h.Library.CreateDraft(() => chat
                ? new[] { new SkillDraftMessage(true, "Please review this contract."), new SkillDraftMessage(false, "Clause 4 caps liability.") }
                : Array.Empty<SkillDraftMessage>());
            return new SkillDraftView { DataContext = h.Draft, Strings = h.Strings };
        }, rig);
        host.Window.Show();
        host.Pump();
        return host;
    }

    /// <summary>Lets the dispatcher run what is queued (layout, bindings, the focus the views move) down to idle.</summary>
    public void Pump()
    {
        Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
    }

    /// <summary>Waits for a task whose continuations post to the dispatcher, running the dispatcher meanwhile.</summary>
    public void Await(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        Pump();
    }

    /// <summary>Waits (briefly) for a condition that a fire-and-forget command brings about.</summary>
    public bool WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(5);
        }
        Pump();
        return condition();
    }

    public IEnumerable<DependencyObject> Tree() => Descendants((DependencyObject?)Window.Content ?? View);

    /// <summary>Everything the view shows right now: its elements that are visible (in a shown window).</summary>
    public IEnumerable<FrameworkElement> Visible() => Tree().OfType<FrameworkElement>().Where(e => e.IsVisible);

    /// <summary>The inputs and buttons the view puts on screen, not the parts inside another control's template.</summary>
    public IEnumerable<FrameworkElement> Controls() => Visible().Where(e =>
        (e is ButtonBase or TextBoxBase or PasswordBox or ComboBox || e is ScrollViewer { Focusable: true })
        && e is not RepeatButton && !(e is ToggleButton && e.TemplatedParent is ComboBox));

    /// <summary>The texts on screen: text blocks (a button's and a check box's label are text blocks too).</summary>
    public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

    public T Find<T>(string name) where T : FrameworkElement => Tree().OfType<T>().First(e => e.Name == name);

    /// <summary>The visible button that says <paramref name="text"/>.</summary>
    public Button ButtonSaying(string text) => Visible().OfType<Button>().First(b => b.Content as string == text);

    public IEnumerable<Button> ButtonsSaying(string text) => Visible().OfType<Button>().Where(b => b.Content as string == text);

    public object? Focused() => System.Windows.Input.FocusManager.GetFocusedElement(Window);

    /// <summary>A key down to the element, as the keyboard would send it.</summary>
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
        SynchronizationContext.SetSynchronizationContext(_previousContext);
        Rig.Dispose();
    }

    /// <summary>Keeps what WPF's bindings complain about.</summary>
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

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;
using Fadenstack.UI.Wpf.Views;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The Tools view in a window far off screen over the real view model and the fake catalog in one of the states of <see cref="ToolsScenes"/>
/// (the way a person gets to it), in a language of the test's choice. Binding errors WPF reports while it runs are kept in
/// <see cref="BindingErrors"/>. Make and use one on the STA thread (<see cref="Sta.Run"/>).
/// </summary>
internal sealed class ToolsHost : IDisposable
{
    private readonly BindingTrace _trace = new();
    private readonly SynchronizationContext? _previousContext;

    public ToolsHost(string state, string language = "en")
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
        Scene = ToolsScenes.Create(state, language);
        View = new ToolsView { DataContext = Scene.Page };
        Window = new Window
        {
            Width = 380, Height = 1400, Content = View, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        Window.Show();
        Pump();
    }

    public ToolsScene Scene { get; }

    public ToolsViewModel Page => Scene.Page;

    public FakeLocalToolsCatalog Catalog => Scene.Catalog;

    public Window Window { get; }

    public ToolsView View { get; }

    public IReadOnlyList<string> BindingErrors => _trace.Errors;

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

    public IEnumerable<DependencyObject> Tree() => Descendants(View);

    /// <summary>Everything the view shows right now: its elements that are visible (in a shown window).</summary>
    public IEnumerable<FrameworkElement> Visible() => Tree().OfType<FrameworkElement>().Where(e => e.IsVisible);

    /// <summary>The inputs and buttons the view puts on screen, not the parts inside another control's template.</summary>
    public IEnumerable<FrameworkElement> Controls() => Visible().Where(e =>
        (e is ButtonBase or TextBoxBase or PasswordBox or ComboBox || e is ScrollViewer { Focusable: true })
        && e is not RepeatButton && !(e is ToggleButton && e.TemplatedParent is ComboBox));

    /// <summary>The texts on screen: text blocks (a button's and a check box's label are text blocks too).</summary>
    public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

    public T Find<T>(string name) where T : FrameworkElement => Tree().OfType<T>().First(e => e.Name == name);

    public bool Has(string name) => Visible().Any(e => e.Name == name);

    public object? Focused() => Keyboard.FocusedElement;

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
        Scene.Dispose();
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

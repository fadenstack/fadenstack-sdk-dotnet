using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// A <see cref="WizardView"/> in a window far off screen on a real <see cref="ConnectionWizardViewModel"/> with the wizard's
/// fakes (the same ones the view model's own tests use), in a language of the test's choice. Binding errors WPF reports
/// while it runs are kept in <see cref="BindingErrors"/>. Make and use one on the STA thread (<see cref="Sta.Run"/>).
/// </summary>
internal sealed class WizardHost : IDisposable
{
    private static readonly PropertyInfo StepProperty = typeof(ConnectionWizardViewModel).GetProperty(nameof(ConnectionWizardViewModel.Step))!;

    private readonly BindingTrace _trace = new();

    public WizardHost(Func<WizardOptions, WizardOptions>? configure = null, string language = "en", bool start = false)
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var options = DefaultOptions;
        options = configure is null ? options : configure(options);
        Options = options;
        Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        Sign = new FakeServerSignIn(() => Clock.Now);
        Wizard = new ConnectionWizardViewModel(options, Store, Sign, Keys, Checker, Clock, new WpfDispatcher(), Clipboard, Links, () => Strings);
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(_trace);
        source.Switch.Level = SourceLevels.Warning;
        View = new WizardView { DataContext = Wizard, AppName = options.AppName };
        Window = new Window
        {
            Width = 420, Height = 800, Content = View, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        Window.Show();
        if (start)
        {
            _ = Wizard.StartAsync();
        }
        Pump();
    }

    public static WizardOptions DefaultOptions { get; } = new()
    {
        AppName = "Test app", TermsVersion = "2026-11", Agents = new[] { "main-agent" }, PasswordFallback = true, OffersDemo = true,
    };

    public UiStrings Strings { get; }

    public WizardOptions Options { get; }

    public Window Window { get; }

    public WizardView View { get; }

    public ConnectionWizardViewModel Wizard { get; }

    public FakeConnectionStore Store { get; } = new();

    public FakeServerSignIn Sign { get; }

    public FakeKeyStore Keys { get; } = new();

    public FakeEndpointChecker Checker { get; } = new();

    public ManualClock Clock { get; } = new();

    public FakeLinks Links { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public IReadOnlyList<string> BindingErrors => _trace.Errors;

    /// <summary>Puts the view model at a step without walking there (its setter is private): for the views of steps that last a moment.</summary>
    public void Show(WizardStep step)
    {
        StepProperty.GetSetMethod(true)!.Invoke(Wizard, new object[] { step });
        Pump();
    }

    /// <summary>Sets one of the view model's state properties (their setters are private).</summary>
    public void Set(string property, object? value) =>
        typeof(ConnectionWizardViewModel).GetProperty(property)!.GetSetMethod(true)!.Invoke(Wizard, new[] { value });

    /// <summary>Lets the dispatcher run what is queued (layout, bindings, the focus the wizard moves) down to idle.</summary>
    public void Pump()
    {
        Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
    }

    public IEnumerable<DependencyObject> Tree() => Descendants(View);

    /// <summary>Everything the view shows right now: its elements that are visible (in a shown window).</summary>
    public IEnumerable<FrameworkElement> Visible() => Tree().OfType<FrameworkElement>().Where(e => e.IsVisible);

    /// <summary>The inputs and buttons the wizard puts on screen, not the parts inside another control's template (a combo box's toggle, a scroll bar's arrows).</summary>
    public IEnumerable<FrameworkElement> Controls() => Visible().Where(e =>
        (e is ButtonBase or TextBoxBase or PasswordBox or ComboBox || e is ScrollViewer { Focusable: true })
        && e is not RepeatButton && !(e is ToggleButton && e.TemplatedParent is ComboBox));

    /// <summary>The texts on screen: text blocks (a button's and a check box's label are text blocks too).</summary>
    public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

    public T Find<T>(string name) where T : FrameworkElement => Tree().OfType<T>().First(e => e.Name == name);

    public object? Focused() => System.Windows.Input.FocusManager.GetFocusedElement(Window);

    /// <summary>A key down (and up) to the element, as the keyboard would send it.</summary>
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
        Wizard.Dispose();
        Window.Close();
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

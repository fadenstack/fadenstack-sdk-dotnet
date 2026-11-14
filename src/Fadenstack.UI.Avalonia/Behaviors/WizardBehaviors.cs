using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// "Is the value one of these?" for the wizard's views: <c>IsVisible="{Binding Step, Converter={x:Static b:WizardIs.In},
/// ConverterParameter='Unreachable,Denied'}"</c>. The parameter lists the names of an enum's values (or the texts) separated
/// by commas. <see cref="NotIn"/> says the opposite.
/// </summary>
public sealed class WizardIs : IValueConverter
{
    private readonly bool _invert;

    private WizardIs(bool invert) => _invert = invert;

    public static WizardIs In { get; } = new(false);

    public static WizardIs NotIn { get; } = new(true);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        var found = name is not null && parameter is string list
            && list.Split(',').Any(item => string.Equals(item.Trim(), name, StringComparison.Ordinal));
        return found != _invert;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when none of the values is true (nothing is offered): <c>&lt;MultiBinding Converter="{x:Static b:WizardNone.Instance}"&gt;</c>.</summary>
public sealed class WizardNone : IMultiValueConverter
{
    public static WizardNone Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is not true);
}

/// <summary>
/// A secret typed into a box (the sign-in password, an endpoint's key) goes to a command as its parameter, once, and the box
/// is emptied right after (ASVS 5.0 V13): it is never a binding on the box's text and never a view-model property. The
/// button names the box and the command; the button does the rest:
/// <c>&lt;Button b:WizardSecret.Box="{Binding #PasswordBox}" b:WizardSecret.Command="{Binding SignInWithPasswordCommand}" /&gt;</c>.
/// The button follows the command's availability itself (it has no <c>Command</c> of its own), and <see cref="EnterFromProperty"/>
/// names a field whose Enter moves on to the box while that is empty.
/// </summary>
public static class WizardSecret
{
    public static readonly AttachedProperty<TextBox?> BoxProperty =
        AvaloniaProperty.RegisterAttached<Button, TextBox?>("Box", typeof(WizardSecret));

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Button, ICommand?>("Command", typeof(WizardSecret));

    /// <summary>A field (the e-mail) whose Enter goes to the box while the box is empty; with text in the box Enter presses the button.</summary>
    public static readonly AttachedProperty<TextBox?> EnterFromProperty =
        AvaloniaProperty.RegisterAttached<Button, TextBox?>("EnterFrom", typeof(WizardSecret));

    private static readonly ConditionalWeakTable<Button, Follower> Followers = new();

    static WizardSecret()
    {
        BoxProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            button.Click -= OnClick;
            button.Click += OnClick;
        });
        CommandProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            button.Click -= OnClick;
            button.Click += OnClick;
            Followers.Remove(button);
            if (e.NewValue is ICommand command)
            {
                Followers.Add(button, new Follower(button, command));
            }
            else
            {
                button.IsEnabled = false;
            }
        });
        EnterFromProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            if (e.NewValue is TextBox from)
            {
                from.KeyDown += (_, key) =>
                {
                    if (key.Key == Key.Enter && key.KeyModifiers == KeyModifiers.None && GetBox(button) is { Text: null or "" } box)
                    {
                        box.Focus(NavigationMethod.Tab);
                        key.Handled = true;
                    }
                };
            }
        });
    }

    public static TextBox? GetBox(Button button) => button.GetValue(BoxProperty);

    public static void SetBox(Button button, TextBox? value) => button.SetValue(BoxProperty, value);

    public static ICommand? GetCommand(Button button) => button.GetValue(CommandProperty);

    public static void SetCommand(Button button, ICommand? value) => button.SetValue(CommandProperty, value);

    public static TextBox? GetEnterFrom(Button button) => button.GetValue(EnterFromProperty);

    public static void SetEnterFrom(Button button, TextBox? value) => button.SetValue(EnterFromProperty, value);

    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || GetBox(button) is not { } box || GetCommand(button) is not { } command)
        {
            return;
        }
        var secret = box.Text ?? "";
        if (command.CanExecute(secret))
        {
            command.Execute(secret);
            box.Text = ""; // gone from the view as soon as the command has it
        }
    }

    /// <summary>Keeps the button as available as the command says.</summary>
    private sealed class Follower
    {
        private readonly Button _button;
        private readonly ICommand _command;

        public Follower(Button button, ICommand command)
        {
            _button = button;
            _command = command;
            command.CanExecuteChanged += Update;
            Update(null, EventArgs.Empty);
        }

        private void Update(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _button.IsEnabled = _command.CanExecute(null);
            }
            else
            {
                Dispatcher.UIThread.Post(() => _button.IsEnabled = _command.CanExecute(null));
            }
        }
    }
}

/// <summary>
/// The wizard's keyboard: the focus goes to a step's first field when the step appears (<see cref="StepProperty"/>, bound to
/// the wizard's <c>Step</c>), and Enter anywhere in the wizard presses the step's primary button (<see cref="IsPrimaryProperty"/>;
/// the one that is visible and available).
/// </summary>
public static class WizardFocus
{
    /// <summary>Bound to the wizard's step: whenever it changes, the focus moves to the new step's first field.</summary>
    public static readonly AttachedProperty<object?> StepProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Step", typeof(WizardFocus));

    /// <summary>This control is the step's first field (it goes before the others, wherever it stands).</summary>
    public static readonly AttachedProperty<bool> IsFirstProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsFirst", typeof(WizardFocus));

    /// <summary>This button is its step's primary action: Enter presses it.</summary>
    public static readonly AttachedProperty<bool> IsPrimaryProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsPrimary", typeof(WizardFocus));

    static WizardFocus()
    {
        StepProperty.Changed.AddClassHandler<Control>((control, _) => Schedule(control));
    }

    public static object? GetStep(Control control) => control.GetValue(StepProperty);

    public static void SetStep(Control control, object? value) => control.SetValue(StepProperty, value);

    public static bool GetIsFirst(Control control) => control.GetValue(IsFirstProperty);

    public static void SetIsFirst(Control control, bool value) => control.SetValue(IsFirstProperty, value);

    public static bool GetIsPrimary(Button button) => button.GetValue(IsPrimaryProperty);

    public static void SetIsPrimary(Button button, bool value) => button.SetValue(IsPrimaryProperty, value);

    private static void Schedule(Control root)
    {
        // After the step's panels have changed their visibility.
        Dispatcher.UIThread.Post(() => FocusFirst(root), DispatcherPriority.Loaded);
    }

    /// <summary>Focuses the first field of what is shown: the marked one, else a text box, combo box or check box, else the first button, else <paramref name="root"/> itself.</summary>
    public static bool FocusFirst(Control root)
    {
        if (!root.IsEffectivelyVisible || TopLevel.GetTopLevel(root) is null)
        {
            return false;
        }
        var all = root.GetVisualDescendants().OfType<Control>().Where(Focusable).ToList();
        var target = all.FirstOrDefault(GetIsFirst) ?? all.FirstOrDefault(c => c is TextBox or ComboBox or CheckBox) ?? all.FirstOrDefault(c => c is Button);
        if (target is null && root.Focusable)
        {
            target = root;
        }
        return target?.Focus(NavigationMethod.Tab) ?? false;
    }

    private static bool Focusable(Control control) => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;

    /// <summary>Presses the first primary button that is visible and available (as a click, so the secret buttons answer too); false when there is none.</summary>
    public static bool PressPrimary(Control root)
    {
        var button = root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => GetIsPrimary(b) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
        if (button is null || ControlAutomationPeer.CreatePeerForElement(button) is not IInvokeProvider invoke)
        {
            return false;
        }
        invoke.Invoke();
        return true;
    }
}

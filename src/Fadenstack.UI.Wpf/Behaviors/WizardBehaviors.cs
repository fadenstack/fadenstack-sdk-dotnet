using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fadenstack.UI.Wpf.Behaviors;

/// <summary>
/// "Is the value one of these?" for the wizard's views, as <see cref="Visibility"/> (or as a Boolean for a target that wants
/// one): <c>Visibility="{Binding Step, Converter={x:Static b:WizardIn.Shown}, ConverterParameter='Unreachable,Denied'}"</c>.
/// The parameter lists the names of an enum's values (or the texts) separated by commas.
/// </summary>
public sealed class WizardIn : IValueConverter
{
    private readonly bool _invert;
    private readonly bool _boolean;

    private WizardIn(bool invert, bool boolean)
    {
        _invert = invert;
        _boolean = boolean;
    }

    /// <summary>Visible when the value is in the list.</summary>
    public static WizardIn Shown { get; } = new(false, false);

    /// <summary>Visible when the value is not in the list.</summary>
    public static WizardIn ShownUnless { get; } = new(true, false);

    /// <summary>True when the value is in the list.</summary>
    public static WizardIn Is { get; } = new(false, true);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        var found = name is not null && parameter is string list
            && list.Split(',').Any(item => string.Equals(item.Trim(), name, StringComparison.Ordinal));
        var result = found != _invert;
        return _boolean ? result : result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when none of the values is true (nothing is offered): <c>&lt;MultiBinding Converter="{x:Static b:WizardNone.Instance}"&gt;</c>.</summary>
public sealed class WizardNone : IMultiValueConverter
{
    public static WizardNone Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(v => v is not true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when every value is true (the password sign-in is offered and the step is the one): <c>&lt;MultiBinding Converter="{x:Static b:WizardAll.Instance}"&gt;</c>.</summary>
public sealed class WizardAll : IMultiValueConverter
{
    public static WizardAll Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A secret typed into a password box (the sign-in password, an endpoint's key) goes to a command as its parameter, once,
/// and the box is emptied right after (ASVS 5.0 V13): it is never a binding and never a view-model property. The button
/// names the box and the command; the button does the rest:
/// <c>&lt;Button b:WizardSecret.Box="{Binding ElementName=PasswordBox}" b:WizardSecret.Command="{Binding SignInWithPasswordCommand}" /&gt;</c>.
/// The button follows the command's availability itself (it has no <c>Command</c> of its own), and <see cref="EnterFromProperty"/>
/// names a field whose Enter moves on to the box while that is empty.
/// </summary>
public static class WizardSecret
{
    public static readonly DependencyProperty BoxProperty = DependencyProperty.RegisterAttached(
        "Box", typeof(PasswordBox), typeof(WizardSecret), new PropertyMetadata(null, OnWired));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(WizardSecret), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>A field (the e-mail) whose Enter goes to the box while the box is empty; with text in the box Enter presses the button.</summary>
    public static readonly DependencyProperty EnterFromProperty = DependencyProperty.RegisterAttached(
        "EnterFrom", typeof(UIElement), typeof(WizardSecret), new PropertyMetadata(null, OnEnterFromChanged));

    private static readonly ConditionalWeakTable<Button, Follower> Followers = new();

    public static PasswordBox? GetBox(Button button) => (PasswordBox?)button.GetValue(BoxProperty);

    public static void SetBox(Button button, PasswordBox? value) => button.SetValue(BoxProperty, value);

    public static ICommand? GetCommand(Button button) => (ICommand?)button.GetValue(CommandProperty);

    public static void SetCommand(Button button, ICommand? value) => button.SetValue(CommandProperty, value);

    public static UIElement? GetEnterFrom(Button button) => (UIElement?)button.GetValue(EnterFromProperty);

    public static void SetEnterFrom(Button button, UIElement? value) => button.SetValue(EnterFromProperty, value);

    private static void OnWired(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Button button)
        {
            button.Click -= OnClick;
            button.Click += OnClick;
        }
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
        {
            return;
        }
        OnWired(d, e);
        Followers.Remove(button);
        if (e.NewValue is ICommand command)
        {
            Followers.Add(button, new Follower(button, command));
        }
        else
        {
            button.IsEnabled = false;
        }
    }

    private static void OnEnterFromChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Button button && e.NewValue is UIElement from)
        {
            from.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && GetBox(button) is { Password.Length: 0 } box)
                {
                    box.Focus();
                    key.Handled = true;
                }
            };
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || GetBox(button) is not { } box || GetCommand(button) is not { } command)
        {
            return;
        }
        var secret = box.Password;
        if (command.CanExecute(secret))
        {
            command.Execute(secret);
            box.Clear(); // gone from the view as soon as the command has it
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
            if (_button.Dispatcher.CheckAccess())
            {
                _button.IsEnabled = _command.CanExecute(null);
            }
            else
            {
                _button.Dispatcher.BeginInvoke(new Action(() => _button.IsEnabled = _command.CanExecute(null)));
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
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(object), typeof(WizardFocus), new PropertyMetadata(null, (d, _) => Schedule(d)));

    /// <summary>This control is the step's first field (it goes before the others, wherever it stands).</summary>
    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.RegisterAttached(
        "IsFirst", typeof(bool), typeof(WizardFocus), new PropertyMetadata(false));

    /// <summary>This button is its step's primary action: Enter presses it.</summary>
    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.RegisterAttached(
        "IsPrimary", typeof(bool), typeof(WizardFocus), new PropertyMetadata(false));

    public static object? GetStep(DependencyObject element) => element.GetValue(StepProperty);

    public static void SetStep(DependencyObject element, object? value) => element.SetValue(StepProperty, value);

    public static bool GetIsFirst(DependencyObject element) => (bool)element.GetValue(IsFirstProperty);

    public static void SetIsFirst(DependencyObject element, bool value) => element.SetValue(IsFirstProperty, value);

    public static bool GetIsPrimary(DependencyObject element) => (bool)element.GetValue(IsPrimaryProperty);

    public static void SetIsPrimary(DependencyObject element, bool value) => element.SetValue(IsPrimaryProperty, value);

    private static void Schedule(DependencyObject d)
    {
        if (d is FrameworkElement root)
        {
            // After the step's panels have changed their visibility.
            root.Dispatcher.BeginInvoke(new Action(() => FocusFirst(root)), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Focuses the first field of what is shown: the marked one, else a text box, password box, combo box or check box, else the first button, else <paramref name="root"/> itself.</summary>
    public static bool FocusFirst(FrameworkElement root)
    {
        if (!root.IsVisible)
        {
            return false;
        }
        var all = Descendants(root).OfType<UIElement>().Where(Focusable).ToList();
        var target = all.FirstOrDefault(c => GetIsFirst(c))
            ?? all.FirstOrDefault(c => c is TextBox or PasswordBox or ComboBox or CheckBox)
            ?? all.FirstOrDefault(c => c is Button);
        if (target is null && root.Focusable)
        {
            target = root;
        }
        return target?.Focus() ?? false;
    }

    private static bool Focusable(UIElement element) => element.Focusable && element.IsVisible && element.IsEnabled;

    /// <summary>Presses the first primary button that is visible and available (as a click, so the secret buttons answer too); false when there is none.</summary>
    public static bool PressPrimary(DependencyObject root)
    {
        var button = Descendants(root).OfType<Button>().FirstOrDefault(b => GetIsPrimary(b) && b.IsVisible && b.IsEnabled);
        if (button is null)
        {
            return false;
        }
        if (button.Command is { } command)
        {
            var parameter = button.CommandParameter;
            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }
        else
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); // a secret button answers the click itself
        }
        return true;
    }

    /// <summary>The visual tree under <paramref name="root"/> in document order.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
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
}

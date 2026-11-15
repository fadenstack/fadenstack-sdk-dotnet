using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Fadenstack.UI.Wpf.Behaviors;

/// <summary>
/// A password box whose value is a row's typed secret (the Tools view's environment and header rows): <c>b:SecretBox.Value="{Binding Value}"</c>.
/// A <see cref="PasswordBox"/> cannot be bound, so this carries its password to the view model as the person types (and takes it back:
/// when the view model clears the value, as it does when the sheet is saved or closed, the box is emptied). The value is never shown
/// and is kept nowhere else (OWASP ASVS 5.0 V13); there is no plain text box for a secret.
/// </summary>
public static class SecretBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(SecretBox), new PropertyMetadata(false));

    public static string GetValue(DependencyObject element) => (string)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, string value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }
        // Hooked on the first value (the default is none, so the first "" of a binding is a change too); unhooked first so a box has one handler.
        box.PasswordChanged -= OnPasswordChanged;
        box.PasswordChanged += OnPasswordChanged;
        var value = e.NewValue as string ?? "";
        if ((bool)box.GetValue(UpdatingProperty) || box.Password == value)
        {
            return;
        }
        box.SetValue(UpdatingProperty, true);
        try
        {
            box.Password = value;
        }
        finally
        {
            box.SetValue(UpdatingProperty, false);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || (bool)box.GetValue(UpdatingProperty))
        {
            return;
        }
        box.SetValue(UpdatingProperty, true);
        try
        {
            box.SetCurrentValue(ValueProperty, box.Password);
            // The binding takes the value to the row; a one-way or missing binding leaves the attached value as it is.
            System.Windows.Data.BindingOperations.GetBindingExpression(box, ValueProperty)?.UpdateSource();
        }
        finally
        {
            box.SetValue(UpdatingProperty, false);
        }
    }
}

/// <summary>
/// The keyboard focus of a drawer or a modal sheet: when it appears, the focus moves to its first control, and when it goes, the focus
/// returns to what had it before (the card that opened it), unless the person has moved it elsewhere meanwhile. View mechanics only;
/// the view model decides when the drawer shows.
/// </summary>
public static class DrawerFocus
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DrawerFocus), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty ReturnToProperty = DependencyProperty.RegisterAttached(
        "ReturnTo", typeof(UIElement), typeof(DrawerFocus), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement drawer)
        {
            return;
        }
        drawer.Loaded -= OnLoaded;
        drawer.Unloaded -= OnUnloaded;
        if (e.NewValue is not true)
        {
            return;
        }
        drawer.Loaded += OnLoaded;
        drawer.Unloaded += OnUnloaded;
        if (drawer.IsLoaded)
        {
            Enter(drawer);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement drawer)
        {
            Enter(drawer);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement drawer || drawer.GetValue(ReturnToProperty) is not UIElement back)
        {
            return;
        }
        drawer.ClearValue(ReturnToProperty);
        back.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Only when the focus went with the drawer: a click on another card keeps its focus.
            var now = Keyboard.FocusedElement as DependencyObject;
            if (back.IsVisible && back.IsEnabled && (now is null || !(now is UIElement { IsVisible: true })))
            {
                back.Focus();
            }
        }), DispatcherPriority.Input);
    }

    private static void Enter(FrameworkElement drawer)
    {
        if (Keyboard.FocusedElement is UIElement before && !IsInside(drawer, before))
        {
            drawer.SetValue(ReturnToProperty, before);
        }
        drawer.Dispatcher.BeginInvoke(new Action(() =>
        {
            var first = WizardFocus.Descendants(drawer).OfType<UIElement>().FirstOrDefault(c => c.Focusable && c.IsVisible && c.IsEnabled && c is Control);
            first?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private static bool IsInside(DependencyObject root, DependencyObject element)
    {
        for (var d = element; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, root))
            {
                return true;
            }
        }
        return false;
    }
}

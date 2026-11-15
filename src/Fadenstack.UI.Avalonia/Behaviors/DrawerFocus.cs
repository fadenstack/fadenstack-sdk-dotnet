using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// The keyboard focus of a drawer or a modal sheet: when it appears, the focus moves to its first control, and when it goes, the
/// focus returns to what had it before (the card that opened it), unless the person has moved it elsewhere meanwhile. View
/// mechanics only; the view model decides when the drawer shows.
/// </summary>
public static class DrawerFocus
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(DrawerFocus));

    private static readonly AttachedProperty<IInputElement?> ReturnToProperty =
        AvaloniaProperty.RegisterAttached<Control, IInputElement?>("ReturnTo", typeof(DrawerFocus));

    static DrawerFocus() => IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Control drawer, AvaloniaPropertyChangedEventArgs e)
    {
        drawer.AttachedToVisualTree -= OnAttached;
        drawer.DetachedFromVisualTree -= OnDetached;
        if (e.NewValue is not true)
        {
            return;
        }
        drawer.AttachedToVisualTree += OnAttached;
        drawer.DetachedFromVisualTree += OnDetached;
        if (drawer.IsAttachedToVisualTree())
        {
            Enter(drawer);
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control drawer)
        {
            Enter(drawer);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control drawer || drawer.GetValue(ReturnToProperty) is not Control back)
        {
            return;
        }
        var focus = TopLevel.GetTopLevel(back)?.FocusManager;
        drawer.SetValue(ReturnToProperty, null);
        Dispatcher.UIThread.Post(() =>
        {
            // Only when the focus was in the drawer (or went with it): a click on another card keeps its focus.
            var now = focus?.GetFocusedElement() as Visual;
            if (back.IsAttachedToVisualTree() && back.IsEffectivelyVisible && (now is null or TopLevel || !now.IsAttachedToVisualTree()))
            {
                back.Focus();
            }
        });
    }

    private static void Enter(Control drawer)
    {
        var focus = TopLevel.GetTopLevel(drawer)?.FocusManager;
        var before = focus?.GetFocusedElement();
        if (before is Visual visual && !drawer.IsVisualAncestorOf(visual))
        {
            drawer.SetValue(ReturnToProperty, before);
        }
        Dispatcher.UIThread.Post(() =>
        {
            var first = drawer.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible);
            first?.Focus(NavigationMethod.Tab);
        });
    }
}

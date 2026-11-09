using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// A button only a deliberate press answers (sdk/09 L-58: the Bypass confirmation's switch): Enter on it does nothing --
/// Avalonia's button clicks on Enter when focused -- and it is never a window's default or cancel button. A click, a tap,
/// Space on it while focused, or UI Automation's Invoke still press it.
/// </summary>
public static class Deliberate
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsEnabled", typeof(Deliberate));

    static Deliberate()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            button.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is true)
            {
                button.IsDefault = false;
                button.IsCancel = false;
                button.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
        });
    }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true; // Esc goes on to the panel, which stops the turn and never answers the card
        }
    }
}

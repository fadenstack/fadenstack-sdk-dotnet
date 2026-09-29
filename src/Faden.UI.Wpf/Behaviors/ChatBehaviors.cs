using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Faden.UI.ViewModels;

namespace Faden.UI.Wpf.Behaviors;

/// <summary>
/// Keeps a scroll viewer at the bottom while new content arrives, as long as the user is there; scrolling up
/// to read stops it, scrolling back down resumes it (as Copilot's chat does).
/// </summary>
public static class StickToBottom
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StickToBottom), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty AtBottomProperty = DependencyProperty.RegisterAttached(
        "AtBottom", typeof(bool), typeof(StickToBottom), new PropertyMetadata(true));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }
        viewer.ScrollChanged -= OnScrollChanged;
        if ((bool)e.NewValue)
        {
            viewer.ScrollChanged += OnScrollChanged;
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The user scrolled: remember whether they are at the bottom.
            viewer.SetValue(AtBottomProperty, viewer.VerticalOffset >= viewer.ScrollableHeight - 24);
            return;
        }
        if ((bool)viewer.GetValue(AtBottomProperty))
        {
            viewer.ScrollToVerticalOffset(viewer.ExtentHeight);
        }
    }
}

/// <summary>Focuses the composer when the chat asks (after "Edit"): <c>w:ComposerFocus.Chat="{Binding}"</c>.</summary>
public static class ComposerFocus
{
    public static readonly DependencyProperty ChatProperty = DependencyProperty.RegisterAttached(
        "Chat", typeof(AgentChatViewModel), typeof(ComposerFocus), new PropertyMetadata(null, OnChanged));

    public static AgentChatViewModel? GetChat(DependencyObject element) => (AgentChatViewModel?)element.GetValue(ChatProperty);

    public static void SetChat(DependencyObject element, AgentChatViewModel? value) => element.SetValue(ChatProperty, value);

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(EventHandler), typeof(ComposerFocus));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }
        if (e.OldValue is AgentChatViewModel old && box.GetValue(HandlerProperty) is EventHandler previous)
        {
            old.ComposerFocusRequested -= previous;
        }
        if (e.NewValue is AgentChatViewModel chat)
        {
            EventHandler handler = (_, _) =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
                Keyboard.Focus(box);
            };
            chat.ComposerFocusRequested += handler;
            box.SetValue(HandlerProperty, handler);
        }
    }
}

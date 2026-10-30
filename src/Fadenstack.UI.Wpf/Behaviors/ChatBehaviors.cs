using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;

namespace Fadenstack.UI.Wpf.Behaviors;

/// <summary>
/// Makes a toggle button the drop-down of a split button: a click (or Down) opens its <see cref="FrameworkElement.ContextMenu"/>
/// under it; the toggle is checked while the menu is open. The menu closes as any menu does (a choice, Esc, a click elsewhere).
/// </summary>
public static class DropDown
{
    public static readonly DependencyProperty OpensProperty = DependencyProperty.RegisterAttached(
        "Opens", typeof(bool), typeof(DropDown), new PropertyMetadata(false, OnChanged));

    public static bool GetOpens(DependencyObject element) => (bool)element.GetValue(OpensProperty);

    public static void SetOpens(DependencyObject element, bool value) => element.SetValue(OpensProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.ToggleButton button)
        {
            return;
        }
        button.Click -= Open;
        button.KeyDown -= OpenByKey;
        if (e.NewValue is true)
        {
            button.Click += Open;
            button.KeyDown += OpenByKey;
        }
    }

    private static void OpenByKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || (e.Key == Key.System && e.SystemKey == Key.Down))
        {
            Open(sender, e);
            e.Handled = true;
        }
    }

    private static void Open(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { ContextMenu: { } menu } button)
        {
            return;
        }
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Closed -= Closed;
        menu.Closed += Closed;
        button.IsChecked = true;
        menu.IsOpen = true;
    }

    private static void Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: System.Windows.Controls.Primitives.ToggleButton button })
        {
            button.IsChecked = false;
        }
    }
}

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

/// <summary>
/// The skill picker's keys in the composer, only while it is open: Up and Down move, Enter (not Shift+Enter) and Tab pick,
/// Escape closes it. Otherwise the keys do what they always do (Enter sends). <c>b:SkillKeys.Picker="{Binding SkillPicker}"</c>.
/// </summary>
public static class SkillKeys
{
    public static readonly DependencyProperty PickerProperty = DependencyProperty.RegisterAttached(
        "Picker", typeof(SkillPickerViewModel), typeof(SkillKeys), new PropertyMetadata(null, OnChanged));

    public static SkillPickerViewModel? GetPicker(DependencyObject element) => (SkillPickerViewModel?)element.GetValue(PickerProperty);

    public static void SetPicker(DependencyObject element, SkillPickerViewModel? value) => element.SetValue(PickerProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }
        element.PreviewKeyDown -= OnKeyDown;
        if (e.NewValue is not null)
        {
            element.PreviewKeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Before the composer's own bindings (Enter sends) and the panel's (Escape stops); an input method that is still
        // composing gets ImeProcessed, not these keys.
        if (GetPicker((DependencyObject)sender) is not { IsOpen: true } picker)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                picker.Move(1);
                break;
            case Key.Up:
                picker.Move(-1);
                break;
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
            case Key.Tab:
                picker.Pick();
                break;
            case Key.Escape:
                picker.Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}

/// <summary>
/// Pictures into the composer: pasted (Ctrl+V, Shift+Insert, the context menu) or dropped as files, made ready by
/// <see cref="Pictures"/> and attached to the next message (<see cref="AgentChatViewModel.AttachPicture"/>, which refuses
/// them, and says why, for a model that does not see pictures). Text pastes as text, other files are the host's.
/// <c>b:PictureInput.Chat="{Binding}"</c> on the composer.
/// </summary>
public static class PictureInput
{
    public static readonly DependencyProperty ChatProperty = DependencyProperty.RegisterAttached(
        "Chat", typeof(AgentChatViewModel), typeof(PictureInput), new PropertyMetadata(null, OnChanged));

    public static AgentChatViewModel? GetChat(DependencyObject element) => (AgentChatViewModel?)element.GetValue(ChatProperty);

    public static void SetChat(DependencyObject element, AgentChatViewModel? value) => element.SetValue(ChatProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || (e.OldValue is null) == (e.NewValue is null))
        {
            return; // handlers are added once, and taken away when the chat goes
        }
        if (e.NewValue is null)
        {
            CommandManager.RemovePreviewCanExecuteHandler(element, OnCanPaste);
            CommandManager.RemovePreviewExecutedHandler(element, OnPaste);
            element.PreviewDragEnter -= OnDragOver;
            element.PreviewDragOver -= OnDragOver;
            element.PreviewDrop -= OnDrop;
            return;
        }
        // Preview: the text box inside says it cannot paste a picture, and refuses dropped files.
        CommandManager.AddPreviewCanExecuteHandler(element, OnCanPaste);
        CommandManager.AddPreviewExecutedHandler(element, OnPaste);
        element.AllowDrop = true;
        element.PreviewDragEnter += OnDragOver;
        element.PreviewDragOver += OnDragOver;
        element.PreviewDrop += OnDrop;
    }

    private static void OnCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && GetChat((DependencyObject)sender) is not null && Pictures.HasPicture(ClipboardData()))
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private static void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || GetChat((DependencyObject)sender) is not { } chat)
        {
            return;
        }
        var data = ClipboardData();
        if (!Pictures.HasPicture(data))
        {
            return; // text pastes as text
        }
        e.Handled = true;
        var files = Pictures.FilesIn(data);
        if (files.Count > 0)
        {
            _ = AddFilesAsync(chat, files);
            return;
        }
        // On this thread: the clipboard's bitmap belongs to it. A pasted picture has no name of its own.
        string? url = null;
        try
        {
            url = Pictures.BitmapIn(data) is { } bitmap ? Pictures.ToDataUrl(bitmap) : null;
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            // said below
        }
        if (url is null)
        {
            chat.PictureNotRead(chat.Strings.PastedPicture);
            return;
        }
        chat.AttachPicture(url);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (GetChat((DependencyObject)sender) is null || Pictures.FilesIn(e.Data).Count == 0)
        {
            return; // other files are the host's
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (GetChat((DependencyObject)sender) is not { } chat || Pictures.FilesIn(e.Data) is not { Count: > 0 } files)
        {
            return;
        }
        e.Handled = true;
        _ = AddFilesAsync(chat, files);
    }

    /// <summary>Reads the files off the UI thread, then attaches each, by its name, in order; one that is no picture is said.</summary>
    private static async Task AddFilesAsync(AgentChatViewModel chat, IReadOnlyList<string> files)
    {
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            try
            {
                var url = await Task.Run(() => Pictures.ReadFile(path));
                chat.AttachPicture(url, name);
            }
            catch (Exception)
            {
                chat.PictureNotRead(name);
            }
        }
    }

    private static IDataObject? ClipboardData()
    {
        try
        {
            return System.Windows.Clipboard.GetDataObject();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // another process holds the clipboard (CLIPBRD_E_CANT_OPEN)
        }
    }
}

/// <summary>Keeps a list's selected item in view (the skill the arrows moved to): <c>b:ScrollSelection.IntoView="True"</c>.</summary>
public static class ScrollSelection
{
    public static readonly DependencyProperty IntoViewProperty = DependencyProperty.RegisterAttached(
        "IntoView", typeof(bool), typeof(ScrollSelection), new PropertyMetadata(false, OnChanged));

    public static bool GetIntoView(DependencyObject element) => (bool)element.GetValue(IntoViewProperty);

    public static void SetIntoView(DependencyObject element, bool value) => element.SetValue(IntoViewProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }
        list.SelectionChanged -= OnSelectionChanged;
        if ((bool)e.NewValue)
        {
            list.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is { } item)
        {
            list.ScrollIntoView(item);
        }
    }
}

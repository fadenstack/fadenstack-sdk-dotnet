using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Fadenstack.UI.Services;
using IClipboard = Fadenstack.UI.Services.IClipboard;
using IPlatformClipboard = Avalonia.Input.Platform.IClipboard;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>
/// The Avalonia UI thread. Batches run at <see cref="DispatcherPriority.Background"/>, below rendering and input, so
/// a fast stream never delays a frame or a key press.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public AvaloniaUiDispatcher(Dispatcher? dispatcher = null) => _dispatcher = dispatcher ?? Dispatcher.UIThread;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action) => _dispatcher.Post(action, DispatcherPriority.Background);
}

/// <summary>The clipboard of the window the chat is in (it is told which one when it is needed, as windows come and go).</summary>
public sealed class AvaloniaClipboard : IClipboard
{
    private readonly Func<TopLevel?> _owner;

    public AvaloniaClipboard(Func<TopLevel?> owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public void SetText(string text)
    {
        if (_owner()?.Clipboard is { } clipboard)
        {
            _ = SetAsync(clipboard, text);
        }
    }

    private static async Task SetAsync(IPlatformClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Another process holds the clipboard for a moment: the copy is lost, nothing else is.
        }
    }
}

using Avalonia.Controls;
using Avalonia.Input;
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
public sealed class AvaloniaClipboard : IHtmlClipboard
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

    /// <summary>
    /// The text and the HTML together. HTML goes where the platform keeps it: Windows <c>HTML Format</c> (CF_HTML, with its
    /// header: <see cref="ClipboardHtml"/>), macOS <c>public.html</c>, X11 and Wayland <c>text/html</c>, as UTF-8. Where the
    /// platform's clipboard does not take it, the text goes alone.
    /// </summary>
    public void SetContent(string text, string html)
    {
        if (_owner()?.Clipboard is { } clipboard)
        {
            _ = SetContentAsync(clipboard, text, html);
        }
    }

    private static async Task SetContentAsync(IPlatformClipboard clipboard, string text, string html)
    {
        try
        {
            var item = new DataTransferItem();
            item.SetText(text);
            var windows = OperatingSystem.IsWindows();
            var name = windows ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html";
            item.Set(DataFormat.CreateBytesPlatformFormat(name), System.Text.Encoding.UTF8.GetBytes(windows ? ClipboardHtml.ToCfHtml(html) : html));
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception)
        {
            await SetAsync(clipboard, text); // the platform took no HTML, or another process holds the clipboard: the text still goes
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

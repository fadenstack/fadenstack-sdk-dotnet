using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Wpf.Services;

/// <summary>The WPF UI thread. Batches run at Background priority, so typing always comes first.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
}

/// <summary>
/// The Windows clipboard. A copy with HTML puts the Unicode text and the <c>HTML Format</c> (CF_HTML, with its header and
/// offsets: <see cref="ClipboardHtml"/>) together, so Word, Outlook and a browser's editor paste it formatted, with its
/// provenance mark, and a program that takes no formatting gets the text.
/// </summary>
public sealed class WpfClipboard : IHtmlClipboard
{
    public void SetText(string text) => Retry(() => Clipboard.SetText(text));

    public void SetContent(string text, string html) => Retry(() =>
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(DataFormats.Html, ClipboardHtml.ToCfHtml(html));
        Clipboard.SetDataObject(data, true);
    });

    private static void Retry(Action set)
    {
        // Another process can hold the clipboard for a moment (CLIPBRD_E_CANT_OPEN).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                set();
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }
}

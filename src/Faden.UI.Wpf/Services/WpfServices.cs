using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Faden.UI.Services;

namespace Faden.UI.Wpf.Services;

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

public sealed class WpfClipboard : IClipboard
{
    public void SetText(string text)
    {
        // Another process can hold the clipboard for a moment (CLIPBRD_E_CANT_OPEN).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }
}

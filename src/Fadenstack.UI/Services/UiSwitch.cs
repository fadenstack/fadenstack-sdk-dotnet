using System.Runtime.CompilerServices;

namespace Fadenstack.UI.Services;

/// <summary>
/// <c>await dispatcher.ToUi()</c>: the rest of the method runs on the UI thread. At once when the caller is already there; else the
/// continuation itself is posted to the dispatcher and runs inside the posted action.
/// </summary>
/// <remarks>
/// Not a <see cref="TaskCompletionSource{TResult}"/> completed from a posted action: its continuation runs where <c>SetResult</c> lets it,
/// and inside a WPF or Avalonia dispatcher callback the UI's synchronization context is current, so .NET does not run the await's
/// continuation inline there and queues it to the thread pool. The flow then goes on off the UI thread, and the first
/// <c>CanExecuteChanged</c> a button handles throws (Word's pane, 15 Nov 2026). Posting the continuation has no such window: it runs
/// on the UI thread or not at all.
/// </remarks>
internal readonly struct UiSwitch : INotifyCompletion
{
    private readonly IUiDispatcher _dispatcher;

    public UiSwitch(IUiDispatcher dispatcher) => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public UiSwitch GetAwaiter() => this;

    /// <summary>Already on the UI thread: the method goes on without a post.</summary>
    public bool IsCompleted => _dispatcher.CheckAccess();

    public void OnCompleted(Action continuation) => _dispatcher.Post(continuation);

    public void GetResult()
    {
    }
}

internal static class UiSwitchExtensions
{
    /// <summary>Back on the UI thread (see <see cref="UiSwitch"/>).</summary>
    public static UiSwitch ToUi(this IUiDispatcher dispatcher) => new(dispatcher);
}

namespace Faden.UI.Services;

/// <summary>The UI thread. Each skin implements it (WPF: the <c>Dispatcher</c>); view models never touch it directly.</summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread, later.</summary>
    void Post(Action action);
}

/// <summary>Runs everything on the calling thread: for tests and headless hosts without a UI thread.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public static InlineDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

public interface IClipboard
{
    void SetText(string text);
}

/// <summary>No clipboard (headless).</summary>
public sealed class NoClipboard : IClipboard
{
    public static NoClipboard Instance { get; } = new();

    public string? LastText { get; private set; }

    public void SetText(string text) => LastText = text;
}

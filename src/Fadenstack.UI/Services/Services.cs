namespace Fadenstack.UI.Services;

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

/// <summary>
/// A clipboard that also takes HTML: what a copy of an answer puts there, so it pastes formatted into mail and documents. A
/// skin's clipboard implements it where the platform can (WPF, Avalonia); the chat gives any other <see cref="IClipboard"/>
/// the text only. TS: <c>Clipboard.setHtml</c> (<c>writeClipboard</c>).
/// </summary>
public interface IHtmlClipboard : IClipboard
{
    /// <summary>Puts the text and the HTML on the clipboard together: a program that takes no formatting gets the text.</summary>
    void SetContent(string text, string html);
}

/// <summary>No clipboard (headless): it keeps what was copied, for tests.</summary>
public sealed class NoClipboard : IHtmlClipboard
{
    public static NoClipboard Instance { get; } = new();

    public string? LastText { get; private set; }

    /// <summary>The HTML of the last <see cref="SetContent"/>; null after a plain <see cref="SetText"/>.</summary>
    public string? LastHtml { get; private set; }

    public void SetText(string text)
    {
        LastText = text;
        LastHtml = null;
    }

    public void SetContent(string text, string html)
    {
        LastText = text;
        LastHtml = html;
    }
}

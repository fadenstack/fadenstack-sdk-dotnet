using CommunityToolkit.Mvvm.ComponentModel;
using Fadenstack.Tools;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.ViewModels;

/// <summary>How a chat view model behaves; the defaults suit a desktop chat panel.</summary>
public sealed class AgentChatOptions
{
    /// <summary>
    /// How often streamed updates reach the UI (default 33 ms, about 30 frames a second). Tokens in between
    /// are merged, so a fast model costs the UI thread one update per frame.
    /// </summary>
    public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(33);

    /// <summary>Turns shown when a session is opened (default 30); older ones load on request.</summary>
    public int InitialTurns { get; set; } = 30;

    /// <summary>Turns added by "Load earlier" (default 30).</summary>
    public int PageSize { get; set; } = 30;

    /// <summary>Characters of a tool result or argument shown (default 4 000).</summary>
    public int PreviewLength { get; set; } = 4000;

    public IClipboard Clipboard { get; set; } = NoClipboard.Instance;

    /// <summary>Renderers for gateway events, tried before the built-in ones.</summary>
    public IList<IAgentEventRenderer> Renderers { get; } = new List<IAgentEventRenderer>();

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// Runs before every message goes -- from the composer, a suggestion, Retry, or Edit and resend -- for what the
    /// host does first: check it may still send, take the context its tools read, add attachments (they go with this
    /// message). It runs on the UI thread; the chat is busy meanwhile and Stop cancels the token. When it throws,
    /// nothing is sent: the text stays in the composer and <see cref="AgentChatViewModel.Error"/> says why.
    /// </summary>
    public Func<OutgoingMessage, CancellationToken, Task>? BeforeSend { get; set; }

    /// <summary>
    /// Runs once after every send, however it ended: its turn finished, failed or was stopped, or
    /// <see cref="BeforeSend"/> threw.
    /// </summary>
    public Action<OutgoingMessage>? AfterSend { get; set; }
}

/// <summary>A message on its way, for <see cref="AgentChatOptions.BeforeSend"/> and <see cref="AgentChatOptions.AfterSend"/>.</summary>
public sealed class OutgoingMessage
{
    internal OutgoingMessage(string text) => Text = text;

    /// <summary>What the user sent, trimmed.</summary>
    public string Text { get; }
}

/// <summary>A mode in the picker, with what it means; above the ceiling it cannot be picked.</summary>
public sealed class ToolModeOption : ObservableObject
{
    private bool _isAvailable = true;

    internal ToolModeOption(ToolMode mode)
    {
        Mode = mode;
        (Label, Description) = mode switch
        {
            ToolMode.Off => ("Off", "No tools: the agent only answers."),
            ToolMode.ReadOnly => ("Read only", "Only tools that change nothing."),
            ToolMode.Ask => ("Ask", "Asks before anything that changes something."),
            _ => ("Auto", "Runs tools that change things; still asks before anything that cannot be undone."),
        };
    }

    public ToolMode Mode { get; }

    public string Label { get; }

    public string Description { get; }

    /// <summary>False above the ceiling the server sets for the agent.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        internal set => SetProperty(ref _isAvailable, value);
    }

    public override string ToString() => Label;
}

/// <summary>A session in the history list.</summary>
public sealed class SessionItemViewModel : ObservableObject
{
    private string _title;
    private DateTimeOffset _lastActivity;
    private string _whenText;
    private bool _isCurrent;

    internal SessionItemViewModel(string id, string? title, DateTimeOffset lastActivity, DateTimeOffset now)
    {
        Id = id;
        _title = string.IsNullOrWhiteSpace(title) ? "New chat" : title!;
        _lastActivity = lastActivity;
        _whenText = When(lastActivity, now);
    }

    public string Id { get; }

    public string Title
    {
        get => _title;
        internal set => SetProperty(ref _title, value);
    }

    public DateTimeOffset LastActivity => _lastActivity;

    public string WhenText
    {
        get => _whenText;
        private set => SetProperty(ref _whenText, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    internal void Touch(DateTimeOffset at, DateTimeOffset now)
    {
        _lastActivity = at;
        WhenText = When(at, now);
    }

    internal static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
        {
            return local.ToString("t");
        }
        if (local.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }
        return (today - local.Date).TotalDays < 7 ? local.ToString("dddd") : local.ToString("d");
    }
}

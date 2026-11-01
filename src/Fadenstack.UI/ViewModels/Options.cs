using System.Globalization;
using System.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using Fadenstack.Agents;
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
    /// What "Automatic" means in the panel's settings: the host's language (an add-in passes Office's UI language);
    /// <see cref="CultureInfo.CurrentUICulture"/> by default. The panel starts in it until the user picks another.
    /// </summary>
    public CultureInfo? AutomaticLanguage { get; set; }

    /// <summary>The languages the user can pick, by tag: <see cref="UiStrings.BuiltInLanguages"/> by default. Add the ones <see cref="TextOverrides"/> has.</summary>
    public IList<string>? Languages { get; set; }

    /// <summary>The host's own resources (a .resx with the same names): its words, and the texts of the languages it adds.</summary>
    public ResourceManager? TextOverrides { get; set; }

    /// <summary>A language the organisation fixed (a policy): the panel uses it, and its settings show it without a choice.</summary>
    public string? LockedLanguage { get; set; }

    /// <summary>
    /// Where the user's pick is kept: by default a <see cref="FilePreferenceStore"/>
    /// (<c>%APPDATA%\Fadenstack\preferences.json</c>), shared by every Fadenstack panel of the user on this computer.
    /// </summary>
    public IPreferenceStore? Preferences { get; set; }

    /// <summary>
    /// Texts fixed by the host instead of a language the user picks: the panel then offers no choice. Most hosts leave
    /// it out and pass <see cref="AutomaticLanguage"/>.
    /// </summary>
    public UiStrings? Strings { get; set; }

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

    /// <summary>
    /// A gate on running turns, for a host that runs several chats and limits how many work at the same time. It is awaited
    /// when a turn is about to start (the user's message is shown, nothing is sent yet) and returns a lease that is disposed
    /// once, when the turn ends. While it waits, <see cref="AgentChatViewModel.IsWaitingForSlot"/> is true and the turn says
    /// so; Stop cancels the wait and nothing is sent. Null (the default) means no gate.
    /// </summary>
    public Func<CancellationToken, Task<IDisposable>>? TurnGate { get; set; }

    /// <summary>
    /// Whether a small "Fadenstack" link to fadenstack.com shows on its own line under the AI notice (default true). False
    /// hides that line only: the notice and About, in the settings, stay.
    /// </summary>
    public bool Branding { get; set; } = true;

    /// <summary>
    /// Your app's short name in the <c>ref</c> of the links to fadenstack.com (<c>office</c> gives <c>office-panel</c> and
    /// <c>office-about</c>): lower-case letters and digits, words joined by hyphens, at most 20 characters. Anything else,
    /// and the default, is <c>sdk</c>. Never a user's, organisation's or server's name.
    /// </summary>
    public string RefSource { get; set; } = AboutLinks.DefaultSource;
}

/// <summary>A message on its way, for <see cref="AgentChatOptions.BeforeSend"/> and <see cref="AgentChatOptions.AfterSend"/>.</summary>
public sealed class OutgoingMessage
{
    internal OutgoingMessage(string text) => Text = text;

    /// <summary>What the user sent, trimmed.</summary>
    public string Text { get; }
}

/// <summary>A language in the panel's settings: <c>auto</c> (the host's) or a tag, named in itself ("Deutsch").</summary>
public sealed class LanguageOption
{
    internal LanguageOption(string id, string label, string language, bool isSelected)
    {
        Id = id;
        Label = label;
        Language = language;
        IsSelected = isSelected;
    }

    public string Id { get; }

    public string Label { get; }

    /// <summary>The language the label is written in, for the item's <c>xml:lang</c>.</summary>
    public string Language { get; }

    public bool IsSelected { get; }

    public override string ToString() => Label;
}

/// <summary>A mode in the picker, with what it means; above the ceiling it cannot be picked.</summary>
public sealed class ToolModeOption : ObservableObject
{
    private bool _isAvailable = true;
    private UiStrings _strings;

    internal ToolModeOption(ToolMode mode, UiStrings strings)
    {
        Mode = mode;
        _strings = strings;
    }

    public ToolMode Mode { get; }

    public string Label => _strings.ModeLabel(Mode);

    public string Description => _strings.ModeDescription(Mode);

    /// <summary>False above the ceiling the server sets for the agent.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        internal set => SetProperty(ref _isAvailable, value);
    }

    public override string ToString() => Label;

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>A level of thinking in the picker, with what it means.</summary>
public sealed class ReasoningOption : ObservableObject
{
    private bool _isSelected;
    private UiStrings _strings;

    internal ReasoningOption(ReasoningLevel level, UiStrings strings)
    {
        Level = level;
        _strings = strings;
    }

    public ReasoningLevel Level { get; }

    /// <summary>"Low"; "On" and "Off" when the agent offers only those two.</summary>
    public string Label => _strings.ReasoningLabel(Level);

    public string Description => _strings.ReasoningDescription(Level);

    /// <summary>The level new messages think at: the menu marks it.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => Label;

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>A session in the history list.</summary>
public sealed class SessionItemViewModel : ObservableObject
{
    private readonly bool _untitled;
    private UiStrings _strings;
    private string _title;
    private DateTimeOffset _lastActivity;
    private string _whenText;
    private bool _isCurrent;

    internal SessionItemViewModel(string id, string? title, DateTimeOffset lastActivity, DateTimeOffset now, UiStrings strings)
    {
        Id = id;
        _strings = strings;
        _untitled = string.IsNullOrWhiteSpace(title);
        _title = _untitled ? strings.NewChat : title!;
        _lastActivity = lastActivity;
        _whenText = When(lastActivity, now, strings);
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
        WhenText = When(at, now, _strings);
    }

    internal void Retext(UiStrings strings, DateTimeOffset now)
    {
        _strings = strings;
        if (_untitled)
        {
            Title = strings.NewChat;
        }
        WhenText = When(_lastActivity, now, strings);
    }

    /// <summary>The time today, "Yesterday", the weekday this week, else the date (written in the format culture).</summary>
    internal static string When(DateTimeOffset at, DateTimeOffset now, UiStrings strings)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
        {
            return local.ToString("t", strings.FormatCulture);
        }
        if (local.Date == today.AddDays(-1))
        {
            return strings.Yesterday;
        }
        return (today - local.Date).TotalDays < 7 ? local.ToString("dddd", strings.FormatCulture) : local.ToString("d", strings.FormatCulture);
    }
}

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Markdown;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Parts;

/// <summary>
/// One piece of an answer, in the order it happened: text, a tool call, an approval, a notice, citations.
/// Skins pick a template by type. Features and extensions add their own through the primitives
/// (<see cref="CardPart"/>, <see cref="TablePart"/>, <see cref="ProgressPart"/>, <see cref="DiffPart"/>),
/// so a feature needs no view of its own.
/// </summary>
public abstract class ResponsePart : ObservableObject
{
    /// <summary>The language changed: the part's texts are worked out in it, and every binding reads them again.</summary>
    internal virtual void Retext(UiStrings strings) => OnPropertyChanged(string.Empty);
}

/// <summary>Answer text as Markdown blocks, growing while it streams.</summary>
public sealed class MarkdownPart : ResponsePart
{
    private bool _isStreaming;

    public MarkdownPart(string? text = null, bool isStreaming = false, IClipboard? clipboard = null)
    {
        Document = new MarkdownDocument(text);
        _isStreaming = isStreaming;
        var copyTo = clipboard ?? NoClipboard.Instance;
        CopyCodeCommand = new RelayCommand<CodeBlock>(block =>
        {
            if (block is not null)
            {
                copyTo.SetText(block.Code);
            }
        });
    }

    /// <summary>Copies a code block (its button in the skin).</summary>
    public IRelayCommand<CodeBlock> CopyCodeCommand { get; }

    public MarkdownDocument Document { get; }

    public ObservableCollection<MarkdownBlock> Blocks => Document.Blocks;

    public bool IsStreaming
    {
        get => _isStreaming;
        internal set => SetProperty(ref _isStreaming, value);
    }

    public string Text => Document.Text;

    public bool IsLoaded => Document.IsLoaded;

    /// <summary>Lets go of the parsed blocks (never of a streaming answer); <see cref="Load"/> brings them back.</summary>
    public void Unload()
    {
        if (!_isStreaming)
        {
            Document.Unload();
        }
    }

    public void Load() => Document.Load();

    internal void Append(string text) => Document.Append(text);
}

/// <summary>The model's reasoning: collapsed, "Thinking..." while it streams, then "Thought for 4 s".</summary>
public sealed class ReasoningPart : ResponsePart
{
    private readonly StringBuilder _text = new();
    private readonly DateTimeOffset _started;
    private bool _isStreaming = true;
    private UiStrings _strings;
    private bool _isExpanded;
    private int? _seconds;

    internal ReasoningPart(DateTimeOffset started, UiStrings strings)
    {
        _started = started;
        _strings = strings;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>The text is built only when shown.</summary>
    public string Text => _isExpanded ? _text.ToString() : "";

    public string Title => _seconds is { } seconds ? _strings.ThoughtFor(seconds) : _strings.Thinking;

    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetProperty(ref _isStreaming, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Text));
            }
        }
    }

    public IRelayCommand ToggleCommand { get; }

    internal void Append(string text)
    {
        _text.Append(text);
        if (_isExpanded)
        {
            OnPropertyChanged(nameof(Text));
        }
    }

    internal void Complete(DateTimeOffset now)
    {
        IsStreaming = false;
        _seconds = Math.Max(1, (int)Math.Round((now - _started).TotalSeconds));
        OnPropertyChanged(nameof(Title));
    }

    internal override void Retext(UiStrings strings)
    {
        _strings = strings;
        base.Retext(strings);
    }
}

public enum ToolCallState
{
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    Denied,
    Cancelled,
}

/// <summary>One tool call: a line ("Ran read_page"), expandable to its arguments and result.</summary>
public sealed class ToolCallPart : ResponsePart
{
    private readonly int _previewLength;
    private UiStrings _strings;
    private ToolCallState _state;
    private TimeSpan? _duration;
    private bool _isExpanded;
    private string? _result;
    private byte[]? _picture;
    private IReadOnlyDictionary<string, object?>? _arguments;

    internal ToolCallPart(string callId, string name, string source, ToolClass toolClass,
        IReadOnlyDictionary<string, object?>? arguments, int previewLength, UiStrings strings)
    {
        _strings = strings;
        CallId = callId;
        Name = name;
        Source = source;
        Class = toolClass;
        _arguments = arguments;
        _previewLength = previewLength;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string CallId { get; }

    public string Name { get; }

    /// <summary><c>host</c>, <c>mcp</c> or <c>gateway</c>.</summary>
    public string Source { get; }

    public ToolClass Class { get; }

    public string ClassLabel => _strings.ToolClassLabel(Class);

    /// <summary>Where it runs, in words ("Runs in this app").</summary>
    public string SourceLabel => _strings.ToolSource(Source);

    public ToolCallState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public bool IsRunning => _state is ToolCallState.Running or ToolCallState.WaitingForApproval;

    /// <summary>What the line says: "Running read_page", "Ran read_page", "read_page was denied".</summary>
    public string Title => _state switch
    {
        ToolCallState.Running => _strings.ToolRunning(Name),
        ToolCallState.WaitingForApproval => _strings.ToolWaiting(Name),
        ToolCallState.Succeeded => _strings.ToolRan(Name),
        ToolCallState.Failed => _strings.ToolFailed(Name),
        ToolCallState.Denied => _strings.ToolDenied(Name),
        _ => _strings.ToolStopped(Name),
    };

    public TimeSpan? Duration
    {
        get => _duration;
        internal set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string DurationText => _duration is { } d ? _strings.Duration(d) : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ArgumentsText));
                OnPropertyChanged(nameof(ResultText));
            }
        }
    }

    /// <summary>Formatted only when the call is expanded.</summary>
    public string ArgumentsText => _isExpanded ? Format(_arguments) : "";

    public string ResultText => _isExpanded ? Preview(_result, _previewLength, _strings) : "";

    public bool HasDetails => (_arguments?.Count ?? 0) > 0 || !string.IsNullOrEmpty(_result);

    /// <summary>The first picture the tool returned (a rendered slide), PNG or JPEG; a UI shows it small under the line.</summary>
    public byte[]? Picture
    {
        get => _picture;
        private set
        {
            if (SetProperty(ref _picture, value))
            {
                OnPropertyChanged(nameof(HasPicture));
            }
        }
    }

    public bool HasPicture => _picture is { Length: > 0 };

    public IRelayCommand ToggleCommand { get; }

    internal void Complete(string outcome, string? result, TimeSpan duration, byte[]? picture = null)
    {
        _result = result;
        Picture = picture;
        Duration = duration;
        State = outcome switch
        {
            "ok" => ToolCallState.Succeeded,
            "denied" => ToolCallState.Denied,
            "cancelled" => ToolCallState.Cancelled,
            _ => ToolCallState.Failed,
        };
        OnPropertyChanged(nameof(HasDetails));
        if (_isExpanded)
        {
            OnPropertyChanged(nameof(ResultText));
        }
    }

    internal override void Retext(UiStrings strings)
    {
        _strings = strings;
        base.Retext(strings);
    }

    internal static string Preview(string? text, int length, UiStrings strings) =>
        text is null ? "" : text.Length <= length ? text : text.Substring(0, length) + "\n" + strings.MoreCharacters(text.Length - length);

    private static string Format(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "";
        }
        try
        {
            return JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (NotSupportedException)
        {
            return string.Join("\n", arguments.Select(a => $"{a.Key}: {a.Value}"));
        }
    }
}

public enum ApprovalState
{
    Pending,
    Approved,
    ApprovedForSession,
    Denied,
    Expired,
    ApprovedAlways,
    ApprovedForTime,
}

/// <summary>
/// "Run delete_page?" with Allow, Allow for this chat, Allow for 1 hour, Allow always, and Deny -- answered in place, like Copilot.
/// "Allow for this chat" and "Allow always" are offered only where the request allows them: never for a destructive
/// or unclassified tool, nor for a call an admin's rule covers.
/// </summary>
public sealed class ApprovalPart : ResponsePart
{
    private readonly PendingApproval? _pending;
    private readonly string _arguments;
    private readonly int _previewLength;
    private UiStrings _strings;
    private ApprovalState _state;

    internal ApprovalPart(PendingApproval pending, int previewLength, UiStrings strings)
        : this(pending.Request, ApprovalState.Pending, previewLength, strings)
    {
        _pending = pending;
    }

    internal ApprovalPart(ApprovalRequest request, ApprovalState state, int previewLength, UiStrings strings)
    {
        Request = request;
        _strings = strings;
        _state = state;
        _arguments = Arguments(request);
        _previewLength = previewLength;
        AllowCommand = new RelayCommand(() => Decide(ApprovalDecision.Approve(), ApprovalState.Approved), () => IsPending);
        AllowForSessionCommand = new RelayCommand(() => Decide(ApprovalDecision.ApproveForSession(), ApprovalState.ApprovedForSession),
            () => IsPending && CanAllowForSession);
        AllowForTimeCommand = new RelayCommand(() => Decide(ApprovalDecision.ApproveForTime(), ApprovalState.ApprovedForTime),
            () => IsPending && CanAllowForTime);
        AllowAlwaysCommand = new RelayCommand(() => Decide(ApprovalDecision.ApproveAlways(), ApprovalState.ApprovedAlways),
            () => IsPending && CanAllowAlways);
        DenyCommand = new RelayCommand(() => Decide(ApprovalDecision.Deny(), ApprovalState.Denied), () => IsPending);
    }

    /// <summary>Whether the card offers "Allow for this chat".</summary>
    public bool CanAllowForSession => Request.CanAllowForSession;

    /// <summary>Whether the card offers "Allow for 1 hour": only where the server advertises it.</summary>
    public bool CanAllowForTime => Request.CanAllowForTime;

    /// <summary>Whether the card offers any choice beyond "Allow" (the drop-down of the split button).</summary>
    public bool HasAllowChoices => CanAllowForSession || CanAllowForTime || CanAllowAlways;

    /// <summary>Whether the card offers "Allow always".</summary>
    public bool CanAllowAlways => Request.CanAllowAlways;

    public ApprovalRequest Request { get; }

    public string ToolName => Request.ToolName;

    public string Title => _strings.ApprovalTitle(Request.ToolName);

    /// <summary>The title as runs, the tool's name in code style (a skin sets it in its mono font), so it stands on its own row.</summary>
    public IReadOnlyList<InlineRun> TitleInlines => MarkdownParser.Inlines(_strings.ApprovalTitle("`" + Request.ToolName + "`"));

    /// <summary>For a tool from a local MCP server: "From Files, an MCP server on this computer"; else "".</summary>
    public string SourceLine => IsMcpTool ? _strings.ApprovalFromMcp(Request.Server!) : "";

    public bool HasSourceLine => IsMcpTool;

    private bool IsMcpTool => Request.Source == "mcp" && Request.Server is { Length: > 0 };

    public ToolClass Class => Request.ToolClass;

    public bool IsDestructive => Request.ToolClass is ToolClass.Destructive or ToolClass.Unknown;

    /// <summary>
    /// The card's badge: a server tool; for the agent's own tools, that it goes outside (a web search) or what its class
    /// means. Reaching outside matters more than changing nothing here; a destructive tool still says what it is.
    /// </summary>
    public string ClassLabel => Request.Source == "gateway" ? _strings.ServerTool
        : GoesOutside ? _strings.ApprovalOutside
        : _strings.ApprovalClassLabel(Request.ToolClass);

    /// <summary>
    /// Why it asks: the admin's message for a rule or a gateway tool; for the agent's own tools, that what it is given
    /// leaves the organisation (a web search) or what its class means.
    /// </summary>
    public string Reason => Request.Source == "gateway" || Request.RequiredByRule ? Request.Reason
        : GoesOutside ? _strings.ApprovalOutsideWhy
        : IsMcpTool && Request.ToolClass == ToolClass.Unknown ? McpReason()
        : _strings.ApprovalWhy(Request.ToolClass);

    /// <summary>An MCP tool nobody classified: that the organisation has not checked it, then what the server itself says.</summary>
    private string McpReason() => Request.DestructiveHint == true ? _strings.ApprovalUnchecked + " " + _strings.ApprovalServerDestructive
        : Request.ReadOnlyHint == true ? _strings.ApprovalUnchecked + " " + _strings.ApprovalServerReadOnly
        : _strings.ApprovalUnchecked;

    private bool GoesOutside => Request.OpenWorld && Request.ToolClass != ToolClass.Destructive;

    /// <summary>What the call acts on, as the tool describes it (<c>button "Send" on mail.example.com</c>), or "".</summary>
    public string Target => Request.Target ?? "";

    public string ArgumentsText => ToolCallPart.Preview(_arguments, _previewLength, _strings);

    public ApprovalState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(StateText));
                AllowCommand.NotifyCanExecuteChanged();
                AllowForSessionCommand.NotifyCanExecuteChanged();
                AllowForTimeCommand.NotifyCanExecuteChanged();
                AllowAlwaysCommand.NotifyCanExecuteChanged();
                DenyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPending => _state == ApprovalState.Pending;

    public string StateText => _state switch
    {
        ApprovalState.Approved => _strings.Allowed,
        ApprovalState.ApprovedForSession => _strings.AllowedForChat,
        ApprovalState.ApprovedForTime => _strings.AllowedForTime,
        ApprovalState.ApprovedAlways => _strings.AllowedAlways,
        ApprovalState.Denied => _strings.Denied,
        ApprovalState.Expired => _strings.NoAnswer,
        _ => "",
    };

    public IRelayCommand AllowCommand { get; }

    public IRelayCommand AllowForSessionCommand { get; }

    public IRelayCommand AllowForTimeCommand { get; }

    public IRelayCommand AllowAlwaysCommand { get; }

    public IRelayCommand DenyCommand { get; }

    internal override void Retext(UiStrings strings)
    {
        _strings = strings;
        base.Retext(strings);
    }

    private void Decide(ApprovalDecision decision, ApprovalState state)
    {
        if (_pending is null || !_pending.Resolve(decision))
        {
            return;
        }
        State = state;
    }

    private static string Arguments(ApprovalRequest request) =>
        request.Arguments.Count == 0 ? "" : string.Join("\n", request.Arguments.Select(a => $"{a.Key}: {Value(a.Value)}"));

    private static string Value(object? value) => value switch
    {
        null => "null",
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        JsonElement j => j.GetRawText(),
        _ => value.ToString() ?? "",
    };
}

public enum NoticeSeverity
{
    Info,
    Warning,
    Blocked,
    Error,
}

/// <summary>A refusal, a limit, an error, or a feature's notice ("3 names masked locally").</summary>
public sealed class NoticePart : ResponsePart
{
    private readonly (string Title, string Message, string? Detail) _fixed;
    private readonly Func<UiStrings, (string Title, string Message, string? Detail)>? _texts;
    private UiStrings? _strings;

    /// <summary>A notice with its own texts (a module's: in the module's words).</summary>
    public NoticePart(NoticeSeverity severity, string title, string message, string? detail = null)
    {
        Severity = severity;
        _fixed = (title, message, detail);
    }

    private NoticePart(NoticeSeverity severity, UiStrings strings, Func<UiStrings, (string Title, string Message, string? Detail)> texts)
        : this(severity, "", "")
    {
        _strings = strings;
        _texts = texts;
    }

    public NoticeSeverity Severity { get; }

    public string Title => Current.Title;

    public string Message => Current.Message;

    /// <summary>For support: the decision id of a refusal.</summary>
    public string? Detail => Current.Detail;

    private (string Title, string Message, string? Detail) Current => _texts is not null && _strings is not null ? _texts(_strings) : _fixed;

    /// <summary>A notice in the panel's words: in its language, and again in the next one the user picks.</summary>
    internal static NoticePart Localized(UiStrings strings, NoticeSeverity severity, Func<UiStrings, (string Title, string Message, string? Detail)> texts) =>
        new(severity, strings, texts);

    internal static NoticePart From(GovernanceNotice notice, UiStrings strings)
    {
        var severity = notice.Outcome switch
        {
            "prompt_blocked" or "response_blocked" => NoticeSeverity.Blocked,
            "tool_denied" or "rate_limited" or "empty_answer" => NoticeSeverity.Warning,
            _ => NoticeSeverity.Info,
        };
        return Localized(strings, severity, s => (
            s.NoticeTitle(notice.Outcome) ?? s.NoticeFallback,
            // An empty answer is the SDK's own notice, not a policy's: no admin's message to show, the panel's words instead.
            notice.Outcome == "empty_answer" ? (notice.Category == "length" ? s.EmptyAnswerLength : s.EmptyAnswer)
                : notice.RetryAfter is { } wait ? $"{notice.Message} {s.TryAgainIn(Math.Ceiling(wait.TotalSeconds))}"
                : notice.Message,
            notice.DecisionId is null ? null : s.Decision(notice.DecisionId)));
    }

    internal override void Retext(UiStrings strings)
    {
        if (_texts is not null)
        {
            _strings = strings;
        }
        base.Retext(strings);
    }
}

public sealed record Citation(string Title, string? Detail, double? Score);

/// <summary>"Used 3 references", expandable.</summary>
public sealed class CitationsPart : ResponsePart
{
    private UiStrings _strings;
    private bool _isExpanded;

    /// <param name="citations">The references, in order.</param>
    /// <param name="strings">The texts (the turn's, <see cref="ViewModels.TurnViewModel.Strings"/>); the current UI language's by default.</param>
    public CitationsPart(IEnumerable<Citation> citations, UiStrings? strings = null)
    {
        _strings = strings ?? UiStrings.Current;
        Citations = new ObservableCollection<Citation>(citations);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public ObservableCollection<Citation> Citations { get; }

    public string Title => _strings.UsedReferences(Citations.Count);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IRelayCommand ToggleCommand { get; }

    internal override void Retext(UiStrings strings)
    {
        _strings = strings;
        base.Retext(strings);
    }

    internal void Add(IEnumerable<Citation> more)
    {
        foreach (var c in more)
        {
            Citations.Add(c);
        }
        OnPropertyChanged(nameof(Title));
    }
}

// -- primitives for features and modules ------------------------------------------

public sealed record CardField(string Label, string Value);

/// <summary>A titled card with fields.</summary>
public sealed class CardPart : ResponsePart
{
    public CardPart(string title, IEnumerable<CardField> fields, string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
        Fields = fields.ToList();
    }

    public string Title { get; }

    public string? Subtitle { get; }

    public IReadOnlyList<CardField> Fields { get; }
}

/// <summary>A plain table.</summary>
public sealed class TablePart : ResponsePart
{
    public TablePart(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, string? title = null)
    {
        Headers = headers;
        Rows = rows.ToList();
        Title = title;
    }

    public string? Title { get; }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
}

/// <summary>Progress: a value from 0 to 1, or none for "working".</summary>
public sealed class ProgressPart : ResponsePart
{
    private string _label;
    private double? _value;

    public ProgressPart(string label, double? value = null)
    {
        _label = label;
        _value = value;
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public double? Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(IsIndeterminate));
            }
        }
    }

    public bool IsIndeterminate => _value is null;
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>A change, line by line (e.g. what a write tool would do to a page).</summary>
public sealed class DiffPart : ResponsePart
{
    public DiffPart(string title, IEnumerable<DiffLine> lines)
    {
        Title = title;
        Lines = lines.ToList();
    }

    public string Title { get; }

    public IReadOnlyList<DiffLine> Lines { get; }
}

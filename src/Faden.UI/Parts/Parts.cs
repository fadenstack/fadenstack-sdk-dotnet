using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Faden.Agents;
using Faden.Governance;
using Faden.Tools;
using Faden.UI.Markdown;
using Faden.UI.Services;

namespace Faden.UI.Parts;

/// <summary>
/// One piece of an answer, in the order it happened: text, a tool call, an approval, a notice, citations.
/// Skins pick a template by type. Features and EE modules add their own through the primitives
/// (<see cref="CardPart"/>, <see cref="TablePart"/>, <see cref="ProgressPart"/>, <see cref="DiffPart"/>),
/// so a feature needs no view of its own.
/// </summary>
public abstract class ResponsePart : ObservableObject
{
}

/// <summary>Answer text as Markdown blocks, growing while it streams.</summary>
public sealed class MarkdownPart : ResponsePart
{
    private bool _isStreaming;

    public MarkdownPart(string? text = null, bool isStreaming = false)
    {
        Document = new MarkdownDocument(text);
        _isStreaming = isStreaming;
    }

    public MarkdownDocument Document { get; }

    public ObservableCollection<MarkdownBlock> Blocks => Document.Blocks;

    public bool IsStreaming
    {
        get => _isStreaming;
        internal set => SetProperty(ref _isStreaming, value);
    }

    public string Text => Document.Text;

    internal void Append(string text) => Document.Append(text);
}

/// <summary>The model's reasoning: collapsed, "Thinking..." while it streams, then "Thought for 4 s".</summary>
public sealed class ReasoningPart : ResponsePart
{
    private readonly StringBuilder _text = new();
    private readonly DateTimeOffset _started;
    private bool _isStreaming = true;
    private bool _isExpanded;
    private string _title = "Thinking...";

    internal ReasoningPart(DateTimeOffset started)
    {
        _started = started;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>The text is built only when shown.</summary>
    public string Text => _isExpanded ? _text.ToString() : "";

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

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
        var seconds = Math.Max(1, (int)Math.Round((now - _started).TotalSeconds));
        Title = $"Thought for {seconds} s";
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
    private ToolCallState _state;
    private TimeSpan? _duration;
    private bool _isExpanded;
    private string? _result;
    private IReadOnlyDictionary<string, object?>? _arguments;

    internal ToolCallPart(string callId, string name, string source, ToolClass toolClass,
        IReadOnlyDictionary<string, object?>? arguments, int previewLength)
    {
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

    public string ClassLabel => Class switch
    {
        ToolClass.Read => "Read",
        ToolClass.Write => "Write",
        ToolClass.Destructive => "Destructive",
        _ => "Unclassified",
    };

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
        ToolCallState.Running => $"Running {Name}",
        ToolCallState.WaitingForApproval => $"{Name} is waiting for your approval",
        ToolCallState.Succeeded => $"Ran {Name}",
        ToolCallState.Failed => $"{Name} failed",
        ToolCallState.Denied => $"{Name} was not run",
        _ => $"{Name} was stopped",
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

    public string DurationText => _duration is { } d ? (d.TotalSeconds >= 1 ? $"{d.TotalSeconds:0.0} s" : $"{d.TotalMilliseconds:0} ms") : "";

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

    public string ResultText => _isExpanded ? Preview(_result, _previewLength) : "";

    public bool HasDetails => (_arguments?.Count ?? 0) > 0 || !string.IsNullOrEmpty(_result);

    public IRelayCommand ToggleCommand { get; }

    internal void Complete(string outcome, string? result, TimeSpan duration)
    {
        _result = result;
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

    internal static string Preview(string? text, int length) =>
        text is null ? "" : text.Length <= length ? text : text.Substring(0, length) + $"\n... ({text.Length - length:N0} more characters)";

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
}

/// <summary>"Run delete_page?" with Allow, Allow for this chat, and Deny -- answered in place, like Copilot.</summary>
public sealed class ApprovalPart : ResponsePart
{
    private readonly PendingApproval? _pending;
    private ApprovalState _state;

    internal ApprovalPart(PendingApproval pending, int previewLength)
        : this(pending.Request, ApprovalState.Pending, previewLength)
    {
        _pending = pending;
    }

    internal ApprovalPart(ApprovalRequest request, ApprovalState state, int previewLength)
    {
        Request = request;
        _state = state;
        ArgumentsText = ToolCallPart.Preview(Arguments(request), previewLength);
        AllowCommand = new RelayCommand(() => Decide(ApprovalDecision.Approve(), ApprovalState.Approved), () => IsPending);
        AllowForSessionCommand = new RelayCommand(() => Decide(ApprovalDecision.ApproveForSession(), ApprovalState.ApprovedForSession), () => IsPending);
        DenyCommand = new RelayCommand(() => Decide(ApprovalDecision.Deny(), ApprovalState.Denied), () => IsPending);
    }

    public ApprovalRequest Request { get; }

    public string ToolName => Request.ToolName;

    public string Title => $"Run {Request.ToolName}?";

    public ToolClass Class => Request.ToolClass;

    public bool IsDestructive => Request.ToolClass is ToolClass.Destructive or ToolClass.Unknown;

    public string ClassLabel => Request.Source == "gateway" ? "Server tool" : Request.ToolClass switch
    {
        ToolClass.Read => "Read",
        ToolClass.Write => "Changes data",
        ToolClass.Destructive => "Cannot be undone",
        _ => "Unclassified",
    };

    public string Reason => Request.Reason;

    public string ArgumentsText { get; }

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
                DenyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPending => _state == ApprovalState.Pending;

    public string StateText => _state switch
    {
        ApprovalState.Approved => "Allowed",
        ApprovalState.ApprovedForSession => "Allowed for this chat",
        ApprovalState.Denied => "Denied",
        ApprovalState.Expired => "No answer in time",
        _ => "",
    };

    public IRelayCommand AllowCommand { get; }

    public IRelayCommand AllowForSessionCommand { get; }

    public IRelayCommand DenyCommand { get; }

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
    public NoticePart(NoticeSeverity severity, string title, string message, string? detail = null)
    {
        Severity = severity;
        Title = title;
        Message = message;
        Detail = detail;
    }

    public NoticeSeverity Severity { get; }

    public string Title { get; }

    public string Message { get; }

    /// <summary>For support: the decision id of a refusal.</summary>
    public string? Detail { get; }

    internal static NoticePart From(GovernanceNotice notice)
    {
        var (severity, title) = notice.Outcome switch
        {
            "prompt_blocked" => (NoticeSeverity.Blocked, "Your message was not sent"),
            "response_blocked" => (NoticeSeverity.Blocked, "The answer was stopped"),
            "tool_denied" => (NoticeSeverity.Warning, "A tool was not allowed"),
            "rate_limited" => (NoticeSeverity.Warning, "Too many requests"),
            "budget_exceeded" => (NoticeSeverity.Warning, "Budget used up"),
            "round_limit" => (NoticeSeverity.Info, "Stopped"),
            _ => (NoticeSeverity.Info, "Notice"),
        };
        var message = notice.RetryAfter is { } wait ? $"{notice.Message} Try again in {Math.Ceiling(wait.TotalSeconds)} s." : notice.Message;
        return new NoticePart(severity, title, message, notice.DecisionId is null ? null : $"Decision {notice.DecisionId}");
    }
}

public sealed record Citation(string Title, string? Detail, double? Score);

/// <summary>"Used 3 references", expandable.</summary>
public sealed class CitationsPart : ResponsePart
{
    private bool _isExpanded;

    public CitationsPart(IEnumerable<Citation> citations)
    {
        Citations = new ObservableCollection<Citation>(citations);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public ObservableCollection<Citation> Citations { get; }

    public string Title => Citations.Count == 1 ? "Used 1 reference" : $"Used {Citations.Count} references";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IRelayCommand ToggleCommand { get; }

    internal void Add(IEnumerable<Citation> more)
    {
        foreach (var c in more)
        {
            Citations.Add(c);
        }
        OnPropertyChanged(nameof(Title));
    }
}

// -- primitives for features and modules (01-design §9) ------------------------------------------

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

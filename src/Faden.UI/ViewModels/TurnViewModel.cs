using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Faden.Agents;
using Faden.Governance;
using Faden.Tools;
using Faden.UI.Parts;
using Faden.UI.Services;

namespace Faden.UI.ViewModels;

public enum TurnState
{
    Running,
    WaitingForApproval,
    Completed,

    /// <summary>The prompt was refused; it will not be sent again.</summary>
    Blocked,
    Stopped,
    Failed,
}

/// <summary>
/// One exchange: the user's message and the answer's parts in the order they happened. The chat's list is a
/// list of turns, so a skin virtualizes whole exchanges.
/// </summary>
public sealed class TurnViewModel : ObservableObject
{
    private readonly AgentChatViewModel? _chat;
    private readonly int _previewLength;
    private readonly Dictionary<string, ToolCallPart> _tools = new();
    private MarkdownPart? _text;
    private ReasoningPart? _reasoning;
    private TurnState _state;
    private ChatOutcome? _outcome;
    private string? _error;
    private string _usageText = "";
    private string _durationText = "";

    internal TurnViewModel(AgentChatViewModel? chat, string userText, DateTimeOffset timestamp, int previewLength, TurnState state = TurnState.Running)
    {
        _chat = chat;
        UserText = userText;
        Timestamp = timestamp;
        _previewLength = previewLength;
        _state = state;
        CopyCommand = new RelayCommand(Copy, () => AnswerText.Length > 0);
        EditCommand = new RelayCommand(() => _chat?.Edit(UserText), () => _chat is not null && !IsRunning);
        RetryCommand = new AsyncRelayCommand(() => _chat?.SendAsync(UserText) ?? Task.CompletedTask,
            () => _chat is not null && !IsRunning && _state is TurnState.Failed or TurnState.Stopped);
    }

    public string? Id { get; internal set; }

    public string UserText { get; }

    public DateTimeOffset Timestamp { get; }

    public ObservableCollection<ResponsePart> Parts { get; } = new();

    public TurnState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPromptBlocked));
                OnPropertyChanged(nameof(StatusText));
                EditCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                CopyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunning => _state is TurnState.Running or TurnState.WaitingForApproval;

    /// <summary>The user's message was refused: shown with a mark and "Edit and resend".</summary>
    public bool IsPromptBlocked => _state == TurnState.Blocked;

    public ChatOutcome? Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    public string StatusText => _state switch
    {
        TurnState.Running => _text is null && _tools.Count == 0 ? "Working..." : "",
        TurnState.WaitingForApproval => "Waiting for your approval",
        TurnState.Stopped => "Stopped",
        TurnState.Failed => "Something went wrong",
        _ => "",
    };

    public string? ErrorMessage
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    /// <summary>The answer's Markdown, for copying.</summary>
    public string AnswerText => string.Join("\n\n", Parts.OfType<MarkdownPart>().Select(p => p.Text).Where(t => t.Length > 0));

    public IRelayCommand CopyCommand { get; }

    /// <summary>Puts the message back into the composer (e.g. to rephrase a refused prompt).</summary>
    public IRelayCommand EditCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    /// <summary>Adds a part (renderers and modules use this for their primitives).</summary>
    public void AddPart(ResponsePart part)
    {
        CloseText();
        Parts.Add(part);
        OnPropertyChanged(nameof(StatusText));
    }

    // -- applied on the UI thread, in the order the agent reported them --------------------------------

    internal void AppendText(string text)
    {
        if (_text is null)
        {
            _text = new MarkdownPart(isStreaming: true);
            Parts.Add(_text);
            OnPropertyChanged(nameof(StatusText));
        }
        _text.Append(text);
    }

    internal void AppendReasoning(string text, DateTimeOffset now)
    {
        if (_reasoning is null)
        {
            _reasoning = new ReasoningPart(now);
            Parts.Add(_reasoning);
        }
        _reasoning.Append(text);
    }

    internal void ToolStarted(ToolCallStarted e)
    {
        CloseText();
        var part = new ToolCallPart(e.CallId, e.Name, e.Source, e.Class, e.Arguments, _previewLength);
        _tools[Key(e.CallId, e.Name)] = part;
        Parts.Add(part);
        OnPropertyChanged(nameof(StatusText));
    }

    internal void ToolCompleted(ToolCallCompleted e)
    {
        if (!_tools.TryGetValue(Key(e.CallId, e.Name), out var part))
        {
            part = new ToolCallPart(e.CallId, e.Name, e.Source, ToolClass.Unknown, null, _previewLength);
            _tools[Key(e.CallId, e.Name)] = part;
            Parts.Add(part);
        }
        part.Complete(e.Outcome, e.Result, e.Duration);
    }

    internal void AddApproval(PendingApproval pending)
    {
        CloseText();
        Parts.Add(new ApprovalPart(pending, _previewLength));
        if (_tools.TryGetValue(Key(pending.Request.CallId, pending.Request.ToolName), out var tool))
        {
            tool.State = ToolCallState.WaitingForApproval;
        }
        State = TurnState.WaitingForApproval;
    }

    internal void ApprovalDecided(ApprovalRequest request, ApprovalDecision decision)
    {
        var part = Parts.OfType<ApprovalPart>().LastOrDefault(p => p.Request.CallId == request.CallId && p.ToolName == request.ToolName);
        if (part is { IsPending: true })
        {
            part.State = decision.Approved ? (decision.ForSession ? ApprovalState.ApprovedForSession : ApprovalState.Approved)
                : decision.Reason == "No answer in time." ? ApprovalState.Expired : ApprovalState.Denied;
        }
        if (_tools.TryGetValue(Key(request.CallId, request.ToolName), out var tool) && tool.State == ToolCallState.WaitingForApproval)
        {
            tool.State = ToolCallState.Running;
        }
        if (_state == TurnState.WaitingForApproval && !Parts.OfType<ApprovalPart>().Any(p => p.IsPending))
        {
            State = TurnState.Running;
        }
    }

    internal void AddNotice(GovernanceNotice notice)
    {
        if (notice.Outcome == "prompt_blocked")
        {
            State = TurnState.Blocked;
        }
        AddPart(NoticePart.From(notice));
    }

    internal void Complete(AgentResult result, DateTimeOffset now)
    {
        Id = result.TurnId;
        Outcome = result.Outcome;
        if (result.Usage?.TotalTokenCount is { } tokens && tokens > 0)
        {
            UsageText = $"{tokens:N0} tokens";
        }
        Finish(result.Outcome == ChatOutcome.PromptBlocked ? TurnState.Blocked : TurnState.Completed, now);
    }

    internal void Stop(DateTimeOffset now) => Finish(TurnState.Stopped, now);

    internal void Fail(string message, DateTimeOffset now)
    {
        ErrorMessage = message;
        Finish(TurnState.Failed, now);
    }

    /// <summary>For a turn read back from the transcript.</summary>
    internal void Restore(TurnState state, ChatOutcome? outcome, string usage)
    {
        _state = state;
        _outcome = outcome;
        _usageText = usage;
    }

    internal void AddRestoredText(string text) => Parts.Add(new MarkdownPart(text));

    internal ToolCallPart AddRestoredTool(string callId, string name, string source, ToolClass toolClass)
    {
        var part = new ToolCallPart(callId, name, source, toolClass, null, _previewLength);
        _tools[Key(callId, name)] = part;
        Parts.Add(part);
        return part;
    }

    internal ToolCallPart? FindTool(string callId, string name) => _tools.TryGetValue(Key(callId, name), out var p) ? p : null;

    private void Finish(TurnState state, DateTimeOffset now)
    {
        CloseText();
        _reasoning?.Complete(now);
        foreach (var approval in Parts.OfType<ApprovalPart>().Where(p => p.IsPending))
        {
            approval.State = ApprovalState.Expired;
        }
        foreach (var tool in _tools.Values.Where(t => t.IsRunning))
        {
            tool.State = ToolCallState.Cancelled;
        }
        var elapsed = now - Timestamp;
        DurationText = elapsed.TotalSeconds >= 1 ? $"{elapsed.TotalSeconds:0.0} s" : "";
        State = state;
        OnPropertyChanged(nameof(AnswerText));
    }

    /// <summary>Text after a tool call starts a new block of text, below it (as Copilot shows it).</summary>
    private void CloseText()
    {
        if (_text is not null)
        {
            _text.IsStreaming = false;
            _text = null;
        }
        if (_reasoning is { IsStreaming: true } r)
        {
            r.Complete(DateTimeOffset.UtcNow);
        }
    }

    private void Copy() => _chat?.Clipboard.SetText(AnswerText);

    private static string Key(string callId, string name) => callId + "\u0001" + name;
}

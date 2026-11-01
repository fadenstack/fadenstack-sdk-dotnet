using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.ViewModels;

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
    private UiStrings _strings;
    private readonly int _previewLength;
    private readonly Dictionary<string, ToolCallPart> _tools = new();
    private MarkdownPart? _text;
    private ReasoningPart? _reasoning;
    private TurnState _state;
    private ChatOutcome? _outcome;
    private string? _error;
    private long? _tokens;
    private TimeSpan? _elapsed;
    private bool _waitingForSlot;

    /// <summary>A model call of the turn ended with nothing (<c>empty_answer</c>): Try again is offered, as for a failed turn.</summary>
    private bool _unanswered;

    internal TurnViewModel(AgentChatViewModel? chat, string userText, DateTimeOffset timestamp, int previewLength, TurnState state = TurnState.Running)
    {
        _chat = chat;
        _strings = chat?.Strings ?? UiStrings.Current;
        UserText = userText;
        Timestamp = timestamp;
        _previewLength = previewLength;
        _state = state;
        CopyCommand = new RelayCommand(Copy, () => AnswerText.Length > 0);
        EditCommand = new RelayCommand(() => _chat?.Edit(UserText), () => _chat is not null && !IsRunning);
        RetryCommand = new AsyncRelayCommand(() => _chat?.SendAsync(UserText) ?? Task.CompletedTask,
            () => _chat is not null && !IsRunning && (_state is TurnState.Failed or TurnState.Stopped || (_state == TurnState.Completed && _unanswered)));
    }

    public string? Id { get; internal set; }

    /// <summary>The chat's texts, for renderers that add parts.</summary>
    public UiStrings Strings => _strings;

    public string UserText { get; }

    /// <summary>What went with the message.</summary>
    public IReadOnlyList<AttachmentChip> Attachments { get; internal set; } = Array.Empty<AttachmentChip>();

    public bool HasAttachments => Attachments.Count > 0;

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

    /// <summary>
    /// The turn's own line, for what no part shows: nothing while tools run or a card waits (the tool's row and
    /// the card say it).
    /// </summary>
    public string StatusText => _state switch
    {
        TurnState.Running when _waitingForSlot => _strings.WaitingForSlot,
        TurnState.Running => _text is null && _tools.Count == 0 ? _strings.Working : "",
        TurnState.Stopped => _strings.Stopped,
        TurnState.Failed => _strings.Failed,
        _ => "",
    };

    public string? ErrorMessage
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string UsageText => _tokens is { } tokens ? _strings.Tokens(tokens) : "";

    public string DurationText => _elapsed is { TotalSeconds: >= 1 } elapsed ? _strings.Duration(elapsed) : "";

    /// <summary>The answer's Markdown, for copying.</summary>
    public string AnswerText => string.Join("\n\n", Parts.OfType<MarkdownPart>().Select(p => p.Text).Where(t => t.Length > 0));

    public IRelayCommand CopyCommand { get; }

    /// <summary>Puts the message back into the composer (e.g. to rephrase a refused prompt).</summary>
    public IRelayCommand EditCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    /// <summary>
    /// A skin calls this for a turn far from the viewport: the answer's parsed blocks go, its text stays. A running turn
    /// keeps them. <see cref="Load"/> brings them back, before the turn is drawn again.
    /// </summary>
    public void Unload()
    {
        if (IsRunning)
        {
            return;
        }
        foreach (var part in Parts.OfType<MarkdownPart>())
        {
            part.Unload();
        }
    }

    public void Load()
    {
        foreach (var part in Parts.OfType<MarkdownPart>())
        {
            part.Load();
        }
    }

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
            CloseReasoning(DateTimeOffset.UtcNow);
            _text = new MarkdownPart(isStreaming: true, clipboard: _chat?.Clipboard);
            Parts.Add(_text);
            OnPropertyChanged(nameof(StatusText));
        }
        _text.Append(text);
    }

    internal void AppendReasoning(string text, DateTimeOffset now)
    {
        if (_reasoning is null)
        {
            _reasoning = new ReasoningPart(now, _strings);
            Parts.Add(_reasoning);
        }
        _reasoning.Append(text);
    }

    internal void ToolStarted(ToolCallStarted e)
    {
        CloseText();
        var part = new ToolCallPart(e.CallId, e.Name, e.Source, e.Class, e.Arguments, _previewLength, _strings);
        _tools[Key(e.CallId, e.Name)] = part;
        Parts.Add(part);
        OnPropertyChanged(nameof(StatusText));
    }

    internal void ToolCompleted(ToolCallCompleted e)
    {
        if (!_tools.TryGetValue(Key(e.CallId, e.Name), out var part))
        {
            part = new ToolCallPart(e.CallId, e.Name, e.Source, ToolClass.Unknown, null, _previewLength, _strings);
            _tools[Key(e.CallId, e.Name)] = part;
            Parts.Add(part);
        }
        part.Complete(e.Outcome, e.Result, e.Duration, e.Pictures.Count > 0 ? e.Pictures[0].Data.ToArray() : null);
    }

    internal void AddApproval(PendingApproval pending)
    {
        CloseText();
        AddApprovalPart(new ApprovalPart(pending, _previewLength, _strings));
        if (_tools.TryGetValue(Key(pending.Request.CallId, pending.Request.ToolName), out var tool))
        {
            tool.State = ToolCallState.WaitingForApproval;
        }
        State = TurnState.WaitingForApproval;
    }

    /// <summary>
    /// The card goes above its tool's row, so the turn reads in order: asked, allowed (or not), ran.
    /// </summary>
    internal void AddApprovalPart(ApprovalPart card)
    {
        if (_tools.TryGetValue(Key(card.Request.CallId, card.ToolName), out var tool) && Parts.IndexOf(tool) is var at and >= 0)
        {
            Parts.Insert(at, card);
        }
        else
        {
            Parts.Add(card);
        }
    }

    internal void ApprovalDecided(ApprovalRequest request, ApprovalDecision decision)
    {
        var part = Parts.OfType<ApprovalPart>().LastOrDefault(p => p.Request.CallId == request.CallId && p.ToolName == request.ToolName);
        if (part is { IsPending: true })
        {
            part.State = decision.Approved
                    ? (decision.Always ? ApprovalState.ApprovedAlways : decision.ForTime ? ApprovalState.ApprovedForTime : decision.ForSession ? ApprovalState.ApprovedForSession : ApprovalState.Approved)
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
        _unanswered |= notice.Outcome == "empty_answer";
        AddPart(NoticePart.From(notice, _strings));
    }

    internal void AddRestoredNotice(GovernanceNotice notice)
    {
        _unanswered |= notice.Outcome == "empty_answer";
        Parts.Add(NoticePart.From(notice, _strings));
    }

    internal void Complete(AgentResult result, DateTimeOffset now)
    {
        Id = result.TurnId;
        Outcome = result.Outcome;
        if (result.Usage?.TotalTokenCount is { } tokens && tokens > 0)
        {
            _tokens = tokens;
            OnPropertyChanged(nameof(UsageText));
        }
        Finish(result.Outcome == ChatOutcome.PromptBlocked ? TurnState.Blocked : TurnState.Completed, now);
    }

    /// <summary>The host's turn gate holds this turn back: its line says so.</summary>
    internal void SetWaitingForSlot(bool waiting)
    {
        if (_waitingForSlot != waiting)
        {
            _waitingForSlot = waiting;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    internal void Stop(DateTimeOffset now) => Finish(TurnState.Stopped, now);

    internal void Fail(string message, DateTimeOffset now)
    {
        ErrorMessage = message;
        Finish(TurnState.Failed, now);
    }

    /// <summary>For a turn read back from the transcript.</summary>
    internal void Restore(TurnState state, ChatOutcome? outcome, long? tokens)
    {
        _state = state;
        _outcome = outcome;
        _tokens = tokens;
    }

    /// <summary>The language changed: the turn and its parts are drawn again in it.</summary>
    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        foreach (var part in Parts)
        {
            part.Retext(strings);
        }
        OnPropertyChanged(string.Empty);
    }

    internal void AddRestoredText(string text) => Parts.Add(new MarkdownPart(text, clipboard: _chat?.Clipboard));

    internal ToolCallPart AddRestoredTool(string callId, string name, string source, ToolClass toolClass)
    {
        var part = new ToolCallPart(callId, name, source, toolClass, null, _previewLength, _strings);
        _tools[Key(callId, name)] = part;
        Parts.Add(part);
        return part;
    }

    internal ToolCallPart? FindTool(string callId, string name) => _tools.TryGetValue(Key(callId, name), out var p) ? p : null;

    private void Finish(TurnState state, DateTimeOffset now)
    {
        CloseReasoning(now);
        CloseText();
        foreach (var approval in Parts.OfType<ApprovalPart>().Where(p => p.IsPending))
        {
            approval.State = ApprovalState.Expired;
        }
        foreach (var tool in _tools.Values.Where(t => t.IsRunning))
        {
            tool.State = ToolCallState.Cancelled;
        }
        _elapsed = now - Timestamp;
        OnPropertyChanged(nameof(DurationText));
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
        CloseReasoning(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Thinking ends where an answer, a tool call or a part starts; thinking after it is a block of its own, below it, so
    /// "Thought for" never counts a tool's run, an approval's wait or the answer's streaming.
    /// </summary>
    private void CloseReasoning(DateTimeOffset now)
    {
        if (_reasoning is { IsStreaming: true } r)
        {
            r.Complete(now);
        }
        _reasoning = null;
    }

    private void Copy() => _chat?.Clipboard.SetText(AnswerText);

    private static string Key(string callId, string name) => callId + "\u0001" + name;
}

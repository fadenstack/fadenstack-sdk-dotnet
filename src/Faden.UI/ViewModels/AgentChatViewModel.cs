using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Faden.Agents;
using Faden.Agents.Sessions;
using Faden.Tools;
using Faden.UI.Services;

namespace Faden.UI.ViewModels;

/// <summary>
/// A chat with one agent, for any skin: the conversation as turns, the composer, the tool-mode picker, the
/// session history. The agent runs on a background thread; its events reach the UI thread in batches, one
/// per <see cref="AgentChatOptions.RenderInterval"/>, however fast it streams. Opened sessions show their
/// latest turns and load older ones on request, so long sessions open fast and stay light.
/// </summary>
public sealed class AgentChatViewModel : ObservableObject, IDisposable
{
    private readonly FadenAgent _agent;
    private readonly ApprovalBroker _approvals;
    private readonly IUiDispatcher _dispatcher;
    private readonly AgentChatOptions _options;
    private readonly RenderBatcher _batcher;
    private readonly List<IAgentEventRenderer> _renderers;
    private readonly object _approvalGate = new();
    private readonly object _reasoningKey = new();
    private readonly Dictionary<string, PendingApproval> _waitingForEvent = new();
    private readonly HashSet<string> _eventsWithoutRequest = new();
    private AgentSession? _session;
    private CancellationTokenSource? _stop;
    private TurnViewModel? _current;
    private List<List<TranscriptEntry>> _history = new();
    private int _loadedFrom;
    private string _title = "";
    private string _greeting = "";
    private string _input = "";
    private bool _isReady;
    private bool _isBusy;
    private bool _isHistoryOpen;
    private bool _isModeMenuOpen;
    private bool _signInRequired;
    private string? _error;
    private ToolModeOption _selectedMode;

    public AgentChatViewModel(FadenAgent agent, ApprovalBroker approvals, IUiDispatcher dispatcher, AgentChatOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? new AgentChatOptions();
        _batcher = new RenderBatcher(dispatcher, _options.RenderInterval);
        _renderers = _options.Renderers.Concat(new IAgentEventRenderer[] { new CitationsRenderer(), new PiiRenderer(), new FallbackRenderer() }).ToList();
        ToolModes = new ObservableCollection<ToolModeOption>(new[] { ToolMode.Off, ToolMode.ReadOnly, ToolMode.Ask, ToolMode.Auto }
            .Select(m => new ToolModeOption(m)));
        _selectedMode = ToolModes[2];
        _approvals.Requested += OnApprovalRequested;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        SendCommand = new AsyncRelayCommand(() => SendAsync(Input), () => CanSend);
        SendSuggestionCommand = new AsyncRelayCommand<string>(s => SendAsync(s ?? ""), _ => _isReady && !_isBusy);
        StopCommand = new RelayCommand(Stop, () => _isBusy);
        NewChatCommand = new RelayCommand(NewChat, () => _isReady && !_isBusy);
        OpenSessionCommand = new AsyncRelayCommand<SessionItemViewModel>(s => s is null ? Task.CompletedTask : OpenSessionAsync(s.Id),
            _ => _isReady && !_isBusy);
        DeleteSessionCommand = new AsyncRelayCommand<SessionItemViewModel>(DeleteSessionAsync, _ => !_isBusy);
        LoadEarlierCommand = new AsyncRelayCommand(LoadEarlierAsync, () => HasEarlierTurns);
        ToggleHistoryCommand = new AsyncRelayCommand(ToggleHistoryAsync);
        RemoveAttachmentCommand = new RelayCommand<AttachmentViewModel>(a =>
        {
            if (a is not null)
            {
                Attachments.Remove(a);
            }
        });
        SelectToolModeCommand = new RelayCommand<ToolModeOption>(option =>
        {
            if (option is { IsAvailable: true })
            {
                SelectedToolMode = option;
            }
            IsModeMenuOpen = false;
        }, option => option is { IsAvailable: true });
        ResetApprovalsCommand = new AsyncRelayCommand(ResetApprovalsAsync, () => _isReady);
    }

    // -- state ---------------------------------------------------------------------------------------

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>The agent's greeting for an empty chat (profile <c>ui.greeting</c>).</summary>
    public string Greeting
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    /// <summary>Prompts to start with (profile <c>ui.suggestions</c>).</summary>
    public ObservableCollection<string> Suggestions { get; } = new();

    public ObservableCollection<TurnViewModel> Turns { get; } = new();

    /// <summary>Context that goes with the next message (chips in the composer).</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = new();

    public bool IsEmpty => Turns.Count == 0;

    /// <summary>Older turns of an opened session that are not shown yet.</summary>
    public bool HasEarlierTurns => _loadedFrom > 0;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();

    public string? CurrentSessionId => _session?.Id;

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value))
            {
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanSend => _isReady && !_isBusy && !string.IsNullOrWhiteSpace(_input);

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>A turn is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                Refresh();
            }
        }
    }

    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set => SetProperty(ref _isHistoryOpen, value);
    }

    /// <summary>The agent needs a sign-in first (the host shows its sign-in, then calls <see cref="InitializeAsync"/>).</summary>
    public bool SignInRequired
    {
        get => _signInRequired;
        private set => SetProperty(ref _signInRequired, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ObservableCollection<ToolModeOption> ToolModes { get; }

    /// <summary>The mode menu is open (presentation state, so the skin needs no code).</summary>
    public bool IsModeMenuOpen
    {
        get => _isModeMenuOpen;
        set => SetProperty(ref _isModeMenuOpen, value);
    }

    /// <summary>The user's pick for this chat. New chats start in Ask (sdk/07).</summary>
    public ToolModeOption SelectedToolMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !value.IsAvailable || !SetProperty(ref _selectedMode, value))
            {
                return;
            }
            if (_session is not null)
            {
                _session.ToolMode = value.Mode;
            }
            OnPropertyChanged(nameof(ToolModeHint));
        }
    }

    /// <summary>What the pick means here, including an EE ceiling.</summary>
    public string ToolModeHint
    {
        get
        {
            var ceiling = _agent.Ceiling;
            var effective = Faden.Tools.ToolModes.Narrowest(_selectedMode.Mode, ceiling);
            return effective == _selectedMode.Mode ? _selectedMode.Description
                : $"{_selectedMode.Description} Limited to {ToolModes.First(o => o.Mode == effective).Label} by your organisation.";
        }
    }

    internal IClipboard Clipboard => _options.Clipboard;

    // -- commands ------------------------------------------------------------------------------------

    public IAsyncRelayCommand InitializeCommand { get; }

    public IAsyncRelayCommand SendCommand { get; }

    public IAsyncRelayCommand<string> SendSuggestionCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand NewChatCommand { get; }

    public IAsyncRelayCommand<SessionItemViewModel> OpenSessionCommand { get; }

    public IAsyncRelayCommand<SessionItemViewModel> DeleteSessionCommand { get; }

    public IAsyncRelayCommand LoadEarlierCommand { get; }

    public IAsyncRelayCommand ToggleHistoryCommand { get; }

    public IRelayCommand<AttachmentViewModel> RemoveAttachmentCommand { get; }

    public IRelayCommand<ToolModeOption> SelectToolModeCommand { get; }

    /// <summary>
    /// "Reset approvals": forgets what the user allowed always for this agent on this device, and what they allowed
    /// for the current chat. The next such call asks again.
    /// </summary>
    public IAsyncRelayCommand ResetApprovalsCommand { get; }

    /// <summary>The skin moves the focus to the composer (after "Edit").</summary>
    public event EventHandler? ComposerFocusRequested;

    // -- behaviour -----------------------------------------------------------------------------------

    /// <summary>Starts the agent (profile, tools) and reads the session list. Call on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        Error = null;
        SignInRequired = false;
        try
        {
            await _agent.StartAsync();
            var profile = _agent.Profile;
            Title = profile.Name;
            Greeting = Ui(profile.Ui, "greeting") is { ValueKind: JsonValueKind.String } g ? g.GetString() ?? "" : $"Ask {profile.Name} anything.";
            Suggestions.Clear();
            if (Ui(profile.Ui, "suggestions") is { ValueKind: JsonValueKind.Array } suggestions)
            {
                foreach (var s in suggestions.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Take(6))
                {
                    Suggestions.Add(s.GetString()!);
                }
            }
            var ceiling = _agent.Ceiling;
            foreach (var option in ToolModes)
            {
                option.IsAvailable = Faden.Tools.ToolModes.Narrowest(option.Mode, ceiling) == option.Mode;
            }
            SelectToolModeCommand.NotifyCanExecuteChanged();
            var start = ToolModes.First(o => o.Mode == _agent.DefaultToolMode);
            SetProperty(ref _selectedMode, start.IsAvailable ? start : ToolModes.Last(o => o.IsAvailable), nameof(SelectedToolMode));
            OnPropertyChanged(nameof(ToolModeHint));
            await RefreshSessionsAsync();
            IsReady = true;
        }
        catch (FadenSignInRequiredException e)
        {
            SignInRequired = true;
            Error = e.Message;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>Sends a message; completes when its turn has ended and been shown.</summary>
    public async Task SendAsync(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || !_isReady || _isBusy)
        {
            return;
        }
        if (text == _input.Trim())
        {
            Input = "";
        }
        var attached = Attachments.Where(a => a.Sends).ToList();
        foreach (var a in attached.Where(a => !a.IsImplicit))
        {
            Attachments.Remove(a);
        }
        var turn = new TurnViewModel(this, text, _options.Clock(), _options.PreviewLength)
        {
            Attachments = attached.Select(a => new AttachmentChip(a.Title, a.Subtitle)).ToList(),
        };
        Turns.Add(turn);
        OnPropertyChanged(nameof(IsEmpty));
        _current = turn;
        _stop = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await Task.Run(() => RunTurnAsync(turn, text, attached, _selectedMode.Mode, _stop.Token));
            await _batcher.WhenFlushedAsync();
        }
        finally
        {
            _current = null;
            _stop.Dispose();
            _stop = null;
            IsBusy = false;
            TouchCurrentSession(text);
        }
    }

    /// <summary>Starts a new chat; the session is created with its first message.</summary>
    public void NewChat()
    {
        if (_isBusy)
        {
            return;
        }
        _session = null;
        _history = new List<List<TranscriptEntry>>();
        _loadedFrom = 0;
        Turns.Clear();
        foreach (var a in Attachments.Where(a => !a.IsImplicit).ToList())
        {
            Attachments.Remove(a);
        }
        SetProperty(ref _selectedMode, ToolModes.FirstOrDefault(o => o.Mode == _agent.DefaultToolMode && o.IsAvailable) ?? ToolModes.Last(o => o.IsAvailable), nameof(SelectedToolMode));
        OnPropertyChanged(nameof(ToolModeHint));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasEarlierTurns));
        OnPropertyChanged(nameof(CurrentSessionId));
        LoadEarlierCommand.NotifyCanExecuteChanged();
        MarkCurrent();
    }

    /// <summary>Opens a session from this device: its latest turns first. Reading and building happen off the UI thread.</summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        if (_isBusy)
        {
            return;
        }
        Error = null;
        try
        {
            var initial = _options.InitialTurns;
            var (session, history, from, turns) = await Task.Run(async () =>
            {
                var opened = await _agent.OpenSessionAsync(sessionId).ConfigureAwait(false);
                var grouped = TranscriptTurns.Group(opened.Transcript);
                var start = Math.Max(0, grouped.Count - initial);
                var built = grouped.Skip(start).Select(t => TranscriptTurns.Build(this, t, _options.PreviewLength)).ToList();
                return (opened, grouped, start, built);
            });
            _session = session;
            _history = history;
            _loadedFrom = from;
            Turns.Clear();
            foreach (var turn in turns)
            {
                Turns.Add(turn);
            }
            var mode = ToolModes.First(o => o.Mode == session.ToolMode);
            SetProperty(ref _selectedMode, mode, nameof(SelectedToolMode));
            OnPropertyChanged(nameof(ToolModeHint));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasEarlierTurns));
            OnPropertyChanged(nameof(CurrentSessionId));
            LoadEarlierCommand.NotifyCanExecuteChanged();
            MarkCurrent();
            IsHistoryOpen = false;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    public async Task RefreshSessionsAsync()
    {
        var now = _options.Clock();
        var sessions = await Task.Run(() => _agent.ListSessionsAsync()); // reads files: not on the UI thread
        Sessions.Clear();
        foreach (var s in sessions)
        {
            Sessions.Add(new SessionItemViewModel(s.Id, s.Title, s.LastActivity, now));
        }
        MarkCurrent();
    }

    internal void Edit(string text)
    {
        Input = text;
        RequestComposerFocus();
    }

    /// <summary>Asks the skin to put the focus in the composer (e.g. after a host attached the selection).</summary>
    public void RequestComposerFocus() => ComposerFocusRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _approvals.Requested -= OnApprovalRequested;
        _stop?.Cancel();
    }

    private void Stop() => _stop?.Cancel();

    private async Task LoadEarlierAsync()
    {
        if (_loadedFrom == 0)
        {
            return;
        }
        var from = Math.Max(0, _loadedFrom - _options.PageSize);
        var page = _history.Skip(from).Take(_loadedFrom - from).ToList();
        var older = await Task.Run(() => page.Select(t => TranscriptTurns.Build(this, t, _options.PreviewLength)).ToList());
        for (var k = 0; k < older.Count; k++)
        {
            Turns.Insert(k, older[k]);
        }
        _loadedFrom = from;
        OnPropertyChanged(nameof(HasEarlierTurns));
        LoadEarlierCommand.NotifyCanExecuteChanged();
    }

    private async Task ResetApprovalsAsync()
    {
        IsModeMenuOpen = false;
        _session?.ResetApprovals();
        try
        {
            await Task.Run(() => _agent.ResetApprovalsAsync()); // writes a file: not on the UI thread
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    private async Task ToggleHistoryAsync()
    {
        IsHistoryOpen = !IsHistoryOpen;
        if (IsHistoryOpen)
        {
            await RefreshSessionsAsync();
        }
    }

    private async Task DeleteSessionAsync(SessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }
        await _agent.DeleteSessionAsync(item.Id);
        Sessions.Remove(item);
        if (item.Id == _session?.Id)
        {
            NewChat();
        }
    }

    // -- the turn, on a background thread -------------------------------------------------------------

    private async Task RunTurnAsync(TurnViewModel turn, string text, IReadOnlyList<AttachmentViewModel> attached, ToolMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var attachments = new List<MessageAttachment>();
            foreach (var a in attached)
            {
                try
                {
                    if (await a.ResolveAsync(cancellationToken).ConfigureAwait(false) is { } resolved)
                    {
                        attachments.Add(resolved);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    var title = a.Title;
                    _batcher.Enqueue(() => turn.AddPart(new Parts.NoticePart(Parts.NoticeSeverity.Warning, $"{title} was not attached", e.Message)));
                }
            }
            if (_session is null)
            {
                _session = await _agent.StartSessionAsync(mode, cancellationToken).ConfigureAwait(false);
                _batcher.Enqueue(() => OnPropertyChanged(nameof(CurrentSessionId)));
            }
            _session.ToolMode = mode;
            await foreach (var e in _session.StreamAsync(text, attachments, cancellationToken).ConfigureAwait(false))
            {
                Apply(turn, e);
            }
        }
        catch (OperationCanceledException)
        {
            _batcher.Enqueue(() => turn.Stop(_options.Clock()));
        }
        catch (FadenSignInRequiredException e)
        {
            _batcher.Enqueue(() =>
            {
                SignInRequired = true;
                turn.Fail(e.Message, _options.Clock());
            });
        }
        catch (Exception e)
        {
            _batcher.Enqueue(() => turn.Fail(e.Message, _options.Clock()));
        }
    }

    private void Apply(TurnViewModel turn, AgentEvent e)
    {
        switch (e)
        {
            case TurnStarted started:
                _batcher.Enqueue(() => turn.Id = started.TurnId);
                break;
            case TextDelta t:
                _batcher.EnqueueText(turn, t.Text, turn.AppendText);
                break;
            case ReasoningDelta r:
                _batcher.EnqueueText(_reasoningKey, r.Text, s => turn.AppendReasoning(s, _options.Clock()));
                break;
            case ToolCallStarted s:
                _batcher.Enqueue(() => turn.ToolStarted(s));
                break;
            case ToolCallCompleted c:
                _batcher.Enqueue(() => turn.ToolCompleted(c));
                break;
            case ApprovalRequested a:
                OnApprovalEvent(turn, a.Request);
                break;
            case ApprovalDecided d:
                _batcher.Enqueue(() => turn.ApprovalDecided(d.Request, d.Decision));
                break;
            case NoticeRaised n:
                _batcher.Enqueue(() => turn.AddNotice(n.Notice));
                break;
            case GatewayEvent g:
                _batcher.Enqueue(() =>
                {
                    foreach (var renderer in _renderers)
                    {
                        if (renderer.TryRender(g.Event, turn))
                        {
                            break;
                        }
                    }
                });
                break;
            case TurnCompleted done:
                _batcher.Enqueue(() => turn.Complete(done.Result, _options.Clock()));
                break;
        }
    }

    // An approval arrives twice: as an event, in order with the turn's other events, and as the broker's
    // request, which carries the answer. The card is added by whichever comes second, so it sits where the
    // event put it.

    private void OnApprovalEvent(TurnViewModel turn, ApprovalRequest request)
    {
        PendingApproval? pending;
        lock (_approvalGate)
        {
            var key = Key(request);
            if (!_waitingForEvent.TryGetValue(key, out pending))
            {
                _eventsWithoutRequest.Add(key);
                return;
            }
            _waitingForEvent.Remove(key);
        }
        _batcher.Enqueue(() => turn.AddApproval(pending));
    }

    private void OnApprovalRequested(PendingApproval pending)
    {
        var turn = _current;
        lock (_approvalGate)
        {
            var key = Key(pending.Request);
            if (!_eventsWithoutRequest.Remove(key))
            {
                _waitingForEvent[key] = pending;
                return;
            }
        }
        if (turn is null)
        {
            pending.Resolve(ApprovalDecision.Deny("The chat is closed."));
            return;
        }
        _batcher.Enqueue(() => turn.AddApproval(pending));
    }

    private static string Key(ApprovalRequest request) => request.CallId + "\u0001" + request.ToolName;

    private void TouchCurrentSession(string text)
    {
        if (_session is null)
        {
            return;
        }
        var now = _options.Clock();
        var item = Sessions.FirstOrDefault(s => s.Id == _session.Id);
        if (item is null)
        {
            item = new SessionItemViewModel(_session.Id, text.Length <= 60 ? text : text.Substring(0, 57) + "...", now, now);
            Sessions.Insert(0, item);
        }
        else
        {
            item.Touch(now, now);
            var index = Sessions.IndexOf(item);
            if (index > 0)
            {
                Sessions.Move(index, 0);
            }
        }
        MarkCurrent();
    }

    private void MarkCurrent()
    {
        foreach (var s in Sessions)
        {
            s.IsCurrent = s.Id == _session?.Id;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        SendSuggestionCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged();
        DeleteSessionCommand.NotifyCanExecuteChanged();
        ResetApprovalsCommand.NotifyCanExecuteChanged();
    }

    private static JsonElement? Ui(Dictionary<string, JsonElement>? ui, string key) =>
        ui is not null && ui.TryGetValue(key, out var v) ? v : null;
}

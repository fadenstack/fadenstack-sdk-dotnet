using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.ViewModels;

/// <summary>One line of Settings → Approvals: a grant the server holds, or a tool allowed always on this device.</summary>
public sealed class StandingApprovalItem
{
    internal StandingApprovalItem(string title, Func<Task> revoke, bool isBypass = false)
    {
        Title = title;
        IsBypass = isBypass;
        RevokeCommand = new AsyncRelayCommand(revoke);
    }

    public string Title { get; }

    /// <summary>A running Bypass (a skin may mark it as such).</summary>
    public bool IsBypass { get; }

    public IAsyncRelayCommand RevokeCommand { get; }
}

/// <summary>
/// What the user let the agent do for longer than one call (sdk/38 §7): the grant cards, the marker while a stretch of
/// work runs (the time left by the server's clock, Stop, and near the end "another 30 min"), the Bypass confirmation and
/// its marker while the chat runs in Bypass (the time left, what is recorded, Stop; never more time; sdk/09 L-58, L-63),
/// and Settings → Approvals with Revoke. The server holds the grants; this shows the agent's copy
/// (<see cref="FadenstackAgent.Grants"/>).
/// </summary>
public sealed class GrantsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long before the end the marker offers to keep going (desktop 37 §5.6).</summary>
    public static readonly TimeSpan EndingSoon = TimeSpan.FromMinutes(2);

    /// <summary>What "keep going" adds, the same bounds otherwise.</summary>
    public static readonly TimeSpan KeepGoingBy = TimeSpan.FromMinutes(30);

    /// <summary>How often the copy is read from the server while a Bypass runs, so a server's end shows (L-63).</summary>
    public static readonly TimeSpan BypassRefresh = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(15);

    private readonly FadenstackAgent _agent;
    private readonly ApprovalBroker _broker;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _stopTurn;
    private readonly Func<AgentSession?> _session;
    private readonly Timer _tick;
    private UiStrings _strings;
    private AgentGrant? _work;
    private string _markerText = "";
    private bool _ending;
    private BypassPart? _bypassCard;
    private AgentGrant? _bypass;
    private string? _bypassChat;
    private string _bypassText = "";
    private string _bypassRecorded = "";
    private bool _bypassEnded;
    private string? _bypassEndedWhy;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    internal GrantsViewModel(FadenstackAgent agent, ApprovalBroker broker, IUiDispatcher dispatcher, UiStrings strings, Action stopTurn,
        Func<AgentSession?>? session = null)
    {
        _agent = agent;
        _broker = broker;
        _dispatcher = dispatcher;
        _strings = strings;
        _stopTurn = stopTurn;
        _session = session ?? (() => null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => HasMarker);
        KeepGoingCommand = new AsyncRelayCommand(KeepGoingAsync, () => _ending && _work is not null);
        StopBypassCommand = new AsyncRelayCommand(StopBypassAsync, () => HasBypass);
        DismissBypassEndedCommand = new RelayCommand(() => SetBypassEnded(false, null));
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        _broker.GrantRequested += OnGrantRequested;
        _agent.Grants.Changed += OnGrantsChanged;
        _tick = new Timer(_ => _dispatcher.Post(OnTimer), null, TickEvery, TickEvery);
    }

    /// <summary>The grant cards waiting for the user, and those answered (with their state).</summary>
    public ObservableCollection<GrantPart> Cards { get; } = new();

    /// <summary>Whether a stretch of work runs: the marker is shown.</summary>
    public bool HasMarker => _work is not null;

    /// <summary>"Working alongside you · 23 min left", or that it is paused.</summary>
    public string MarkerText
    {
        get => _markerText;
        private set => SetProperty(ref _markerText, value);
    }

    /// <summary>Whether the end is near: the marker offers <see cref="KeepGoingCommand"/>.</summary>
    public bool IsEnding
    {
        get => _ending;
        private set
        {
            if (SetProperty(ref _ending, value))
            {
                KeepGoingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StopText => _strings.MarkerStop;

    public string KeepGoingText => _strings.MarkerKeepGoing(_strings.Period(KeepGoingBy));

    /// <summary>Stops everything (desktop 37 §5.6): every grant of this device for the agent, and the turn.</summary>
    public IAsyncRelayCommand StopCommand { get; }

    /// <summary>Extends the running stretch of work by <see cref="KeepGoingBy"/> in one click, as far as the server allows.</summary>
    public IAsyncRelayCommand KeepGoingCommand { get; }

    // -- Bypass ---------------------------------------------------------------------------------------

    /// <summary>
    /// The Bypass confirmation of this chat's conversation: waiting for the person, for the server, or saying that Bypass
    /// is not on (<see cref="BypassPart.IsShown"/>); null when none was asked for.
    /// </summary>
    public BypassPart? BypassCard
    {
        get => _bypassCard;
        private set
        {
            if (SetProperty(ref _bypassCard, value))
            {
                OnPropertyChanged(nameof(HasBypassCard));
            }
        }
    }

    /// <summary>Whether a Bypass confirmation is there (shown or answered).</summary>
    public bool HasBypassCard => _bypassCard is not null;

    /// <summary>Whether this chat runs in Bypass: its marker is shown (L-63).</summary>
    public bool HasBypass => _bypass is not null;

    /// <summary>"Bypass on · 12 min left", by the server's clock.</summary>
    public string BypassText
    {
        get => _bypassText;
        private set => SetProperty(ref _bypassText, value);
    }

    /// <summary>What the server records of each call under the running Bypass, from its grant's evidence level.</summary>
    public string BypassRecordedText
    {
        get => _bypassRecorded;
        private set => SetProperty(ref _bypassRecorded, value);
    }

    public string BypassStopName => _strings.BypassStopName;

    /// <summary>Ends this chat's Bypass (its grant, from the next call on) and the turn; the chat goes back to its mode.</summary>
    public IAsyncRelayCommand StopBypassCommand { get; }

    /// <summary>Whether the chat says that its Bypass ended, and why (<see cref="BypassEndedText"/>).</summary>
    public bool HasBypassEnded => _bypassEnded;

    /// <summary>"Bypass ended: an administrator ended it. Approvals ask again.", from the grant's <c>revoked_by</c>.</summary>
    public string BypassEndedText => _bypassEnded ? _strings.BypassEnded(_bypassEndedWhy) : "";

    public IRelayCommand DismissBypassEndedCommand { get; }

    public string CloseText => _strings.Close;

    /// <summary>Raised on the UI thread when this chat's Bypass ended, whoever ended it: the chat leaves Bypass.</summary>
    internal event EventHandler? BypassEnded;

    // -- Settings → Approvals ---------------------------------------------------------------------------

    /// <summary>Settings → Approvals: the grants the server holds and what was allowed always on this device.</summary>
    public ObservableCollection<StandingApprovalItem> Standing { get; } = new();

    public bool HasStanding => Standing.Count > 0;

    public string StandingTitle => _strings.SettingsApprovals;

    public string StandingEmpty => _strings.SettingsApprovalsEmpty;

    public string RevokeText => _strings.Revoke;

    /// <summary>Reads Settings → Approvals again (when Settings opens).</summary>
    public IAsyncRelayCommand LoadCommand { get; }

    /// <summary>Brings the markers up to date: the time left, paused, ending; a Bypass that ended says why.</summary>
    internal void Tick()
    {
        var work = _agent.Grants.Available ? _agent.Grants.Live.FirstOrDefault(g => g.Kind == GrantKinds.Work) : null;
        var had = HasMarker;
        _work = work;
        if (work is null)
        {
            MarkerText = "";
            IsEnding = false;
        }
        else
        {
            var left = _agent.Grants.Remaining(work);
            MarkerText = work.Status == "paused" ? _strings.MarkerPaused : _strings.MarkerWorking(_strings.Period(left));
            IsEnding = work.Status == "active" && left <= EndingSoon;
        }
        if (had != HasMarker)
        {
            OnPropertyChanged(nameof(HasMarker));
            StopCommand.NotifyCanExecuteChanged();
        }
        KeepGoingCommand.NotifyCanExecuteChanged();
        TickBypass();
    }

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        foreach (var card in Cards)
        {
            card.Retext(strings);
        }
        _bypassCard?.Retext(strings);
        Tick();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Asks for Bypass in <paramref name="session"/>: the confirmation shows in this chat; after the person's click the
    /// card waits for the server and says when it gives no acknowledgement (L-59). True once the server confirmed it;
    /// false when declined, not offered, or not confirmed. An error with no card to say it on is returned.
    /// </summary>
    internal async Task<(bool On, Exception? Unsaid)> StartBypassAsync(AgentSession session, CancellationToken cancellationToken)
    {
        BypassCard = null;
        SetBypassEnded(false, null);
        try
        {
            var grant = await session.StartBypassAsync(null, cancellationToken).ConfigureAwait(true);
            if (grant is null)
            {
                _bypassCard?.Withdrawn();
                return (false, null);
            }
            _bypassCard?.Confirmed();
            Tick();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            _bypassCard?.Withdrawn();
            return (false, null);
        }
        catch (Exception e)
        {
            if (_bypassCard is { } card && card.State == BypassCardState.Confirming)
            {
                card.NotConfirmed(e);
                return (false, null);
            }
            return (false, e);
        }
    }

    public void Dispose()
    {
        _tick.Dispose();
        _broker.GrantRequested -= OnGrantRequested;
        _agent.Grants.Changed -= OnGrantsChanged;
    }

    private void TickBypass()
    {
        var session = _session();
        var bypass = session?.Bypass;
        if (session?.Id != _bypassChat)
        {
            // Another chat: what the last one showed is not this one's.
            _bypass = null;
            _bypassChat = session?.Id;
            SetBypassEnded(false, null);
            if (_bypassCard is { } card && card.ConversationId != _bypassChat)
            {
                BypassCard = null;
            }
        }
        var ended = bypass is null ? _bypass : null;
        var started = bypass is not null && _bypass is null;
        var had = HasBypass;
        _bypass = bypass;
        BypassText = bypass is null ? "" : _strings.BypassMarker(_strings.Period(_agent.Grants.Remaining(bypass)));
        BypassRecordedText = bypass is null ? "" : _strings.BypassRecorded(bypass.EvidenceLevel);
        if (had != HasBypass)
        {
            OnPropertyChanged(nameof(HasBypass));
            StopBypassCommand.NotifyCanExecuteChanged();
        }
        if (ended is not null)
        {
            _ = SayWhyEndedAsync(ended.Id); // the server's end, a Stop, a Revoke in Settings: the marker goes and the chat says why
        }
        else if (started)
        {
            SetBypassEnded(false, null);
        }
    }

    private async Task SayWhyEndedAsync(string grantId)
    {
        var chat = _bypassChat;
        SetBypassEnded(true, null);
        BypassEnded?.Invoke(this, EventArgs.Empty);
        string? why;
        try
        {
            why = await _agent.Grants.WhyEndedAsync(grantId).ConfigureAwait(true);
        }
        catch (Exception)
        {
            why = null;
        }
        if (_bypassChat == chat && _bypassEnded && _bypass is null)
        {
            SetBypassEnded(true, why);
        }
    }

    private void SetBypassEnded(bool ended, string? why)
    {
        var changed = ended != _bypassEnded || why != _bypassEndedWhy;
        (_bypassEnded, _bypassEndedWhy) = (ended, why);
        if (changed)
        {
            OnPropertyChanged(nameof(HasBypassEnded));
            OnPropertyChanged(nameof(BypassEndedText));
        }
    }

    /// <summary>The tick: the markers; while a Bypass runs, the copy from the server now and then, so its end shows.</summary>
    private void OnTimer()
    {
        Tick();
        if (HasBypass && DateTimeOffset.UtcNow - _lastRefresh >= BypassRefresh)
        {
            _lastRefresh = DateTimeOffset.UtcNow;
            _ = RefreshQuietlyAsync();
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await _agent.Grants.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // offline: the marker counts on by the server's clock; the next read says more
        }
    }

    private async Task StopAsync()
    {
        _stopTurn();
        await _agent.Grants.RevokeAllAsync().ConfigureAwait(true);
    }

    private async Task StopBypassAsync()
    {
        _stopTurn();
        if (_session() is { } session)
        {
            await session.StopBypassAsync().ConfigureAwait(true);
        }
        else if (_bypass is { } bypass)
        {
            await _agent.Grants.RevokeAsync(bypass.Id).ConfigureAwait(true);
        }
    }

    private async Task KeepGoingAsync()
    {
        if (_work is not { } work)
        {
            return;
        }
        // The marker's button is the card for this: the same bounds, only more time (desktop 37 §5.6).
        await _agent.Grants.ExtendAsync(work.Id, KeepGoingBy, shownText: MarkerText + "\n" + KeepGoingText).ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var grants = _agent.Grants.Available ? _agent.Grants.Live : Array.Empty<AgentGrant>();
        var always = await _agent.ListApprovalsAsync().ConfigureAwait(true);
        Standing.Clear();
        foreach (var grant in grants)
        {
            var left = _strings.Period(_agent.Grants.Remaining(grant));
            var title = grant.Kind switch
            {
                GrantKinds.Work => _strings.SettingsGrantWork(
                    string.Join(", ", grant.Scope.Targets?.SelectMany(t => t.Identifiers).Select(Short) ?? Array.Empty<string>()), left),
                GrantKinds.Bypass => _strings.SettingsGrantBypass(left, _strings.BypassRecorded(grant.EvidenceLevel)),
                _ => _strings.SettingsGrantTime(string.Join(", ", grant.Scope.Tools ?? new List<string>()), left),
            };
            Standing.Add(new StandingApprovalItem(title, async () =>
            {
                await _agent.Grants.RevokeAsync(grant.Id).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            }, grant.Kind == GrantKinds.Bypass));
        }
        foreach (var approval in always)
        {
            Standing.Add(new StandingApprovalItem(_strings.SettingsAlways(approval.Tool), async () =>
            {
                await _agent.ResetApprovalsAsync(approval.Tool).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            }));
        }
        OnPropertyChanged(nameof(HasStanding));
    }

    /// <summary>A place as Settings lists it: a file's name, else as given.</summary>
    private static string Short(string identifier)
    {
        var name = Path.GetFileName(identifier.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? identifier : name;
    }

    private void OnGrantRequested(PendingGrant pending)
    {
        if (pending.Offer.Bypass)
        {
            // Only the chat of its conversation draws the confirmation; it says no more than the offer (L-58).
            if (pending.Offer.ConversationId is { } conversation && conversation != _session()?.Id)
            {
                return;
            }
            pending.Claim();
            _dispatcher.Post(() => BypassCard = new BypassPart(pending, _strings));
            return;
        }
        _dispatcher.Post(() => Cards.Add(new GrantPart(pending, _strings)));
    }

    private void OnGrantsChanged(object? sender, EventArgs e) => _dispatcher.Post(Tick);
}

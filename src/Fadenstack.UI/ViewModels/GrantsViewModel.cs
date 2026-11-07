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
    internal StandingApprovalItem(string title, Func<Task> revoke)
    {
        Title = title;
        RevokeCommand = new AsyncRelayCommand(revoke);
    }

    public string Title { get; }

    public IAsyncRelayCommand RevokeCommand { get; }
}

/// <summary>
/// What the user let the agent do for longer than one call (sdk/38 §7): the grant cards, the marker while a stretch of
/// work runs (the time left by the server's clock, Stop, and near the end "another 30 min"), and Settings → Approvals with
/// Revoke. The server holds the grants; this shows the agent's copy (<see cref="FadenstackAgent.Grants"/>).
/// </summary>
public sealed class GrantsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long before the end the marker offers to keep going (desktop 37 §5.6).</summary>
    public static readonly TimeSpan EndingSoon = TimeSpan.FromMinutes(2);

    /// <summary>What "keep going" adds, the same bounds otherwise.</summary>
    public static readonly TimeSpan KeepGoingBy = TimeSpan.FromMinutes(30);

    private readonly FadenstackAgent _agent;
    private readonly ApprovalBroker _broker;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _stopTurn;
    private readonly Timer _tick;
    private UiStrings _strings;
    private AgentGrant? _work;
    private string _markerText = "";
    private bool _ending;

    internal GrantsViewModel(FadenstackAgent agent, ApprovalBroker broker, IUiDispatcher dispatcher, UiStrings strings, Action stopTurn)
    {
        _agent = agent;
        _broker = broker;
        _dispatcher = dispatcher;
        _strings = strings;
        _stopTurn = stopTurn;
        StopCommand = new AsyncRelayCommand(StopAsync, () => HasMarker);
        KeepGoingCommand = new AsyncRelayCommand(KeepGoingAsync, () => _ending && _work is not null);
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        _broker.GrantRequested += OnGrantRequested;
        _agent.Grants.Changed += OnGrantsChanged;
        _tick = new Timer(_ => _dispatcher.Post(Tick), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
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

    /// <summary>Settings → Approvals: the grants the server holds and what was allowed always on this device.</summary>
    public ObservableCollection<StandingApprovalItem> Standing { get; } = new();

    public bool HasStanding => Standing.Count > 0;

    public string StandingTitle => _strings.SettingsApprovals;

    public string StandingEmpty => _strings.SettingsApprovalsEmpty;

    public string RevokeText => _strings.Revoke;

    /// <summary>Reads Settings → Approvals again (when Settings opens).</summary>
    public IAsyncRelayCommand LoadCommand { get; }

    /// <summary>Brings the marker up to date: the time left, paused, ending.</summary>
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
    }

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        foreach (var card in Cards)
        {
            card.Retext(strings);
        }
        Tick();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        _tick.Dispose();
        _broker.GrantRequested -= OnGrantRequested;
        _agent.Grants.Changed -= OnGrantsChanged;
    }

    private async Task StopAsync()
    {
        _stopTurn();
        await _agent.Grants.RevokeAllAsync().ConfigureAwait(true);
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
            var title = grant.Kind == GrantKinds.Work
                ? _strings.SettingsGrantWork(string.Join(", ", grant.Scope.Targets?.SelectMany(t => t.Identifiers).Select(Short) ?? Array.Empty<string>()), left)
                : _strings.SettingsGrantTime(string.Join(", ", grant.Scope.Tools ?? new List<string>()), left);
            Standing.Add(new StandingApprovalItem(title, async () =>
            {
                await _agent.Grants.RevokeAsync(grant.Id).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            }));
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

    private void OnGrantRequested(PendingGrant pending) => _dispatcher.Post(() => Cards.Add(new GrantPart(pending, _strings)));

    private void OnGrantsChanged(object? sender, EventArgs e) => _dispatcher.Post(Tick);
}

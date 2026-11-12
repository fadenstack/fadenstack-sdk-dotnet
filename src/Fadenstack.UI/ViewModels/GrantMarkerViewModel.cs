using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.ViewModels;

/// <summary>One running grant as the device-wide marker lists it.</summary>
/// <param name="GrantId">The grant's id.</param>
/// <param name="Kind">The grant's kind (<see cref="GrantKinds"/>).</param>
/// <param name="ConversationId">The conversation it belongs to; null when it has none.</param>
/// <param name="Text">The line: kind, chat, time left or paused ("Bypass · Order form · 12 min left").</param>
/// <param name="Left">The time left by the server's clock ("12 min").</param>
/// <param name="IsPaused">The grant is paused (the screen is locked, or the app was closed).</param>
/// <param name="IsEnding">The grant is near its end and can be extended: "another 30 min" is offered.</param>
public sealed record GrantMarkerEntry(string GrantId, string Kind, string? ConversationId, string Text, string Left, bool IsPaused, bool IsEnding);

/// <summary>What a grant that ended leaves: one summary per grant, saying how long it ran and why it ended.</summary>
public sealed class GrantSummary : ObservableObject
{
    internal GrantSummary(string grantId, string kind, GrantEndReason reason, string title, string detail, Action<GrantSummary> dismiss)
    {
        GrantId = grantId;
        Kind = kind;
        Reason = reason;
        Title = title;
        Detail = detail;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }

    public string GrantId { get; }

    public string Kind { get; }

    public GrantEndReason Reason { get; }

    /// <summary>"Worked alongside you for 23 min", "Bypass ran for 23 min".</summary>
    public string Title { get; }

    /// <summary>Where it worked, how many calls ran without asking, and why it ended, one per line.</summary>
    public string Detail { get; }

    public IRelayCommand DismissCommand { get; }
}

/// <summary>
/// The marker for every grant this device holds for the agent, across chats (sdk/46 G2): a stretch of work, Bypass, and
/// what the app adds. It shows the time left by the server's clock, <b>Stop</b> (every grant of the device, and the running
/// turns of their conversations), near the end "another 30 min" once, and one summary per grant when it ends, saying why.
/// The rules -- pausing at the app's end and at the lock, ending after 30 minutes -- are
/// <see cref="GrantLifecycle"/>'s; this shows them. A chat's own marker (<see cref="GrantsViewModel"/>) stays for the chat;
/// this is the one a host shows outside it: a title bar's chip, an avatar's badge, a tray's menu. No text is its own: every
/// text is in <c>UiText.resx</c>. Used on the UI thread; the lifecycle's events come through the dispatcher.
/// </summary>
public sealed class GrantMarkerViewModel : ObservableObject, IDisposable
{
    private readonly GrantLifecycle _lifecycle;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string, string?> _chatTitle;
    private readonly Action<string> _stopTurn;
    private readonly Func<string, string, string?> _placeLabel;
    private readonly List<GrantSummary> _deferred = new();
    private UiStrings _strings;
    private IReadOnlyList<GrantMarkerEntry> _entries = Array.Empty<GrantMarkerEntry>();
    private string _markerText = "";
    private string _badgeText = "";
    private string _problem = "";
    private bool _isOn;
    private bool _isPaused;
    private bool _isEnding;
    private bool _disposed;

    /// <param name="lifecycle">The rules (<see cref="AgentGrants"/> over the agent's grants).</param>
    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="strings">The texts, in the UI language (<see cref="Retext"/> follows a change).</param>
    /// <param name="chatTitle">The title of the chat of a conversation id; null or empty: "A chat".</param>
    /// <param name="stopTurn">Stops the running turn of a conversation (by id) when Stop ends its grant; none: only the grants.</param>
    /// <param name="placeLabel">The label of a place (type, identifier) for a summary; none or null: a file's name, else as given.</param>
    public GrantMarkerViewModel(GrantLifecycle lifecycle, IUiDispatcher dispatcher, UiStrings strings,
        Func<string, string?>? chatTitle = null, Action<string>? stopTurn = null, Func<string, string, string?>? placeLabel = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _chatTitle = chatTitle ?? (_ => null);
        _stopTurn = stopTurn ?? (_ => { });
        _placeLabel = placeLabel ?? ((_, _) => null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsOn);
        KeepGoingCommand = new AsyncRelayCommand<string?>(KeepGoingAsync, _ => IsEnding);
        DismissMessageCommand = new RelayCommand(() => Problem = "");
        _lifecycle.Changed += OnChanged;
        _lifecycle.Ended += OnEnded;
        _lifecycle.EndingSoon += OnChanged;
        Refresh();
    }

    /// <summary>A grant of this device runs (or waits, paused): the marker shows.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (SetProperty(ref _isOn, value))
            {
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The grant ending first is paused.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    /// <summary>A grant is near its end and can be extended: <see cref="KeepGoingCommand"/> is offered.</summary>
    public bool IsEnding
    {
        get => _isEnding;
        private set
        {
            if (SetProperty(ref _isEnding, value))
            {
                KeepGoingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// "Working alongside you · 23 min left", "Bypass on · 12 min left", or with several grants "2 grants running · first ends
    /// in 12 min"; empty while none runs.
    /// </summary>
    public string MarkerText
    {
        get => _markerText;
        private set => SetProperty(ref _markerText, value);
    }

    /// <summary>The time left of the grant ending first ("23 min"), or "Paused"; empty while none runs: an avatar's badge.</summary>
    public string BadgeText
    {
        get => _badgeText;
        private set => SetProperty(ref _badgeText, value);
    }

    /// <summary>Every running grant, the one ending first first: a menu's lines.</summary>
    public IReadOnlyList<GrantMarkerEntry> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    /// <summary>
    /// What went wrong that the person should know -- Stop did not reach the server, more time was refused -- or that a paused
    /// grant waits for the server; empty when nothing.
    /// </summary>
    public string Message => _problem.Length > 0 ? _problem : _lifecycle.Waiting > 0 ? _strings.GrantsWaiting : "";

    public bool HasMessage => Message.Length > 0;

    private string Problem
    {
        get => _problem;
        set
        {
            if (SetProperty(ref _problem, value, nameof(Problem)))
            {
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>One summary per grant that ended, newest last; the host shows them (a notice) and the person dismisses each.</summary>
    public ObservableCollection<GrantSummary> Summaries { get; } = new();

    /// <summary>"Stop": a chip's button and a menu item.</summary>
    public string StopText => _strings.MarkerStop;

    /// <summary>"Another 30 min": offered near the end.</summary>
    public string KeepGoingText => _strings.MarkerKeepGoing(_strings.Period(GrantLifecycle.KeepGoingBy));

    /// <summary>
    /// Stops everything: the running turns of the grants' conversations, then every grant of this device for the agent, a
    /// paused one too. Never more time.
    /// </summary>
    public IAsyncRelayCommand StopCommand { get; }

    /// <summary>
    /// Adds another <see cref="GrantLifecycle.KeepGoingBy"/> to the grant given as the parameter, or to each that is near its
    /// end; the server cuts it at the longest grant (the message says so). Bypass is never offered it.
    /// </summary>
    public IAsyncRelayCommand<string?> KeepGoingCommand { get; }

    public IRelayCommand DismissMessageCommand { get; }

    /// <summary>The UI language changed: the marker, the lines and the texts read the new language.</summary>
    public void Retext(UiStrings strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        Refresh();
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(KeepGoingText));
        OnPropertyChanged(nameof(Message));
    }

    public async Task StopAsync()
    {
        foreach (var grant in _lifecycle.Running)
        {
            if (grant.ConversationId is { } conversation)
            {
                _stopTurn(conversation);
            }
        }
        Problem = "";
        var result = await _lifecycle.StopAsync().ConfigureAwait(true);
        if (!result.Complete)
        {
            Problem = _strings.GrantsStopFailed;
        }
        Refresh();
    }

    public async Task KeepGoingAsync(string? grantId)
    {
        var targets = _lifecycle.Running.Where(g => grantId is null ? IsNearEnd(g) : g.Id == grantId).Select(g => g.Id).ToList();
        foreach (var id in targets)
        {
            try
            {
                await _lifecycle.KeepGoingAsync(id, MarkerText + "\n" + KeepGoingText).ConfigureAwait(true);
                Problem = "";
            }
            catch (FadenstackApiException e) when (e.Code == AtLongestCode)
            {
                Problem = _strings.GrantsAtLongest;
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException or InvalidOperationException)
            {
                Problem = _strings.GrantsExtendFailed;
            }
        }
        Refresh();
    }

    /// <summary>The server's code when an extension would pass the longest grant from its start.</summary>
    public const string AtLongestCode = "grant_at_longest";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifecycle.Changed -= OnChanged;
        _lifecycle.Ended -= OnEnded;
        _lifecycle.EndingSoon -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    private bool IsNearEnd(AgentGrant grant) =>
        grant.Kind != GrantKinds.Bypass && grant.Status == "active" && _lifecycle.Grants.Remaining(grant) is var left
        && left > TimeSpan.Zero && left <= GrantLifecycle.EndingSoonBefore;

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        var running = _lifecycle.Running;
        var first = running.FirstOrDefault();
        if (first is null)
        {
            (MarkerText, BadgeText, IsPaused, IsEnding) = ("", "", false, false);
            Entries = Array.Empty<GrantMarkerEntry>();
            IsOn = false;
        }
        else
        {
            var left = _strings.Period(_lifecycle.Grants.Remaining(first));
            IsPaused = first.Status == "paused";
            MarkerText = running.Count > 1 ? _strings.GrantsMarkerMany(running.Count, left)
                : first.Kind == GrantKinds.Bypass ? (IsPaused ? _strings.GrantsBypassPaused : _strings.BypassMarker(left))
                : IsPaused ? _strings.MarkerPaused : _strings.MarkerWorking(left);
            BadgeText = IsPaused ? _strings.GrantsBadgePaused : left;
            Entries = running.Select(Entry).ToList();
            IsEnding = running.Any(IsNearEnd);
            IsOn = true;
        }
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
        if (!_lifecycle.IsLocked && _deferred.Count > 0)
        {
            // The person is back: the summaries that waited for them show.
            foreach (var summary in _deferred)
            {
                Summaries.Add(summary);
            }
            _deferred.Clear();
        }
    }

    private GrantMarkerEntry Entry(AgentGrant grant)
    {
        var left = _strings.Period(_lifecycle.Grants.Remaining(grant));
        var paused = grant.Status == "paused";
        var chat = grant.ConversationId is { } id && _chatTitle(id) is { Length: > 0 } title ? title : _strings.GrantsChatUnknown;
        var text = string.Join(" · ", _strings.GrantsKind(grant.Kind), chat, paused ? _strings.GrantsPaused : _strings.GrantsLeft(left));
        return new GrantMarkerEntry(grant.Id, grant.Kind, grant.ConversationId, text, left, paused, IsNearEnd(grant));
    }

    private void OnEnded(object? sender, GrantEndedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }
        var grant = e.Grant;
        var period = _strings.Period(e.Ran);
        var places = (grant.Scope.Targets ?? new List<GrantTarget>())
            .SelectMany(t => t.Identifiers.Select(i => _placeLabel(t.Type, i) ?? Short(i))).Distinct().ToList();
        var lines = new List<string>();
        if (places.Count > 0)
        {
            lines.Add(_strings.GrantsSummaryPlaces(string.Join(" · ", places)));
        }
        if (grant.Kind != GrantKinds.Bypass || e.CallsUsed > 0)
        {
            lines.Add(_strings.GrantsSummaryCalls(e.CallsUsed));
        }
        lines.Add(_strings.GrantsEnded(e.Reason));
        var summary = new GrantSummary(grant.Id, grant.Kind, e.Reason,
            grant.Kind == GrantKinds.Bypass ? _strings.GrantsSummaryBypass(period) : _strings.GrantsSummaryWork(period),
            string.Join("\n", lines), s => Summaries.Remove(s));
        // Ended after the lock: the summary waits until the person is back.
        if (e.Reason == GrantEndReason.Locked && _lifecycle.IsLocked)
        {
            _deferred.Add(summary);
        }
        else
        {
            Summaries.Add(summary);
        }
        Refresh();
    });

    private static string Short(string identifier) => PlaceNames.Short(identifier);
}

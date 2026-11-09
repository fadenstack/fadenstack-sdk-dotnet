using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Parts;

public enum BypassCardState
{
    /// <summary>Waiting for the person's click.</summary>
    Pending,

    /// <summary>Allowed; the server has not answered yet.</summary>
    Confirming,

    /// <summary>The server confirmed it: the chat runs in Bypass.</summary>
    On,

    /// <summary>No acknowledgement from the server (offline, refused, switched off): no Bypass (sdk/09 L-59).</summary>
    NotConfirmed,

    /// <summary>The person said not now.</summary>
    Declined,
}

/// <summary>
/// The Bypass confirmation (sdk/09 L-58, sdk/40 A3.3): what will no longer be asked in this chat, what still asks, what
/// the server records of each call (from the profile's <c>approvals.bypass.evidence</c>), for how long -- the shortest
/// time first, no extension -- and an optional reason that administrators see. Drawn from the offer, never from the
/// model's words.
/// <para>
/// Deliberate: only the person's click on <see cref="AllowCommand"/> answers it. Allow is not the default button, Enter
/// and Esc never switch Bypass on, nothing answers it on a timer. After the click the card waits for the server: Bypass
/// holds only once the server confirmed it (<see cref="Confirmed"/>); without that the card says so
/// (<see cref="NotConfirmed"/>) and the chat stays as it was (L-59).
/// </para>
/// </summary>
public sealed class BypassPart : ObservableObject
{
    private readonly PendingGrant _pending;
    private UiStrings _strings;
    private DurationOption? _duration;
    private BypassCardState _state;
    private bool _durationMenuOpen;
    private bool _closed;
    private string _reason = "";
    private string? _failure;

    internal BypassPart(PendingGrant pending, UiStrings strings)
    {
        _pending = pending;
        _strings = strings;
        _duration = Durations.FirstOrDefault(d => d.Value == pending.Offer.Preselected) ?? Durations.FirstOrDefault();
        AllowCommand = new RelayCommand(Allow, () => IsPending && _duration is not null);
        NotNowCommand = new RelayCommand(() => Answer(GrantAnswer.NotNow, BypassCardState.Declined), () => IsPending);
        CloseCommand = new RelayCommand(() =>
        {
            _closed = true;
            OnPropertyChanged(nameof(IsShown));
            CloseCommand!.NotifyCanExecuteChanged();
        }, () => _state == BypassCardState.NotConfirmed && !_closed);
        PickDurationCommand = new RelayCommand<DurationOption>(d =>
        {
            SelectedDuration = d;
            IsDurationMenuOpen = false;
        }, d => IsPending && d is not null);
    }

    public GrantOffer Offer => _pending.Offer;

    /// <summary>The conversation the card is for (<see cref="GrantOffer.ConversationId"/>).</summary>
    public string? ConversationId => Offer.ConversationId;

    public string Title => _strings.BypassTitle;

    public string NoLongerAsksText => _strings.BypassNoLongerAsks;

    public string StillAsksText => _strings.BypassStillAsks;

    /// <summary>What the server records of each call, as the profile says it (sdk/40 A4 decision 6).</summary>
    public string RecordsText => _strings.BypassRecords(Offer.Evidence);

    public string DurationLabel => _strings.GrantDuration;

    /// <summary>The times to pick from: the offer's, up to the longest the server allows.</summary>
    public IReadOnlyList<DurationOption> Durations => Offer.Durations.Select(d => new DurationOption(d, _strings.Period(d))).ToList();

    public DurationOption? SelectedDuration
    {
        get => _duration;
        set
        {
            if (IsPending && value is not null && Offer.Durations.Contains(value.Value))
            {
                SetProperty(ref _duration, value);
            }
        }
    }

    /// <summary>Whether the times to pick from are open.</summary>
    public bool IsDurationMenuOpen
    {
        get => _durationMenuOpen;
        set => SetProperty(ref _durationMenuOpen, value && IsPending);
    }

    public IRelayCommand<DurationOption> PickDurationCommand { get; }

    public string NoExtensionText => _strings.BypassNoExtension;

    public string ReasonLabel => _strings.BypassReason;

    public string ReasonSeenText => _strings.BypassReasonSeen;

    /// <summary>The person's own words on why (optional), at most <see cref="ReasonLength"/> characters.</summary>
    public string Reason
    {
        get => _reason;
        set
        {
            if (!IsPending)
            {
                return;
            }
            var text = value ?? "";
            SetProperty(ref _reason, text.Length > ReasonLength ? text.Substring(0, ReasonLength) : text);
        }
    }

    public int ReasonLength => GrantAnswer.ReasonLength;

    /// <summary>The app's own reason for offering it, if it gave one (its text, never the model's).</summary>
    public string AppReason => Offer.Reason ?? "";

    public string AllowText => _strings.BypassAllow;

    public string NotNowText => _strings.GrantNotNow;

    public string CloseText => _strings.Close;

    public BypassCardState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsShown));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsNotConfirmed));
                AllowCommand.NotifyCanExecuteChanged();
                NotNowCommand.NotifyCanExecuteChanged();
                PickDurationCommand.NotifyCanExecuteChanged();
                CloseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPending => _state == BypassCardState.Pending;

    public bool IsNotConfirmed => _state == BypassCardState.NotConfirmed;

    /// <summary>Whether the card shows: waiting for the person or the server, or saying that Bypass is not on until closed.</summary>
    public bool IsShown => _state is BypassCardState.Pending or BypassCardState.Confirming
        || (_state == BypassCardState.NotConfirmed && !_closed);

    public string StateText => _state switch
    {
        BypassCardState.Confirming => _strings.BypassConfirming,
        BypassCardState.On => _strings.BypassOn,
        BypassCardState.NotConfirmed => _strings.BypassNotConfirmed(_failure),
        BypassCardState.Declined => _strings.GrantDeclined,
        _ => "",
    };

    /// <summary>Switches Bypass on for the picked time: the person's click, and only that.</summary>
    public IRelayCommand AllowCommand { get; }

    public IRelayCommand NotNowCommand { get; }

    /// <summary>Takes away the card that says Bypass is not on.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>The card's text as the person sees it now: its SHA-256 goes to the server as evidence.</summary>
    public string ShownText => string.Join("\n", new[]
    {
        Title, AppReason, NoLongerAsksText, StillAsksText, RecordsText, DurationLabel + " " + (_duration?.Label ?? ""),
        NoExtensionText, ReasonSeenText,
    }.Where(line => line.Length > 0));

    /// <summary>The server confirmed it.</summary>
    internal void Confirmed()
    {
        if (_state == BypassCardState.Confirming)
        {
            State = BypassCardState.On;
        }
    }

    /// <summary>The server gave no acknowledgement: the card says why, as far as it can tell, and stays until closed.</summary>
    internal void NotConfirmed(Exception? error)
    {
        _failure = error switch
        {
            FadenstackApiException { Code: "grant_limits_unavailable" } => "unavailable",
            FadenstackApiException { Code: "bypass_not_allowed" or "grant_not_offered" } or FadenstackPermissionException => "refused",
            FadenstackApiException => null,
            HttpRequestException or TaskCanceledException or FadenstackException => "offline",
            _ => null,
        };
        State = BypassCardState.NotConfirmed;
    }

    /// <summary>Nothing came of it (the offer was withdrawn, the turn stopped, the chat left): the card goes.</summary>
    internal void Withdrawn()
    {
        if (_state is BypassCardState.Pending or BypassCardState.Confirming)
        {
            State = BypassCardState.Declined;
        }
    }

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        var picked = _duration?.Value;
        _duration = Durations.FirstOrDefault(d => d.Value == picked) ?? _duration;
        OnPropertyChanged(string.Empty);
    }

    private void Allow()
    {
        var reason = _reason.Trim();
        Answer(new GrantAnswer
        {
            Allowed = true, Duration = _duration!.Value, ShownText = ShownText, Reason = reason.Length > 0 ? reason : null,
        }, BypassCardState.Confirming);
    }

    private void Answer(GrantAnswer answer, BypassCardState state)
    {
        IsDurationMenuOpen = false;
        if (_pending.Resolve(answer))
        {
            State = state;
        }
    }
}

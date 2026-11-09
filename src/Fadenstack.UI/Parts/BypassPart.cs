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

    /// <summary>Allowed; the second sign-in runs in the browser (sdk/41): the app waits until the person approves it there.</summary>
    SigningIn,

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
/// holds only once the server confirmed it (<see cref="Confirmed"/>), and that needs a second sign-in in the browser
/// (<see cref="BypassCardState.SigningIn"/>; sdk/41), which the app opens and the person approves there; without that
/// the card says so (<see cref="NotConfirmed"/>) and the chat stays as it was (L-59).
/// </para>
/// </summary>
public sealed class BypassPart : ObservableObject
{
    private readonly PendingGrant _pending;
    private readonly Func<Uri, bool> _openBrowser;
    private UiStrings _strings;
    private StepUpRequest? _request;
    private DurationOption? _duration;
    private BypassCardState _state;
    private bool _durationMenuOpen;
    private bool _closed;
    private string _reason = "";
    private string? _failure;

    internal BypassPart(PendingGrant pending, UiStrings strings, Func<Uri, bool>? openBrowser = null)
    {
        _pending = pending;
        _openBrowser = openBrowser ?? SystemBrowser.Open;
        _strings = strings;
        _duration = Durations.FirstOrDefault(d => d.Value == pending.Offer.Preselected) ?? Durations.FirstOrDefault();
        AllowCommand = new RelayCommand(Allow, () => IsPending && _duration is not null);
        NotNowCommand = new RelayCommand(() => Answer(GrantAnswer.NotNow, BypassCardState.Declined), () => IsPending);
        OpenAgainCommand = new RelayCommand(OpenAgain, () => _state == BypassCardState.SigningIn && _request is not null);
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
            SetProperty(ref _reason, GrantAnswer.Cut(text) ?? "");
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
                OnPropertyChanged(nameof(IsSigningIn));
                OpenAgainCommand.NotifyCanExecuteChanged();
                AllowCommand.NotifyCanExecuteChanged();
                NotNowCommand.NotifyCanExecuteChanged();
                PickDurationCommand.NotifyCanExecuteChanged();
                CloseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPending => _state == BypassCardState.Pending;

    public bool IsNotConfirmed => _state == BypassCardState.NotConfirmed;

    /// <summary>Whether the second sign-in runs in the browser: the card shows the code and <see cref="OpenAgainCommand"/>.</summary>
    public bool IsSigningIn => _state == BypassCardState.SigningIn;

    /// <summary>Whether the card shows: waiting for the person or the server, or saying that Bypass is not on until closed.</summary>
    public bool IsShown => _state is BypassCardState.Pending or BypassCardState.Confirming or BypassCardState.SigningIn
        || (_state == BypassCardState.NotConfirmed && !_closed);

    /// <summary>"Sign in again in your browser to switch Bypass on. This app waits until you approve it there."</summary>
    public string SignInText => _strings.BypassSignIn;

    /// <summary>"The page shows the code ABCD-EFGH.", so the person can compare it; empty when the request has none.</summary>
    public string SignInCodeText => _request is { UserCode.Length: > 0 } request ? _strings.BypassSignInCode(request.UserCode) : "";

    public string OpenAgainText => _strings.BypassOpenAgain;

    public string OpenAgainName => _strings.BypassOpenAgainName;

    /// <summary>Opens the second sign-in's page in the browser again (the first one was closed, or went to another window).</summary>
    public IRelayCommand OpenAgainCommand { get; }

    public string StateText => _state switch
    {
        BypassCardState.Confirming => _strings.BypassConfirming,
        BypassCardState.SigningIn => _strings.BypassSignIn,
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
        if (_state is BypassCardState.Confirming or BypassCardState.SigningIn)
        {
            State = BypassCardState.On;
        }
    }

    /// <summary>
    /// How the second sign-in stands (sdk/41): in the browser, approved (the server is asked now), or why it came to
    /// nothing. A report that arrives before the click's own state is set still counts once the card is answered.
    /// </summary>
    internal void StepUp(StepUpRequest request, StepUpStage stage)
    {
        if (!Waiting)
        {
            return;
        }
        switch (stage)
        {
            case StepUpStage.Browser:
                _request = request;
                OnPropertyChanged(nameof(SignInCodeText));
                State = BypassCardState.SigningIn;
                OpenAgainCommand.NotifyCanExecuteChanged();
                break;
            case StepUpStage.Approved:
                State = BypassCardState.Confirming;
                break;
            case StepUpStage.Denied:
                Failed("denied");
                break;
            case StepUpStage.TimedOut:
                Failed("timed_out");
                break;
            case StepUpStage.Failed:
                Failed("no_browser");
                break;
            case StepUpStage.Cancelled:
                Withdrawn();
                break;
        }
    }

    /// <summary>The server gave no acknowledgement: the card says why, as far as it can tell, and stays until closed.</summary>
    internal void NotConfirmed(Exception? error)
    {
        if (error is BypassSignInException { Stage: StepUpStage.Cancelled })
        {
            Withdrawn();
            return;
        }
        _failure = error switch
        {
            BypassSignInException { Stage: StepUpStage.Denied } => "denied",
            BypassSignInException { Stage: StepUpStage.TimedOut } => "timed_out",
            BypassSignInException => "no_browser",
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
        if (_state is BypassCardState.Pending or BypassCardState.Confirming or BypassCardState.SigningIn)
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

    /// <summary>Allowed and waiting for the browser or the server (the state itself is set just after the click is passed on).</summary>
    internal bool Waiting => _state is BypassCardState.Confirming or BypassCardState.SigningIn
        || (_state == BypassCardState.Pending && _pending.IsResolved);

    private void Failed(string why)
    {
        _failure = why;
        State = BypassCardState.NotConfirmed;
    }

    private void OpenAgain()
    {
        if (_request is { } request)
        {
            _openBrowser(request.Link);
        }
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
        if (_pending.Resolve(answer) && _state == BypassCardState.Pending)
        {
            State = state; // a report of the second sign-in may have come first
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Parts;

/// <summary>What a grant lets the agent do without asking, as the card says it.</summary>
public enum GrantAbility
{
    Read,
    Write,
    ReadWrite,
}

public enum GrantCardState
{
    Pending,
    Allowed,
    Declined,
}

/// <summary>A place on the grant card the user may take out before allowing.</summary>
public sealed class GrantPlaceItem : ObservableObject
{
    private bool _kept = true;

    internal GrantPlaceItem(GrantPlace place, Func<bool> pending)
    {
        Place = place;
        ToggleCommand = new RelayCommand(() => Kept = !Kept, pending);
    }

    public GrantPlace Place { get; }

    public string Label => Place.Label;

    /// <summary>Whether the place stays in the grant.</summary>
    public bool Kept
    {
        get => _kept;
        private set => SetProperty(ref _kept, value);
    }

    public IRelayCommand ToggleCommand { get; }
}

/// <summary>
/// The grant card (desktop 37 §5.3, 38 §7): "Work alongside you?", the places (each can be taken out), the time, what the
/// agent may do without asking and what still asks; for an extension only the difference. Drawn from the places and the
/// classes the app gives, never from the model's words. Allow sends what the user kept; the server's answer is the grant.
/// </summary>
public sealed class GrantPart : ObservableObject
{
    private readonly PendingGrant _pending;
    private UiStrings _strings;
    private DurationOption? _duration;
    private GrantCardState _state;
    private bool _durationMenuOpen;

    internal GrantPart(PendingGrant pending, UiStrings strings)
    {
        _pending = pending;
        _strings = strings;
        Places = new ObservableCollection<GrantPlaceItem>(pending.Offer.Places.Select(p => new GrantPlaceItem(p, () => IsPending)));
        foreach (var place in Places)
        {
            place.PropertyChanged += (_, _) => AllowCommand?.NotifyCanExecuteChanged();
        }
        _duration = Durations.FirstOrDefault(d => d.Value == pending.Offer.Preselected) ?? Durations.FirstOrDefault();
        AllowCommand = new RelayCommand(Allow, CanAllow);
        NotNowCommand = new RelayCommand(() => Answer(GrantAnswer.NotNow, GrantCardState.Declined), () => IsPending);
        PickDurationCommand = new RelayCommand<DurationOption>(d =>
        {
            SelectedDuration = d;
            IsDurationMenuOpen = false;
        }, d => IsPending && d is not null);
    }

    /// <summary>Whether the times to pick from are open.</summary>
    public bool IsDurationMenuOpen
    {
        get => _durationMenuOpen;
        set => SetProperty(ref _durationMenuOpen, value && IsPending);
    }

    /// <summary>Picks a time from the menu and closes it.</summary>
    public IRelayCommand<DurationOption> PickDurationCommand { get; }

    public GrantOffer Offer => _pending.Offer;

    /// <summary>Whether the card extends a running grant (it shows only what it adds).</summary>
    public bool IsExtension => Offer.Extending is not null;

    public string Title => IsExtension ? _strings.GrantExtendTitle : _strings.GrantTitle;

    public string PlacesHeading => IsExtension ? _strings.GrantNewPlaces : _strings.GrantPlaces;

    public bool HasPlaces => Places.Count > 0;

    public ObservableCollection<GrantPlaceItem> Places { get; }

    public string DurationLabel => _strings.GrantDuration;

    /// <summary>The times to pick from; for an extension, how much longer.</summary>
    public IReadOnlyList<DurationOption> Durations => Offer.Durations
        .Select(d => new DurationOption(d, IsExtension ? _strings.GrantMoreTime(_strings.Period(d)) : _strings.Period(d)))
        .ToList();

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

    public string MayText => _strings.GrantMay(
        Offer.Classes.Contains("read") && Offer.Classes.Contains("write") ? GrantAbility.ReadWrite
        : Offer.Classes.Contains("write") ? GrantAbility.Write : GrantAbility.Read);

    public string StillAsksText => _strings.GrantStillAsks;

    /// <summary>The app's reason, if it gave one.</summary>
    public string Reason => Offer.Reason ?? "";

    public GrantCardState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(StateText));
                AllowCommand.NotifyCanExecuteChanged();
                NotNowCommand.NotifyCanExecuteChanged();
                foreach (var place in Places)
                {
                    place.ToggleCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    public bool IsPending => _state == GrantCardState.Pending;

    public string StateText => _state switch
    {
        GrantCardState.Allowed => _strings.GrantAllowed,
        GrantCardState.Declined => _strings.GrantDeclined,
        _ => "",
    };

    public IRelayCommand AllowCommand { get; }

    public IRelayCommand NotNowCommand { get; }

    /// <summary>The card's text as the user sees it now: its SHA-256 goes to the server as evidence.</summary>
    public string ShownText => string.Join("\n", new[]
    {
        Title, _duration?.Label ?? "", HasPlaces ? PlacesHeading + " " + string.Join(" · ", Places.Where(p => p.Kept).Select(p => p.Label)) : "",
        MayText, StillAsksText,
    }.Where(line => line.Length > 0));

    internal void Retext(UiStrings strings)
    {
        _strings = strings;
        var picked = _duration?.Value;
        _duration = Durations.FirstOrDefault(d => d.Value == picked) ?? _duration;
        OnPropertyChanged(string.Empty);
    }

    private bool CanAllow() => IsPending && _duration is not null && (IsExtension || Places.Any(p => p.Kept));

    private void Allow()
    {
        var kept = Places.Where(p => p.Kept).Select(p => p.Place).ToList();
        Answer(new GrantAnswer { Allowed = true, Places = kept, Duration = _duration!.Value, ShownText = ShownText }, GrantCardState.Allowed);
    }

    private void Answer(GrantAnswer answer, GrantCardState state)
    {
        IsDurationMenuOpen = false;
        if (_pending.Resolve(answer))
        {
            State = state;
        }
    }
}

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.UI.Services;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Settings;

/// <summary>Where the setting for old chats reads and writes its state: an agent (<see cref="ChatRetentionSettingsViewModel(FadenstackAgent, IUiDispatcher?, Func{UiStrings}?)"/>), or a test's.</summary>
public interface IChatRetentionSource
{
    /// <summary>How long old chats are kept, and who decides, as far as is known now.</summary>
    ChatRetentionState State { get; }

    /// <summary>Gets ready to know the state (an agent starts, which reads the server's limits and the person's choice).</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Keeps the person's choice; returns the state in force (unchanged where the person may not choose).</summary>
    Task<ChatRetentionState> SetAsync(ChatRetentionChoice choice, CancellationToken cancellationToken);
}

/// <summary>
/// The setting for old chats, for an app's settings page: whether chats are deleted automatically, and after how many days and beyond
/// how many chats. In a standalone agent the person chooses and presses Save; the choice is kept per person on the device
/// (<see cref="FadenstackAgent.SetChatRetentionAsync"/>) and applied at once. It is read only, with a sentence that says why, where the
/// organisation decides (a governed agent: "Your organisation keeps at most N chats for D days on this device") or where the machine
/// policy on this computer fixes it. A view binds to this class and holds no logic: the texts come from <see cref="Strings"/> (say them
/// again on <see cref="Retext"/>), the checks and the state of the buttons are here.
/// </summary>
public sealed class ChatRetentionSettingsViewModel : ObservableObject, ISkillTexts
{
    private readonly IChatRetentionSource _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private ChatRetentionState _state;
    private bool _autoDelete;
    private string _days = "";
    private string _chats = "";
    private bool _saved;

    /// <param name="agent">The agent whose chats it is. Call <see cref="LoadAsync"/> once the settings page opens (it starts the agent).</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public ChatRetentionSettingsViewModel(FadenstackAgent agent, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
        : this(new AgentSource(agent ?? throw new ArgumentNullException(nameof(agent))), dispatcher, strings)
    {
    }

    /// <param name="source">Where the state comes from and goes to.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public ChatRetentionSettingsViewModel(IChatRetentionSource source, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        _state = source.State;
        Reset();
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        CancelCommand = new RelayCommand(Cancel, () => IsDirty);
    }

    public UiStrings Strings => _strings();

    /// <summary>Reads the agent's state again (starting the agent first, which fetches the server's limits and the person's choice).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _source.StartAsync(cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            _state = _source.State;
            _saved = false;
            Reset();
        });
    }

    /// <summary>Who decides: the person, the organisation's server, or the machine policy.</summary>
    public ChatRetentionControl Control => _state.Control;

    /// <summary>The person may change it (standalone, and the machine policy leaves it to them).</summary>
    public bool CanEdit => _state.CanChange;

    /// <summary>It is shown and cannot be changed.</summary>
    public bool IsReadOnly => !_state.CanChange;

    /// <summary>The switch and the two limits show: not in governed mode, where one sentence says what the organisation keeps.</summary>
    public bool ShowControls => _state.Control != ChatRetentionControl.Organisation;

    /// <summary>The sentence about who decides shows (governed, or fixed by the machine policy).</summary>
    public bool ShowNote => _state.Control != ChatRetentionControl.Person;

    /// <summary>"Your organisation keeps at most N chats for D days on this device.", "Your organisation sets no limit." or "Set by your organisation's policy on this computer."</summary>
    public string Note => _state.Control switch
    {
        ChatRetentionControl.Organisation => _state.OrganisationSetsLimit
            ? Strings.ChatRetentionOrganisationLimit(_state.MaxSessions, _state.MaxAgeDays)
            : Strings.ChatRetentionOrganisationNoLimit,
        ChatRetentionControl.MachinePolicy => Strings.ChatRetentionPolicyFixed,
        _ => "",
    };

    /// <summary>Delete old chats automatically. Off keeps every chat until the person deletes it.</summary>
    public bool AutoDelete
    {
        get => _autoDelete;
        set
        {
            if (CanEdit && SetProperty(ref _autoDelete, value))
            {
                Changed();
                OnPropertyChanged(nameof(LimitsEnabled));
            }
        }
    }

    /// <summary>The limits can be typed: automatic deletion is on, and the person may change it.</summary>
    public bool LimitsEnabled => CanEdit && _autoDelete;

    /// <summary>The days after which a chat goes, as typed.</summary>
    public string MaxAgeDaysText
    {
        get => _days;
        set
        {
            if (CanEdit && SetProperty(ref _days, value ?? ""))
            {
                Changed();
                OnPropertyChanged(nameof(DaysError));
                OnPropertyChanged(nameof(HasDaysError));
            }
        }
    }

    /// <summary>The most chats kept, as typed.</summary>
    public string MaxSessionsText
    {
        get => _chats;
        set
        {
            if (CanEdit && SetProperty(ref _chats, value ?? ""))
            {
                Changed();
                OnPropertyChanged(nameof(ChatsError));
                OnPropertyChanged(nameof(HasChatsError));
            }
        }
    }

    /// <summary>Why the days are not valid; empty when they are (or when automatic deletion is off).</summary>
    public string DaysError => LimitsEnabled && Parse(_days, ChatRetentionChoice.IsValidDays) is null
        ? Strings.ChatRetentionDaysError(ChatRetentionChoice.MinDays, ChatRetentionChoice.MaxDays) : "";

    public bool HasDaysError => DaysError.Length > 0;

    /// <summary>Why the chats are not valid; empty when they are (or when automatic deletion is off).</summary>
    public string ChatsError => LimitsEnabled && Parse(_chats, ChatRetentionChoice.IsValidChats) is null
        ? Strings.ChatRetentionChatsError(ChatRetentionChoice.MinChats, ChatRetentionChoice.MaxChats) : "";

    public bool HasChatsError => ChatsError.Length > 0;

    /// <summary>What is typed can be saved: whole numbers within the bounds (not checked while automatic deletion is off).</summary>
    public bool IsValid => !HasDaysError && !HasChatsError;

    /// <summary>The form differs from what is kept.</summary>
    public bool IsDirty => CanEdit && (_autoDelete != _state.AutoDelete
        || (_autoDelete && (_days.Trim() != _state.MaxAgeDays.ToString(CultureInfo.InvariantCulture)
            || _chats.Trim() != _state.MaxSessions.ToString(CultureInfo.InvariantCulture))));

    public bool CanSave => IsDirty && IsValid;

    /// <summary>"Saved." after a save, until the person changes something.</summary>
    public string StatusText => _saved ? Strings.ChatRetentionSaved : "";

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>The app's language changed: every text is said again.</summary>
    public void Retext()
    {
        OnPropertyChanged(nameof(Strings));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(DaysError));
        OnPropertyChanged(nameof(ChatsError));
        OnPropertyChanged(nameof(StatusText));
    }

    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }
        var choice = _autoDelete
            ? new ChatRetentionChoice(true, Parse(_days, ChatRetentionChoice.IsValidDays)!.Value, Parse(_chats, ChatRetentionChoice.IsValidChats)!.Value)
            : new ChatRetentionChoice(false, _state.MaxAgeDays, _state.MaxSessions); // off keeps the limits it had, for later
        var kept = await _source.SetAsync(choice, CancellationToken.None).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            _state = kept;
            _saved = true;
            Reset();
        });
    }

    private void Cancel()
    {
        _saved = false;
        Reset();
    }

    /// <summary>The form shows what is kept.</summary>
    private void Reset()
    {
        _autoDelete = _state.AutoDelete;
        _days = _state.MaxAgeDays.ToString(CultureInfo.InvariantCulture);
        _chats = _state.MaxSessions.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(string.Empty);
        SaveCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
    }

    private void Changed()
    {
        _saved = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(StatusText));
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static int? Parse(string text, Func<int, bool> valid) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && valid(number) ? number : null;

    private sealed class AgentSource : IChatRetentionSource
    {
        private readonly FadenstackAgent _agent;

        internal AgentSource(FadenstackAgent agent) => _agent = agent;

        public ChatRetentionState State => _agent.ChatRetention;

        public Task StartAsync(CancellationToken cancellationToken) => _agent.StartAsync(cancellationToken);

        public Task<ChatRetentionState> SetAsync(ChatRetentionChoice choice, CancellationToken cancellationToken) =>
            _agent.SetChatRetentionAsync(choice, cancellationToken);
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.RegularExpressions;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.ViewModels;

/// <summary>
/// A chat with one agent, for any skin: the conversation as turns, the composer, the tool-mode picker, the
/// session history. The agent runs on a background thread; its events reach the UI thread in batches, one
/// per <see cref="AgentChatOptions.RenderInterval"/>, however fast it streams. Opened sessions show their
/// latest turns and load older ones on request, so long sessions open fast and stay light.
/// </summary>
public sealed class AgentChatViewModel : ObservableObject, IDisposable
{
    private readonly FadenstackAgent _agent;
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
    private bool _isWaitingForSlot;
    private TurnViewModel? _current;
    private List<List<TranscriptEntry>> _history = new();
    private int _loadedFrom;
    private string _title = "";
    private Dictionary<string, JsonElement>? _profileUi;
    private readonly IReadOnlyList<string> _languages;
    private readonly IPreferenceStore _preferences;
    private readonly string? _locked;
    private UiStrings _strings;
    private string _language = Automatic;
    private bool _languagePicked;
    private IReadOnlyList<LanguageOption> _languageOptions = Array.Empty<LanguageOption>();
    private bool _isSettingsOpen;
    private string _input = "";
    private bool _isReady;
    private bool _isBusy;
    private bool _isHistoryOpen;
    private bool _isModeMenuOpen;
    private bool _signInRequired;
    private string? _error;
    private ToolModeOption _selectedMode;
    private AgentProfile? _profile;
    private AboutInfo _about;
    private PanelLink? _brandingLink;
    private bool _isAboutOpen;

    public AgentChatViewModel(FadenstackAgent agent, ApprovalBroker approvals, IUiDispatcher dispatcher, AgentChatOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? new AgentChatOptions();
        _languages = ((IEnumerable<string>?)_options.Languages ?? UiStrings.BuiltInLanguages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _preferences = _options.Preferences ?? PreferenceDefaults.Create();
        _locked = _options.Strings is null && _options.LockedLanguage is { } locked ? Nearest(Culture(locked)) : null;
        if (CanChangeLanguage && Offered(Read()) is { } saved)
        {
            _language = saved;
        }
        _strings = Resolve();
        _languageOptions = BuildLanguageOptions();
        (_about, _brandingLink) = BuildAbout();
        _batcher = new RenderBatcher(dispatcher, _options.RenderInterval);
        _renderers = _options.Renderers.Concat(new IAgentEventRenderer[] { new CitationsRenderer(), new PiiRenderer(), new FallbackRenderer() }).ToList();
        ToolModes = new ObservableCollection<ToolModeOption>(new[] { ToolMode.Off, ToolMode.ReadOnly, ToolMode.Ask, ToolMode.Auto }
            .Select(m => new ToolModeOption(m, Strings)));
        _selectedMode = ToolModes[2];
        _approvals.Requested += OnApprovalRequested;
        SkillPicker = new SkillPickerViewModel(ct => _agent.ListSkillsAsync(null, ct), PickSkill, () => Strings, _options.Clock);

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        SetLanguageCommand = new RelayCommand<string>(id => SetLanguage(id ?? Automatic), _ => CanChangeLanguage);
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
        RemoveSkillCommand = new RelayCommand<SkillOption>(s =>
        {
            if (s is not null)
            {
                RemoveSkill(s.Slug);
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
        OpenAboutCommand = new RelayCommand(OpenAbout);
        CloseAboutCommand = new RelayCommand(() => IsAboutOpen = false);
        CopyDetailsCommand = new RelayCommand(() => Clipboard.SetText(About.Details));
        OpenLinkCommand = new RelayCommand<PanelLink>(OpenLink, link => link is not null);
    }

    // -- state ---------------------------------------------------------------------------------------

    /// <summary>The settings' pick that follows the host's language.</summary>
    public const string Automatic = "auto";

    private const string LanguagePreference = "language";

    /// <summary>A picture's media type, from its <c>data:</c> URL.</summary>
    private static readonly Regex PictureType = new(@"^data:(image/[\w.+-]+)[;,]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The chat's texts, in the language the user picked (or the host's): the skin binds its labels to them.</summary>
    public UiStrings Strings
    {
        get => _strings;
        private set => SetProperty(ref _strings, value);
    }

    /// <summary>The settings' language: <see cref="Automatic"/> (the host's language) or a tag of <see cref="LanguageOptions"/>.</summary>
    public string Language => _locked ?? _language;

    /// <summary>The languages to pick from, "Automatic" first, each named in itself; none when the host fixed the texts.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions => _languageOptions;

    /// <summary>The user can pick a language: the host did not fix the texts, nor the organisation the language.</summary>
    public bool CanChangeLanguage => _options.Strings is null && _locked is null && _languages.Count > 1;

    /// <summary>The organisation fixed the language: the settings show it without a choice.</summary>
    public bool IsLanguageLocked => _options.Strings is null && _locked is not null;

    /// <summary>The settings have a language to show: a choice, or the one fixed.</summary>
    public bool HasLanguageSettings => CanChangeLanguage || IsLanguageLocked;

    /// <summary>The settings menu is open (presentation state, so the skin needs no code).</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    /// <summary>Picks a language (its id: <see cref="Automatic"/> or a tag).</summary>
    public IRelayCommand<string> SetLanguageCommand { get; }

    // -- About and the links ---------------------------------------------------------------------------

    /// <summary>
    /// What About shows (the settings' last item): the agent, the app, the SDK, the server, the links. It is there before
    /// the agent starts, too, and follows the panel's language.
    /// </summary>
    public AboutInfo About
    {
        get => _about;
        private set => SetProperty(ref _about, value);
    }

    /// <summary>About is open (presentation state, so the skin needs no code).</summary>
    public bool IsAboutOpen
    {
        get => _isAboutOpen;
        set => SetProperty(ref _isAboutOpen, value);
    }

    /// <summary>The footer's link shows (<see cref="AgentChatOptions.Branding"/>).</summary>
    public bool Branding => _options.Branding;

    /// <summary>
    /// The footer's "Fadenstack" link, on its own line under the AI notice (<c>ref=&lt;app&gt;-panel</c>); null when the
    /// host turned <see cref="AgentChatOptions.Branding"/> off.
    /// </summary>
    public PanelLink? BrandingLink
    {
        get => _brandingLink;
        private set => SetProperty(ref _brandingLink, value);
    }

    /// <summary>Opens About, closing the menus.</summary>
    public IRelayCommand OpenAboutCommand { get; }

    public IRelayCommand CloseAboutCommand { get; }

    /// <summary>Copies <see cref="AboutInfo.Details"/> (<see cref="AgentChatOptions.Clipboard"/>).</summary>
    public IRelayCommand CopyDetailsCommand { get; }

    /// <summary>Opens one of the panel's links (<see cref="PanelLink"/>) through <see cref="LinkRequested"/>.</summary>
    public IRelayCommand<PanelLink> OpenLinkCommand { get; }

    /// <summary>
    /// A link of the panel's is to open: the skin opens it in the browser (the WPF skin through <c>MarkdownText.OpenLink</c>,
    /// which a host can replace). The view model never opens anything itself.
    /// </summary>
    public event EventHandler<Uri>? LinkRequested;

    /// <summary>Opens <paramref name="link"/> (<see cref="LinkRequested"/>).</summary>
    public void OpenLink(PanelLink? link)
    {
        if (link is not null)
        {
            LinkRequested?.Invoke(this, link.Url);
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// The agent's greeting for an empty chat (profile <c>ui.greeting</c>), in the panel's language when the definition
    /// has it (<c>ui.languages.&lt;tag&gt;.greeting</c>).
    /// </summary>
    public string Greeting => UiField("greeting") is { ValueKind: JsonValueKind.String } greeting
        ? greeting.GetString() ?? ""
        : _title.Length > 0 ? Strings.GreetingFallback(_title) : "";

    /// <summary>Prompts to start with (profile <c>ui.suggestions</c>), in the panel's language when the definition has them.</summary>
    public ObservableCollection<string> Suggestions { get; } = new();

    public ObservableCollection<TurnViewModel> Turns { get; } = new();

    /// <summary>Context that goes with the next message (chips in the composer).</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = new();

    /// <summary>The skills list the composer opens on <c>$</c> (or <c>/</c> at the start); skins draw it while it is open.</summary>
    public SkillPickerViewModel SkillPicker { get; }

    /// <summary>The skills picked for the next message (chips in the composer); sending takes them.</summary>
    public ObservableCollection<SkillOption> PickedSkills { get; } = new();

    /// <summary>The server offers skills: typing <c>$</c> in the composer lists them.</summary>
    public bool CanPickSkills => _profile?.Skills == true;

    /// <summary>The agent's model sees pictures: a picture can go with a message (<see cref="AttachPicture"/>).</summary>
    public bool CanAttachPictures => _profile?.Vision == true;

    public bool IsEmpty => Turns.Count == 0;

    /// <summary>Older turns of an opened session that are not shown yet.</summary>
    public bool HasEarlierTurns => _loadedFrom > 0;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();

    public string? CurrentSessionId => _session?.Id;

    /// <summary>The composer's text; a <c>$</c> at its end (or <c>/</c> as the whole of it) opens the skill picker, narrowed by what follows.</summary>
    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value))
            {
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
                if (CanPickSkills && SkillToken.Find(value) is { } token)
                {
                    SkillPicker.Show(token.Query);
                }
                else
                {
                    SkillPicker.Close();
                }
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

    /// <summary>A message is on its way: the host's <see cref="AgentChatOptions.BeforeSend"/> or its turn is running.</summary>
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

    /// <summary>The turn is held back by the host's <see cref="AgentChatOptions.TurnGate"/> until a slot is free.</summary>
    public bool IsWaitingForSlot
    {
        get => _isWaitingForSlot;
        private set => SetProperty(ref _isWaitingForSlot, value);
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

    /// <summary>The user's pick for this chat. New chats start in Ask.</summary>
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

    /// <summary>What the pick means here, including a ceiling the server sets.</summary>
    public string ToolModeHint
    {
        get
        {
            var ceiling = _agent.Ceiling;
            var effective = Fadenstack.Tools.ToolModes.Narrowest(_selectedMode.Mode, ceiling);
            return effective == _selectedMode.Mode ? _selectedMode.Description
                : Strings.LimitedTo(_selectedMode.Description, ToolModes.First(o => o.Mode == effective).Label);
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

    /// <summary>Takes a picked skill off the next message (its chip's remove button).</summary>
    public IRelayCommand<SkillOption> RemoveSkillCommand { get; }

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
            _profile = profile;
            OnPropertyChanged(nameof(CanPickSkills));
            OnPropertyChanged(nameof(CanAttachPictures));
            AdoptUserLanguage(profile.User?.Language);
            Title = profile.Name;
            RefreshAbout();
            _profileUi = profile.Ui;
            OnPropertyChanged(nameof(Greeting));
            RefreshSuggestions();
            var ceiling = _agent.Ceiling;
            foreach (var option in ToolModes)
            {
                option.IsAvailable = Fadenstack.Tools.ToolModes.Narrowest(option.Mode, ceiling) == option.Mode;
            }
            SelectToolModeCommand.NotifyCanExecuteChanged();
            var start = ToolModes.First(o => o.Mode == _agent.DefaultToolMode);
            SetProperty(ref _selectedMode, start.IsAvailable ? start : ToolModes.Last(o => o.IsAvailable), nameof(SelectedToolMode));
            OnPropertyChanged(nameof(ToolModeHint));
            await RefreshSessionsAsync();
            IsReady = true;
        }
        catch (FadenstackSignInRequiredException e)
        {
            SignInRequired = true;
            Error = e.Message;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>
    /// Sends a message, after the host's <see cref="AgentChatOptions.BeforeSend"/>; completes when its turn has ended
    /// and been shown. A new message clears the last <see cref="Error"/>.
    /// </summary>
    public async Task SendAsync(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || !_isReady || _isBusy)
        {
            return;
        }
        var outgoing = new OutgoingMessage(text);
        var stop = new CancellationTokenSource();
        _stop = stop;
        IsBusy = true;
        Error = null;
        TurnViewModel? turn = null;
        IDisposable? lease = null;
        try
        {
            if (_options.BeforeSend is { } beforeSend)
            {
                try
                {
                    await beforeSend(outgoing, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                }
                catch (Exception e)
                {
                    if (!stop.IsCancellationRequested)
                    {
                        Error = e.Message;
                    }
                    return;
                }
            }
            if (text == _input.Trim())
            {
                Input = "";
            }
            SkillPicker.Close();
            var attached = Attachments.Where(a => a.Sends).ToList();
            foreach (var a in attached.Where(a => !a.IsImplicit))
            {
                Attachments.Remove(a);
            }
            var skills = PickedSkills.ToList(); // for this message only
            PickedSkills.Clear();
            var started = new TurnViewModel(this, text, _options.Clock(), _options.PreviewLength)
            {
                Attachments = skills.Select(s => new AttachmentChip(s.Name, null) { IsSkill = true })
                    .Concat(attached.Select(a => new AttachmentChip(a.Title, a.Subtitle) { Image = a.Image }))
                    .ToList(),
            };
            turn = started;
            Turns.Add(started);
            OnPropertyChanged(nameof(IsEmpty));
            _current = started;
            if (_options.TurnGate is { } gate)
            {
                IsWaitingForSlot = true;
                started.SetWaitingForSlot(true);
                try
                {
                    lease = await gate(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    started.Stop(_options.Clock());
                    return;
                }
                catch (Exception e)
                {
                    started.Fail(e.Message, _options.Clock());
                    return;
                }
                finally
                {
                    IsWaitingForSlot = false;
                    started.SetWaitingForSlot(false);
                }
            }
            await Task.Run(() => RunTurnAsync(started, text, attached, skills, _selectedMode.Mode, stop.Token));
            await _batcher.WhenFlushedAsync();
        }
        finally
        {
            lease?.Dispose();
            _current = null;
            _stop = null;
            stop.Dispose();
            IsBusy = false;
            if (turn is not null)
            {
                TouchCurrentSession(text);
            }
            RunAfterSend(outgoing);
        }
    }

    private void RunAfterSend(OutgoingMessage message)
    {
        if (_options.AfterSend is not { } afterSend)
        {
            return;
        }
        try
        {
            afterSend(message);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>
    /// Picks the panel's language (<see cref="Automatic"/> for the host's) and remembers it on this computer and for the
    /// user, so all their clients and the console follow it. Everything on screen, the conversation too, is drawn again
    /// in it at once. Nothing changes when the language is fixed. Call on the UI thread.
    /// </summary>
    public void SetLanguage(string id) => _ = SetLanguageAsync(id);

    /// <summary>
    /// <see cref="SetLanguage"/>, settling once the server kept the pick (or could not: the pick still holds on this
    /// computer). Call on the UI thread.
    /// </summary>
    public async Task SetLanguageAsync(string id)
    {
        IsSettingsOpen = false;
        if (!CanChangeLanguage || (id != Automatic && Offered(id) is null))
        {
            return;
        }
        _languagePicked = true;
        _language = id == Automatic ? Automatic : Offered(id)!;
        ApplyLanguage();
        try
        {
            _preferences.Set(LanguagePreference, _language == Automatic ? null : _language);
        }
        catch (Exception)
        {
            // not remembered: still this chat's language
        }
        try
        {
            await _agent.SetUserLanguageAsync(_language == Automatic ? null : _language);
        }
        catch (Exception)
        {
            // not kept for the user (an older server, no network): this computer still remembers it
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
        PickedSkills.Clear();
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
            Sessions.Add(new SessionItemViewModel(s.Id, s.Title, s.LastActivity, now, Strings));
        }
        MarkCurrent();
    }

    internal void Edit(string text)
    {
        Input = text;
        RequestComposerFocus();
    }

    /// <summary>
    /// A picture for the next message, as a chip: pasted, dropped, or taken by the host (a screenshot). Only for a model
    /// that sees pictures (<see cref="CanAttachPictures"/>): otherwise nothing is added, <see cref="Error"/> says why, and
    /// it returns false.
    /// </summary>
    /// <param name="dataUrl">The picture as a <c>data:image/...</c> URL, already as small as it should go (the WPF skin's
    /// <c>Pictures.ToDataUrl</c> makes one).</param>
    /// <param name="title">Its name on the chip; "Pasted picture" by default.</param>
    /// <param name="kind">Where it comes from, for the model (<c>screenshot</c>, <c>file</c>); <c>picture</c> by default.</param>
    public bool AttachPicture(string dataUrl, string? title = null, string? kind = null)
    {
        if (dataUrl is null || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A picture is a data:image/... URL.", nameof(dataUrl));
        }
        if (!CanAttachPictures)
        {
            Error = Strings.PicturesNotSeen;
            return false;
        }
        var name = string.IsNullOrWhiteSpace(title) ? Strings.PastedPicture : title!.Trim();
        var attachment = new MessageAttachment
        {
            Title = name,
            Content = dataUrl,
            Kind = string.IsNullOrEmpty(kind) ? "picture" : kind,
            MediaType = PictureType.Match(dataUrl) is { Success: true } m ? m.Groups[1].Value : "image/png",
        };
        Attachments.Add(new AttachmentViewModel(name, Strings.Picture, _ => Task.FromResult<MessageAttachment?>(attachment)) { Image = dataUrl });
        RequestComposerFocus();
        return true;
    }

    /// <summary>Says that a file the user added could not be read as a picture (<see cref="Error"/>).</summary>
    public void PictureNotRead(string name) => Error = Strings.PictureNotRead(name);

    /// <summary>Takes a picked skill off the next message.</summary>
    public void RemoveSkill(string slug)
    {
        foreach (var skill in PickedSkills.Where(s => s.Slug == slug).ToList())
        {
            PickedSkills.Remove(skill);
        }
    }

    /// <summary>Asks the skin to put the focus in the composer (e.g. after a host attached the selection).</summary>
    public void RequestComposerFocus() => ComposerFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The picker's choice: the <c>$...</c> the user typed goes, the skill goes with the next message.</summary>
    private void PickSkill(SkillOption skill)
    {
        if (SkillToken.Find(_input) is { } token)
        {
            // One space before the token stays, so what the user types next does not stick to their text.
            var before = _input.Substring(0, token.Start);
            var kept = before.TrimEnd();
            Input = kept.Length < before.Length ? kept + before[kept.Length] : kept;
        }
        if (!PickedSkills.Any(s => s.Slug == skill.Slug))
        {
            PickedSkills.Add(skill);
        }
        RequestComposerFocus();
    }

    public void Dispose()
    {
        _approvals.Requested -= OnApprovalRequested;
        _stop?.Cancel();
    }

    private void Stop() => _stop?.Cancel();

    private void OpenAbout()
    {
        IsSettingsOpen = false;
        IsModeMenuOpen = false;
        IsHistoryOpen = false;
        IsAboutOpen = true;
    }

    /// <summary>About and the footer's link, in the panel's language, for the agent as far as it is known.</summary>
    private (AboutInfo About, PanelLink? Branding) BuildAbout()
    {
        var about = AboutInfo.Create(_profile, _agent.App, SdkInfo.Version, _agent.Client.Options.ServerUrl, _options.RefSource, Strings);
        var branding = _options.Branding
            ? new PanelLink(AboutInfo.Product, AboutLinks.PanelWebsite(_options.RefSource, SdkInfo.Version), Strings)
            : null;
        return (about, branding);
    }

    private void RefreshAbout() => (About, BrandingLink) = BuildAbout();

    /// <summary>The texts for the language: the host's fixed ones, the organisation's, the user's pick, or the host's language.</summary>
    private UiStrings Resolve()
    {
        if (_options.Strings is { } fixedTexts)
        {
            return fixedTexts;
        }
        var id = _locked ?? _language;
        return new UiStrings(Culture(id == Automatic ? AutomaticTag() : id), _options.TextOverrides);
    }

    /// <summary>The offered language nearest the host's.</summary>
    private string AutomaticTag() => Nearest(_options.AutomaticLanguage ?? CultureInfo.CurrentUICulture);

    /// <summary>The offered language nearest <paramref name="culture"/>: de-AT finds de, zh-CN and zh zh-Hans; English when none.</summary>
    private string Nearest(CultureInfo culture) => NearestOffered(culture) ?? Offered("en") ?? _languages[0];

    private string? NearestOffered(CultureInfo culture)
    {
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (Offered(c.Name) is { } offered)
            {
                return offered;
            }
        }
        // A language alone, as the console writes it: zh is zh-CN, whose parent is zh-Hans.
        if (culture.IsNeutralCulture && !Equals(culture, CultureInfo.InvariantCulture))
        {
            try
            {
                var specific = CultureInfo.CreateSpecificCulture(culture.Name);
                if (!Equals(specific, culture) && !Equals(specific, CultureInfo.InvariantCulture))
                {
                    return NearestOffered(specific);
                }
            }
            catch (CultureNotFoundException)
            {
                // none: as it is
            }
        }
        return null;
    }

    /// <summary>
    /// The language the user picked in another client or the console wins over this computer's, when the panel offers it
    /// (de-AT is German, zh Simplified Chinese; a language it lacks changes nothing). A pick made here since the panel
    /// opened stays.
    /// </summary>
    private void AdoptUserLanguage(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || _languagePicked || !CanChangeLanguage)
        {
            return;
        }
        var culture = Culture(tag!);
        if (Equals(culture, CultureInfo.InvariantCulture) || NearestOffered(culture) is not { } key || key == _language)
        {
            return;
        }
        _language = key;
        ApplyLanguage();
        try
        {
            _preferences.Set(LanguagePreference, key);
        }
        catch (Exception)
        {
            // not remembered on this computer: the profile says it again next time
        }
    }

    private string? Offered(string? tag) => tag is null ? null : _languages.FirstOrDefault(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase));

    private static CultureInfo Culture(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private string? Read()
    {
        try
        {
            return _preferences.Get(LanguagePreference);
        }
        catch (Exception)
        {
            return null; // the store failed: the automatic language
        }
    }

    private IReadOnlyList<LanguageOption> BuildLanguageOptions()
    {
        if (_options.Strings is not null)
        {
            return Array.Empty<LanguageOption>();
        }
        var automatic = new UiStrings(Culture(AutomaticTag()), _options.TextOverrides);
        var chosen = Language;
        return new[] { new LanguageOption(Automatic, Strings.LanguageAutomatic(automatic.LanguageName), Strings.Language, chosen == Automatic) }
            .Concat(_languages.Select(tag =>
            {
                var texts = new UiStrings(Culture(tag), _options.TextOverrides);
                return new LanguageOption(tag, texts.LanguageName, texts.Language, string.Equals(chosen, tag, StringComparison.OrdinalIgnoreCase));
            }))
            .ToList();
    }

    /// <summary>Every text on screen in the new language: the conversation's, the history's, the menus'.</summary>
    private void ApplyLanguage()
    {
        var strings = Resolve();
        Strings = strings;
        foreach (var option in ToolModes)
        {
            option.Retext(strings);
        }
        var now = _options.Clock();
        foreach (var session in Sessions)
        {
            session.Retext(strings, now);
        }
        foreach (var turn in Turns)
        {
            turn.Retext(strings);
        }
        SkillPicker.Retext();
        RefreshAbout();
        _languageOptions = BuildLanguageOptions();
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(Greeting));
        RefreshSuggestions();
        OnPropertyChanged(nameof(ToolModeHint));
    }

    /// <summary>The agent's suggestions in the panel's language (six at most).</summary>
    private void RefreshSuggestions()
    {
        var suggestions = UiField("suggestions") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).Take(6).ToList()
            : new List<string>();
        if (suggestions.SequenceEqual(Suggestions))
        {
            return;
        }
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }
    }

    /// <summary>
    /// A field of the agent's <c>ui</c> in the panel's language: <c>ui.languages</c> may carry the greeting and suggestions
    /// per language tag ("de", "zh-Hans"; "de" also serves "de-AT"), the definition's own serve the rest.
    /// </summary>
    private JsonElement? UiField(string name)
    {
        if (_profileUi is null)
        {
            return null;
        }
        if (_profileUi.TryGetValue("languages", out var languages) && languages.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in LanguageTags(Strings))
            {
                foreach (var own in languages.EnumerateObject())
                {
                    if (string.Equals(own.Name, tag, StringComparison.OrdinalIgnoreCase) && own.Value.ValueKind == JsonValueKind.Object
                        && own.Value.TryGetProperty(name, out var value))
                    {
                        return value;
                    }
                }
            }
        }
        return _profileUi.TryGetValue(name, out var field) ? field : null;
    }

    /// <summary>The chat's language, the most specific tag first ("de-AT", "de"), then the one its texts are in.</summary>
    private static IEnumerable<string> LanguageTags(UiStrings strings)
    {
        for (var culture = strings.Culture; !Equals(culture, CultureInfo.InvariantCulture); culture = culture.Parent)
        {
            yield return culture.Name;
        }
        yield return strings.Language;
    }

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

    private async Task RunTurnAsync(TurnViewModel turn, string text, IReadOnlyList<AttachmentViewModel> attached, IReadOnlyList<SkillOption> skills,
        ToolMode mode, CancellationToken cancellationToken)
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
                    _batcher.Enqueue(() => turn.AddPart(Parts.NoticePart.Localized(Strings, Parts.NoticeSeverity.Warning, s => (s.NotAttached(title), e.Message, null))));
                }
            }
            if (_session is null)
            {
                _session = await _agent.StartSessionAsync(mode, cancellationToken).ConfigureAwait(false);
                _batcher.Enqueue(() => OnPropertyChanged(nameof(CurrentSessionId)));
            }
            _session.ToolMode = mode;
            var options = new SendOptions
            {
                Attachments = attachments,
                Skills = skills.Select(s => new PickedSkill(s.Slug, s.Name)).ToList(),
            };
            await foreach (var e in _session.StreamAsync(text, options, cancellationToken).ConfigureAwait(false))
            {
                Apply(turn, e);
            }
        }
        catch (OperationCanceledException)
        {
            _batcher.Enqueue(() => turn.Stop(_options.Clock()));
        }
        catch (FadenstackSignInRequiredException e)
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
        // Another chat's request (several chats share one broker): not ours to show, keep, or answer.
        if (pending.Request.SessionId.Length > 0 && pending.Request.SessionId != _session?.Id)
        {
            return;
        }
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
            item = new SessionItemViewModel(_session.Id, text.Length <= 60 ? text : text.Substring(0, 57) + "...", now, now, Strings);
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
}

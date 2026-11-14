using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Wizard;

/// <summary>Why a typed address was not taken.</summary>
public enum AddressProblem
{
    None,

    /// <summary>Not an http(s) address, or one with a user name in it.</summary>
    Invalid,

    /// <summary>Plain http to another computer: https is needed (or, for an endpoint, the local network with the opt-in).</summary>
    HttpsRequired,

    /// <summary>The machine policy's <c>endpoints</c> list does not have it.</summary>
    NotAllowed,

    /// <summary>The machine policy's <c>plainHttp</c> is forbidden.</summary>
    PlainHttpForbidden,

    /// <summary>A key over plain http to the local network: never (D3).</summary>
    KeyNeedsHttps,
}

/// <summary>Why the first pairing request failed (<see cref="WizardStep.StartFailed"/>).</summary>
public enum StartFailure
{
    Unreachable,
    CertificateNotTrusted,

    /// <summary>Too many pairings from this address: <see cref="ConnectionWizardViewModel.RetryAt"/> says when to try again.</summary>
    RateLimited,

    /// <summary>The server named its sign-in page on another address than its own (a proxy's <c>Host</c> or <c>X-Forwarded-Proto</c>).</summary>
    PageElsewhere,

    ServerError,
}

/// <summary>Why the password sign-in failed (<see cref="WizardStep.PasswordError"/>).</summary>
public enum PasswordFailure
{
    BadCredentials,
    HttpsRequired,
    NotFadenstack,
    Unreachable,
    CertificateNotTrusted,
    Other,
}

/// <summary>
/// The connection wizard (sdk/51 F15, I15): one state machine for "Connect to my organisation" (pairing, the password
/// fallback) and "Use my own AI" (acceptance, the local search, the key, the check, the model), and for Settings ›
/// Connection. Platform-neutral: the skins draw <see cref="Step"/> and bind the commands; the app gives the stores, its
/// own steps and its declarations (<see cref="WizardOptions"/>), calls <see cref="StartAsync"/>, and on
/// <see cref="Completed"/> builds the agent from <see cref="Result"/>.
/// </summary>
/// <remarks>
/// No secret stays here (ASVS 5.0 V13): a key goes straight to the <see cref="IEndpointKeyStore"/> and only
/// <see cref="KeySet"/> remains; a password is a command parameter from the skin's secure input, passed once to the login
/// call; the pairing code stays inside the SDK. The view model never assumes the UI thread: after every await it returns to
/// it through <see cref="IUiDispatcher"/>. A newer action makes every older one's answer stale, and a stale answer changes
/// nothing.
/// </remarks>
public sealed class ConnectionWizardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The problems that make an endpoint unusable, the one to show first first.</summary>
    private static readonly EndpointProblem[] Blocking =
    [
        EndpointProblem.KeyRejected, EndpointProblem.OriginRefused, EndpointProblem.CertificateNotTrusted,
        EndpointProblem.Unreachable, EndpointProblem.PlainHttpRefused, EndpointProblem.RateLimited,
        EndpointProblem.ModelNotFound, EndpointProblem.NotCompatible,
    ];

    private static readonly HashSet<WizardStep> Busy =
    [
        WizardStep.BeforeChoice, WizardStep.AppPrecondition, WizardStep.Checking, WizardStep.ServerAccepted,
        WizardStep.PairStarting, WizardStep.Approved, WizardStep.Redeeming, WizardStep.OwnSearching, WizardStep.BeforeCheck,
        WizardStep.OwnChecking, WizardStep.OwnDone, WizardStep.AfterConnect,
    ];

    private readonly WizardOptions _options;
    private readonly IConnectionStore _store;
    private readonly IServerSignIn? _server;
    private readonly IEndpointChecker _checker;
    private readonly IEndpointKeyStore? _keys;
    private readonly IWizardClock _clock;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboard _clipboard;
    private readonly ILinkOpener? _links;
    private readonly Func<UiStrings> _strings;

    private CancellationTokenSource? _run;
    private IDisposable? _timer;
    private IDisposable? _retryTimer;
    private PairingTicket? _ticket;
    private ModelEndpoint? _pending;
    private ITokenProvider? _keyProvider;
    private bool _modelChosen;
    private bool _started;
    private bool _passwordBusy;

    private WizardStep _step = WizardStep.Choose;
    private WizardOutcome _outcome;
    private ConnectionResult? _result;
    private IWizardStep? _appStep;
    private bool _appStepRefused;
    private bool _storeFailed;
    private bool _signInEnded;
    private string _serverAddressText = "";
    private AddressProblem _addressError;
    private Uri? _serverUrl;
    private string? _userCode;
    private Uri? _pairingPage;
    private TimeSpan _timeLeft;
    private bool _isRetrying;
    private StartFailure? _startFailure;
    private DateTimeOffset? _retryAt;
    private SignedInUser? _person;
    private IReadOnlyList<AgentSignIn> _agents = Array.Empty<AgentSignIn>();
    private string _email = "";
    private PasswordFailure? _passwordFailure;
    private bool _accepted;
    private bool _searchConsent;
    private IReadOnlyList<LocalEndpoint> _found = Array.Empty<LocalEndpoint>();
    private EndpointPreset? _selectedPreset;
    private string _endpointAddressText = "";
    private bool _needsKey;
    private bool _keySet;
    private EndpointProblem? _problem;
    private EndpointReport? _report;
    private IReadOnlyList<string> _models = Array.Empty<string>();
    private string? _selectedModel;
    private bool _deleteLocalChats;

    /// <param name="options">What the app declares and what the wizard runs with.</param>
    /// <param name="store">Where the app keeps the connection.</param>
    /// <param name="server">Path A's server side (<see cref="ServerSignIn"/>); needed when the app offers the server path.</param>
    /// <param name="keys">The key store for endpoint keys; needed when the app offers its own AI.</param>
    /// <param name="checker">Path B's checks; <see cref="EndpointChecker"/> with <see cref="WizardOptions.CheckOptions"/> by default.</param>
    /// <param name="clock">The time and the timers; <see cref="SystemWizardClock"/> by default.</param>
    /// <param name="dispatcher">The UI thread; inline by default (tests, headless hosts).</param>
    /// <param name="clipboard">For "copy the code".</param>
    /// <param name="links">Opens the pairing page in the browser.</param>
    /// <param name="strings">The texts; the current UI language's by default (the acceptance record names its language).</param>
    public ConnectionWizardViewModel(WizardOptions options, IConnectionStore store, IServerSignIn? server = null,
        IEndpointKeyStore? keys = null, IEndpointChecker? checker = null, IWizardClock? clock = null,
        IUiDispatcher? dispatcher = null, IClipboard? clipboard = null, ILinkOpener? links = null, Func<UiStrings>? strings = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (options.OffersServer && server is null)
        {
            throw new ArgumentException("The server path needs its sign-in (ServerSignIn).", nameof(server));
        }
        if (options.OffersOwnAi && keys is null)
        {
            throw new ArgumentException("The own-AI path needs a key store.", nameof(keys));
        }
        _server = server;
        _keys = keys;
        _checker = checker ?? new EndpointChecker(options.CheckOptions);
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        _clock = clock ?? new SystemWizardClock(_dispatcher);
        _clipboard = clipboard ?? new NoClipboard();
        _links = links;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);

        var policy = options.Policy;
        Presets = EndpointPreset.All.Where(p => p.BaseUrl is null || policy.AllowsEndpoint(p.BaseUrl)).ToList();
        if (policy.Server is { } named)
        {
            _serverAddressText = named.AbsoluteUri;
        }

        ChooseServerCommand = new RelayCommand(OpenServerAddress, () => Is(WizardStep.Choose) && CanChooseServer);
        ChooseOwnAiCommand = new RelayCommand(OpenOwnAccept, () => Is(WizardStep.Choose) && CanChooseOwnAi);
        ChooseDemoCommand = new RelayCommand(() => Finish(WizardOutcome.Demo), () => Is(WizardStep.Choose) && CanChooseDemo);
        CloseCommand = new RelayCommand(() => Finish(WizardOutcome.Closed), () => _step != WizardStep.Finished);
        CancelCommand = new RelayCommand(Cancel, CanCancel);
        BackCommand = new RelayCommand(Back, CanGoBack);
        SubmitServerCommand = new AsyncRelayCommand(SubmitServerAsync, () => Is(WizardStep.ServerAddress));
        ChangeServerCommand = new RelayCommand(ChangeServer, CanChangeServer);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        NewCodeCommand = new AsyncRelayCommand(StartPairingAsync, () => Is(WizardStep.Denied, WizardStep.Expired));
        UsePasswordCommand = new RelayCommand(UsePassword,
            () => _options.PasswordFallback && Is(WizardStep.PairCode, WizardStep.ServerTooOld));
        BackToCodeCommand = new AsyncRelayCommand(StartPairingAsync,
            () => !_passwordBusy && Is(WizardStep.Password, WizardStep.PasswordError));
        SignInWithPasswordCommand = new AsyncRelayCommand<string>(SignInWithPasswordAsync,
            _ => !_passwordBusy && Is(WizardStep.Password));
        OpenPairingPageCommand = new RelayCommand(() => _links?.TryOpen(_pairingPage!), () => Is(WizardStep.PairCode) && _pairingPage is not null);
        CopyCodeCommand = new RelayCommand(() => _clipboard.SetText(_userCode!), () => Is(WizardStep.PairCode) && _userCode is not null);
        AcceptCommand = new AsyncRelayCommand(AcceptAsync, () => Is(WizardStep.OwnAccept) && _accepted);
        ServerModeInsteadCommand = new RelayCommand(OpenServerAddress, () => Is(WizardStep.OwnAccept) && CanChooseServer);
        SelectPresetCommand = new RelayCommand<EndpointPreset>(SelectPreset, p => p is not null && Is(WizardStep.OwnPick));
        SelectFoundCommand = new RelayCommand<LocalEndpoint>(SelectFound, f => f is not null && Is(WizardStep.OwnPick));
        UseEndpointCommand = new AsyncRelayCommand(UseEndpointAsync, () => Is(WizardStep.OwnPick));
        ConfirmPlainHttpCommand = new AsyncRelayCommand(RunCheckAsync, () => Is(WizardStep.OwnPlainHttp));
        SubmitKeyCommand = new AsyncRelayCommand<string>(SubmitKeyAsync, _ => Is(WizardStep.OwnKey));
        EnterKeyCommand = new RelayCommand(() =>
        {
            NeedsKey = true;
            Go(WizardStep.OwnKey);
        }, () => Is(WizardStep.OwnFailed) && _problem == EndpointProblem.KeyRejected && _pending is { AllowPlainHttpInLan: false });
        ShowHelpCommand = new RelayCommand(() => Go(WizardStep.OwnHelp),
            () => Is(WizardStep.OwnFailed) && _problem == EndpointProblem.OriginRefused);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync,
            () => Is(WizardStep.OwnResult) || (Is(WizardStep.OwnModel) && !string.IsNullOrEmpty(_selectedModel)));
        ConnectOrganisationCommand = new RelayCommand(OpenServerAddress, () => Is(WizardStep.Settings) && CanChooseServer);
        ChangeEndpointCommand = new RelayCommand(OpenOwnAccept, () => Is(WizardStep.Settings) && CanChooseOwnAi);
        SignOutCommand = new RelayCommand(() =>
        {
            DeleteLocalChats = false;
            Go(WizardStep.SignOut);
        }, () => Is(WizardStep.Settings) && _options.Current is not null);
        ConfirmSignOutCommand = new AsyncRelayCommand(ConfirmSignOutAsync, () => Is(WizardStep.SignOut));
    }

    /// <summary>The wizard is over (<see cref="WizardStep.Finished"/>): how, in <see cref="Outcome"/>.</summary>
    public event EventHandler<WizardOutcome>? Completed;

    // ---- State ----

    public WizardStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                Refresh();
            }
        }
    }

    public WizardOutcome Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }

    /// <summary>The connection, once made (saved before <see cref="Completed"/>); keys and tokens are never in it.</summary>
    public ConnectionResult? Result { get => _result; private set => SetProperty(ref _result, value); }

    /// <summary>A step is running: the skin shows progress and offers Cancel.</summary>
    public bool IsBusy => Busy.Contains(_step) || _passwordBusy;

    /// <summary>The app's step that runs now (a skin may draw it), or null.</summary>
    public IWizardStep? AppStep { get => _appStep; private set => SetProperty(ref _appStep, value); }

    /// <summary>The app's last step stopped the flow (a permission refused).</summary>
    public bool AppStepRefused { get => _appStepRefused; private set => SetProperty(ref _appStepRefused, value); }

    /// <summary>
    /// The app could not keep what the wizard handed it (its settings could not be written): the step that needed it is
    /// shown again, to try once more. Cleared when the next action starts.
    /// </summary>
    public bool StoreFailed { get => _storeFailed; private set => SetProperty(ref _storeFailed, value); }

    /// <summary>The "your sign-in ended" banner (<see cref="WizardEntry.ReSignIn"/>).</summary>
    public bool SignInEnded { get => _signInEnded; private set => SetProperty(ref _signInEnded, value); }

    public bool CanChooseServer => _options.OffersServer;

    /// <summary>Own AI is offered and the policy allows it (<c>standalone</c>).</summary>
    public bool CanChooseOwnAi => _options.OffersOwnAi && _options.Policy.Standalone == PolicyAccess.Allowed;

    public bool CanChooseDemo => _options.OffersDemo && _options.Policy.Server is null;

    /// <summary>The machine policy names the server: the address cannot be changed.</summary>
    public bool ServerLocked => _options.Policy.Server is not null;

    /// <summary>The connection the app has now (Settings).</summary>
    public ConnectionResult? Current => _options.Current;

    // Path A.

    public string ServerAddressText
    {
        get => _serverAddressText;
        set
        {
            if (!ServerLocked && SetProperty(ref _serverAddressText, value ?? ""))
            {
                AddressError = AddressProblem.None;
            }
        }
    }

    public AddressProblem AddressError { get => _addressError; private set => SetProperty(ref _addressError, value); }

    /// <summary>The server being connected (the address as kept: no query, no fragment).</summary>
    public Uri? ServerUrl { get => _serverUrl; private set => SetProperty(ref _serverUrl, value); }

    /// <summary>The code to show (<see cref="WizardStep.PairCode"/>). Show it, never log it.</summary>
    public string? UserCode { get => _userCode; private set => SetProperty(ref _userCode, value); }

    /// <summary>The console page with the code filled in.</summary>
    public Uri? PairingPage { get => _pairingPage; private set => SetProperty(ref _pairingPage, value); }

    /// <summary>How long the code is still valid; ticks each second.</summary>
    public TimeSpan TimeLeft { get => _timeLeft; private set => SetProperty(ref _timeLeft, value); }

    /// <summary>The server could not be reached for a moment; the wait goes on by itself (the "retrying" banner).</summary>
    public bool IsRetrying { get => _isRetrying; private set => SetProperty(ref _isRetrying, value); }

    public StartFailure? StartFailure { get => _startFailure; private set => SetProperty(ref _startFailure, value); }

    /// <summary>After a rate limit: when a new try is allowed.</summary>
    public DateTimeOffset? RetryAt { get => _retryAt; private set => SetProperty(ref _retryAt, value); }

    /// <summary>Who signed in ("Signed in as …").</summary>
    public SignedInUser? Person { get => _person; private set => SetProperty(ref _person, value); }

    /// <summary>Each agent's sign-in.</summary>
    public IReadOnlyList<AgentSignIn> Agents { get => _agents; private set => SetProperty(ref _agents, value); }

    public bool PasswordFallback => _options.PasswordFallback;

    /// <summary>The e-mail for the password sign-in. The password itself is never a property.</summary>
    public string Email { get => _email; set => SetProperty(ref _email, value ?? ""); }

    public PasswordFailure? PasswordFailure { get => _passwordFailure; private set => SetProperty(ref _passwordFailure, value); }

    // Path B.

    /// <summary>The consent box; starts unticked.</summary>
    public bool Accepted
    {
        get => _accepted;
        set
        {
            if (SetProperty(ref _accepted, value))
            {
                AcceptCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Consent to search this computer for model servers; without it only a preset or an address.</summary>
    public bool SearchConsent { get => _searchConsent; set => SetProperty(ref _searchConsent, value); }

    public bool CanSearchThisComputer => _options.CanSearchThisComputer;

    /// <summary>The model servers the search found here (what the policy allows).</summary>
    public IReadOnlyList<LocalEndpoint> Found { get => _found; private set => SetProperty(ref _found, value); }

    /// <summary>The presets the policy allows.</summary>
    public IReadOnlyList<EndpointPreset> Presets { get; }

    public EndpointPreset? SelectedPreset { get => _selectedPreset; private set => SetProperty(ref _selectedPreset, value); }

    public string EndpointAddressText
    {
        get => _endpointAddressText;
        set
        {
            if (SetProperty(ref _endpointAddressText, value ?? ""))
            {
                AddressError = AddressProblem.None;
            }
        }
    }

    /// <summary>The endpoint needs a key (from the preset; the person may set it for an address of their own).</summary>
    public bool NeedsKey { get => _needsKey; set => SetProperty(ref _needsKey, value); }

    /// <summary>A key is in the key store for this endpoint. The key itself never is a property.</summary>
    public bool KeySet { get => _keySet; private set => SetProperty(ref _keySet, value); }

    /// <summary>Why the endpoint is unusable (<see cref="WizardStep.OwnFailed"/>).</summary>
    public EndpointProblem? Problem { get => _problem; private set => SetProperty(ref _problem, value); }

    /// <summary>The check's report.</summary>
    public EndpointReport? Report { get => _report; private set => SetProperty(ref _report, value); }

    public IReadOnlyList<string> Models { get => _models; private set => SetProperty(ref _models, value); }

    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                ContinueCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // Settings.

    /// <summary>Sign-out also deletes the chats kept on this computer; starts unticked.</summary>
    public bool DeleteLocalChats { get => _deleteLocalChats; set => SetProperty(ref _deleteLocalChats, value); }

    // ---- Commands ----

    public IRelayCommand ChooseServerCommand { get; }
    public IRelayCommand ChooseOwnAiCommand { get; }
    public IRelayCommand ChooseDemoCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand SubmitServerCommand { get; }
    public IRelayCommand ChangeServerCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand NewCodeCommand { get; }
    public IRelayCommand UsePasswordCommand { get; }
    public IAsyncRelayCommand BackToCodeCommand { get; }

    /// <summary>The password sign-in; the parameter is the password from the skin's secure input, passed once and kept nowhere.</summary>
    public IAsyncRelayCommand<string> SignInWithPasswordCommand { get; }

    public IRelayCommand OpenPairingPageCommand { get; }
    public IRelayCommand CopyCodeCommand { get; }
    public IAsyncRelayCommand AcceptCommand { get; }
    public IRelayCommand ServerModeInsteadCommand { get; }
    public IRelayCommand<EndpointPreset> SelectPresetCommand { get; }
    public IRelayCommand<LocalEndpoint> SelectFoundCommand { get; }
    public IAsyncRelayCommand UseEndpointCommand { get; }
    public IAsyncRelayCommand ConfirmPlainHttpCommand { get; }

    /// <summary>The key; the parameter is the key from the skin's secure input, handed to the key store at once.</summary>
    public IAsyncRelayCommand<string> SubmitKeyCommand { get; }

    public IRelayCommand EnterKeyCommand { get; }
    public IRelayCommand ShowHelpCommand { get; }
    public IAsyncRelayCommand ContinueCommand { get; }
    public IRelayCommand ConnectOrganisationCommand { get; }
    public IRelayCommand ChangeEndpointCommand { get; }
    public IRelayCommand SignOutCommand { get; }
    public IAsyncRelayCommand ConfirmSignOutCommand { get; }

    // ---- Entry ----

    /// <summary>Starts the wizard as <see cref="WizardOptions.Entry"/> says. Once per view model.</summary>
    public Task StartAsync()
    {
        if (_started)
        {
            throw new InvalidOperationException("A wizard runs once; make a new one.");
        }
        _started = true;
        switch (_options.Entry)
        {
            case WizardEntry.Settings:
                Go(WizardStep.Settings);
                return Task.CompletedTask;
            case WizardEntry.ServerKnown:
            case WizardEntry.ReSignIn:
                SignInEnded = _options.Entry == WizardEntry.ReSignIn;
                if ((_options.Policy.Server ?? _options.Server) is not { } known || !_options.OffersServer)
                {
                    OpenServerAddress();
                    return Task.CompletedTask;
                }
                ServerUrl = known;
                _serverAddressText = known.AbsoluteUri;
                OnPropertyChanged(nameof(ServerAddressText));
                return StartPairingAsync();
            default:
                return FirstRunAsync();
        }
    }

    private async Task FirstRunAsync()
    {
        var run = NewRun();
        switch (await RunStepsAsync(WizardPoint.BeforeChoice, new WizardStepContext(WizardPoint.BeforeChoice), WizardStep.BeforeChoice, run))
        {
            case Steps.Stale:
                return;
            case Steps.Stopped:
                Finish(WizardOutcome.Closed);
                return;
        }
        if (_options.Policy.Server is { } named && _options.OffersServer)
        {
            ServerUrl = named; // the policy's server: path A, locked
            await StartPairingAsync();
            return;
        }
        ShowChoice();
    }

    /// <summary>The choice, or straight on when only one path is offered and allowed.</summary>
    private void ShowChoice()
    {
        var paths = (CanChooseServer ? 1 : 0) + (CanChooseOwnAi ? 1 : 0);
        if (CanChooseDemo || paths != 1)
        {
            Go(WizardStep.Choose);
        }
        else if (CanChooseServer)
        {
            OpenServerAddress();
        }
        else
        {
            OpenOwnAccept();
        }
    }

    /// <summary>Back to where the wizard began: Settings, or the choice.</summary>
    private void BackToStart()
    {
        CancelRun();
        Result = null;
        if (_options.Entry == WizardEntry.Settings)
        {
            Go(WizardStep.Settings);
        }
        else
        {
            ShowChoice();
        }
    }

    // ---- Path A ----

    private void OpenServerAddress()
    {
        CancelRun();
        AddressError = AddressProblem.None;
        AppStepRefused = false;
        Go(WizardStep.ServerAddress);
    }

    private async Task SubmitServerAsync()
    {
        var server = ParseServer(_serverAddressText, out var problem);
        if (server is null)
        {
            AddressError = problem;
            return;
        }
        AddressError = AddressProblem.None;
        AppStepRefused = false;
        ServerUrl = server;
        var run = NewRun();
        switch (await RunStepsAsync(WizardPoint.BeforeServerCheck, new WizardStepContext(WizardPoint.BeforeServerCheck, server),
            WizardStep.AppPrecondition, run))
        {
            case Steps.Stale:
                return;
            case Steps.Stopped:
                Go(WizardStep.ServerAddress); // the app's step refused (a permission)
                return;
        }
        await CheckServerAsync(run);
    }

    /// <summary>
    /// The address rule (I15 §2): the scheme, host, port and path are kept, the query and fragment dropped; no scheme means
    /// https; https, or http to this machine, or the http server a machine policy names; no user name in it.
    /// </summary>
    internal Uri? ParseServer(string text, out AddressProblem problem)
    {
        problem = AddressProblem.Invalid;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (!trimmed.Contains("://"))
        {
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Host.Length == 0)
        {
            return null;
        }
        var kept = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri;
        if (kept.Scheme == Uri.UriSchemeHttp && !kept.IsLoopback && !IsPolicyServer(kept))
        {
            problem = AddressProblem.HttpsRequired;
            return null;
        }
        problem = AddressProblem.None;
        return kept;
    }

    private bool IsPolicyServer(Uri server) =>
        _options.Policy.Server is { } named && string.Equals(named.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            server.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private async Task CheckServerAsync(CancellationTokenSource run)
    {
        Go(WizardStep.Checking);
        try
        {
            await _server!.CheckAsync(_serverUrl!, run.Token).ConfigureAwait(false);
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
        {
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
            switch (e)
            {
                case ServerCertificateException:
                    Go(WizardStep.CertificateNotTrusted);
                    break;
                case FadenstackUnreachableException:
                case OperationCanceledException:
                    Go(WizardStep.Unreachable);
                    break;
                case FadenstackHttpsRequiredException:
                    AddressError = AddressProblem.HttpsRequired;
                    Go(WizardStep.ServerAddress);
                    break;
                default:
                    Go(WizardStep.NotFadenstack);
                    break;
            }
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Go(WizardStep.ServerAccepted);
        if (!await StoreAsync(ct => _store.SaveServerAsync(_serverUrl!, ct), run))
        {
            if (IsCurrent(run))
            {
                Go(WizardStep.ServerAddress);
            }
            return;
        }
        if (_options.RestartsOnNewServer)
        {
            Finish(WizardOutcome.RestartOnServer); // the app starts again on it, as ServerKnown
            return;
        }
        await StartPairingAsync();
    }

    private async Task StartPairingAsync()
    {
        var run = NewRun();
        ClearPairing();
        StartFailure = null;
        RetryAt = null;
        Go(WizardStep.PairStarting);
        PairingTicket ticket;
        try
        {
            ticket = await _server!.StartAsync(_serverUrl!, run.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
        {
            await ToUi();
            if (IsCurrent(run))
            {
                StartFailed(e);
            }
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return;
        }
        _ticket = ticket;
        UserCode = ticket.UserCode;
        PairingPage = ticket.VerificationUriComplete;
        UpdateTimeLeft();
        Go(WizardStep.PairCode);
        ScheduleTick(run);
        await WaitForApprovalAsync(ticket, run);
    }

    private void StartFailed(Exception e)
    {
        if (e is FadenstackPairingUnsupportedException)
        {
            Go(WizardStep.ServerTooOld);
            return;
        }
        StartFailure = e switch
        {
            FadenstackRateLimitedException => Wizard.StartFailure.RateLimited,
            FadenstackPairingPageElsewhereException => Wizard.StartFailure.PageElsewhere,
            ServerCertificateException => Wizard.StartFailure.CertificateNotTrusted,
            FadenstackUnreachableException or OperationCanceledException => Wizard.StartFailure.Unreachable,
            _ => Wizard.StartFailure.ServerError,
        };
        if (e is FadenstackRateLimitedException limited)
        {
            // The rate limit happens only here: the poll's slow_down is handled silently in the SDK.
            var wait = limited.RetryAfter is { } after && after > TimeSpan.Zero ? after : DefaultRetryAfter;
            RetryAt = _clock.Now + wait;
            _retryTimer?.Dispose();
            _retryTimer = _clock.After(wait, () => RetryCommand.NotifyCanExecuteChanged());
        }
        Go(WizardStep.StartFailed);
    }

    private async Task WaitForApprovalAsync(PairingTicket ticket, CancellationTokenSource run)
    {
        PairingApproval approval;
        try
        {
            approval = await _server!.WaitAsync(ticket, new Callback<bool>(retrying => _dispatcher.Post(() =>
            {
                if (IsCurrent(run))
                {
                    IsRetrying = retrying;
                }
            })), run.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
        {
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
            StopTimer();
            IsRetrying = false;
            Go(e switch
            {
                FadenstackPairingDeniedException => WizardStep.Denied,
                FadenstackPairingExpiredException => WizardStep.Expired,
                _ => WizardStep.PairFailed,
            });
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return;
        }
        StopTimer();
        IsRetrying = false;
        Person = approval.Person;
        Go(WizardStep.Approved);
        await RedeemAsync(ticket, approval, run);
    }

    /// <summary>Collects the pairing's other agents (one approval for all, sdk/47), then signed in or no access.</summary>
    private async Task RedeemAsync(PairingTicket ticket, PairingApproval approval, CancellationTokenSource run)
    {
        Go(WizardStep.Redeeming);
        var agents = approval.Agents.ToList();
        foreach (var agent in ticket.Agents.Where(a => !agents.Any(s => string.Equals(s.Agent, a, StringComparison.OrdinalIgnoreCase))))
        {
            if (run.IsCancellationRequested)
            {
                return; // cancelled: no more requests for the agents left
            }
            try
            {
                agents.Add(await _server!.RedeemAsync(ticket, agent, run.Token).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
            {
                await ToUi();
                if (IsCurrent(run))
                {
                    Go(WizardStep.PairFailed);
                }
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        await ToUi();
        if (IsCurrent(run))
        {
            SignedInOrNoAccess(approval.Person, agents, run);
        }
    }

    private void SignedInOrNoAccess(SignedInUser? person, IReadOnlyList<AgentSignIn> agents, CancellationTokenSource run)
    {
        Person = person;
        Agents = agents;
        var main = _options.Agents.FirstOrDefault() ?? agents.FirstOrDefault()?.Agent;
        var required = _options.RequiredAgents.Count > 0 ? _options.RequiredAgents : main is null ? Array.Empty<string>() : [main];
        if (required.Any(r => !agents.Any(a => a.Access == AgentAccess.Approved && string.Equals(a.Agent, r, StringComparison.OrdinalIgnoreCase))))
        {
            Go(WizardStep.NoAccess);
            return;
        }
        Result = new ConnectionResult { Mode = ConnectionMode.Server, Server = _serverUrl, Person = person, Agents = agents };
        Go(WizardStep.SignedIn);
        // "Signed in as …" for a moment, then the app's steps and the result.
        _timer = _clock.After(_options.SignedInPause, () =>
        {
            if (IsCurrent(run) && Is(WizardStep.SignedIn))
            {
                _ = AfterConnectAsync(run);
            }
        });
    }

    private void UsePassword()
    {
        CancelRun();
        ClearPairing();
        PasswordFailure = null;
        Go(WizardStep.Password);
    }

    private async Task SignInWithPasswordAsync(string? password)
    {
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrEmpty(password))
        {
            return;
        }
        var run = NewRun();
        SetPasswordBusy(true);
        PairingApproval approval;
        try
        {
            approval = await _server!.SignInWithPasswordAsync(_serverUrl!, _email.Trim(), password!, run.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
        {
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
            SetPasswordBusy(false);
            PasswordFailure = e switch
            {
                FadenstackBadCredentialsException => Wizard.PasswordFailure.BadCredentials,
                FadenstackHttpsRequiredException => Wizard.PasswordFailure.HttpsRequired,
                ServerCertificateException => Wizard.PasswordFailure.CertificateNotTrusted,
                FadenstackUnreachableException or OperationCanceledException => Wizard.PasswordFailure.Unreachable,
                FadenstackApiException { Code: "not_fadenstack" or "redirect" } => Wizard.PasswordFailure.NotFadenstack,
                _ => Wizard.PasswordFailure.Other,
            };
            Go(WizardStep.PasswordError);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return;
        }
        SetPasswordBusy(false);
        SignedInOrNoAccess(approval.Person, approval.Agents, run);
    }

    private void SetPasswordBusy(bool busy)
    {
        _passwordBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
        Refresh();
    }

    private void ChangeServer()
    {
        CancelRun();
        ClearPairing();
        OpenServerAddress();
    }

    private void ClearPairing()
    {
        StopTimer();
        _ticket = null;
        UserCode = null;
        PairingPage = null;
        TimeLeft = TimeSpan.Zero;
        IsRetrying = false;
    }

    private void ScheduleTick(CancellationTokenSource run)
    {
        StopTimer();
        _timer = _clock.After(Tick, () =>
        {
            if (!IsCurrent(run) || !Is(WizardStep.PairCode))
            {
                return;
            }
            UpdateTimeLeft();
            if (_timeLeft <= TimeSpan.Zero)
            {
                CancelRun(); // the code expired here: the wait stops
                ClearPairing();
                Go(WizardStep.Expired);
                return;
            }
            ScheduleTick(run);
        });
    }

    private void UpdateTimeLeft()
    {
        var left = _ticket is null ? TimeSpan.Zero : _ticket.ExpiresAt - _clock.Now;
        TimeLeft = left < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
    }

    // ---- Path B ----

    private void OpenOwnAccept()
    {
        CancelRun();
        Accepted = false;
        SearchConsent = false;
        Go(WizardStep.OwnAccept);
    }

    private async Task AcceptAsync()
    {
        var run = NewRun();
        var record = new AcceptanceRecord(_options.TermsVersion, _clock.Now, _strings().Language, "standalone");
        if (!await StoreAsync(ct => _store.SaveAcceptanceAsync(record, ct), run))
        {
            return; // stays at the acceptance: without its record the person has not accepted
        }
        if (_searchConsent && _options.CanSearchThisComputer)
        {
            Go(WizardStep.OwnSearching);
            IReadOnlyList<LocalEndpoint> found;
            try
            {
                found = await _checker.FindLocalAsync(run.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
            {
                found = Array.Empty<LocalEndpoint>(); // nothing found is no failure: a preset or an address remains
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
            Found = found.Where(f => _options.Policy.AllowsEndpoint(f.BaseUrl)).ToList();
        }
        OpenPick();
    }

    private void OpenPick()
    {
        CancelRun();
        AddressError = AddressProblem.None;
        AppStepRefused = false;
        Go(WizardStep.OwnPick);
    }

    private void SelectPreset(EndpointPreset? preset)
    {
        if (preset is null)
        {
            return;
        }
        SelectedPreset = preset;
        EndpointAddressText = preset.BaseUrl?.AbsoluteUri ?? "";
        NeedsKey = preset.NeedsKey;
    }

    private void SelectFound(LocalEndpoint? found)
    {
        if (found is null)
        {
            return;
        }
        SelectedPreset = found.Preset;
        EndpointAddressText = found.BaseUrl.AbsoluteUri;
        NeedsKey = false;
    }

    private async Task UseEndpointAsync()
    {
        var url = ParseEndpoint(_endpointAddressText);
        if (url is null)
        {
            AddressError = AddressProblem.Invalid;
            return;
        }
        if (!_options.Policy.AllowsEndpoint(url))
        {
            AddressError = AddressProblem.NotAllowed;
            return;
        }
        var run = NewRun();
        var lan = false;
        if (url.Scheme == Uri.UriSchemeHttp)
        {
            EndpointPlace place;
            try
            {
                place = await _checker.PlaceOfAsync(url, run.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                place = EndpointPlace.Internet; // where it points cannot be told: plain http is refused
            }
            await ToUi();
            if (!IsCurrent(run))
            {
                return;
            }
            var forbidden = _options.Policy.PlainHttp == PolicyAccess.Forbidden;
            switch (place)
            {
                case EndpointPlace.ThisComputer when _needsKey && forbidden:
                    AddressError = AddressProblem.PlainHttpForbidden; // the policy forbids a key unencrypted, even here
                    return;
                case EndpointPlace.ThisComputer:
                    break; // a key may go: the check says "unencrypted on this computer" (owner, 14 Nov)
                case EndpointPlace.LocalNetwork when forbidden:
                    AddressError = AddressProblem.PlainHttpForbidden;
                    return;
                case EndpointPlace.LocalNetwork when _needsKey:
                    AddressError = AddressProblem.KeyNeedsHttps; // never a key over plain http to the network (D3)
                    return;
                case EndpointPlace.LocalNetwork:
                    lan = true;
                    break;
                default:
                    AddressError = AddressProblem.HttpsRequired;
                    return;
            }
        }
        _pending = new ModelEndpoint
        {
            BaseUrl = url, Dialect = _selectedPreset?.Dialect ?? EndpointDialect.OpenAICompatible, AllowPlainHttpInLan = lan,
        };
        _keyProvider = null;
        KeySet = false;
        _modelChosen = false;
        if (lan)
        {
            Go(WizardStep.OwnPlainHttp); // the person confirms; no key
            return;
        }
        if (_needsKey)
        {
            Go(WizardStep.OwnKey);
            return;
        }
        await RunCheckAsync();
    }

    private static Uri? ParseEndpoint(string text)
    {
        var trimmed = (text ?? "").Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.UserInfo.Length == 0 && uri.Host.Length > 0
            ? new UriBuilder(uri) { Query = "", Fragment = "" }.Uri
            : null;
    }

    private async Task SubmitKeyAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || _pending is null)
        {
            return;
        }
        var run = NewRun();
        ITokenProvider provider;
        try
        {
            provider = await _keys!.SaveAsync(_pending.BaseUrl, key!.Trim(), run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            return;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return;
        }
        _keyProvider = provider;
        KeySet = true;
        await RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        if (_pending is null)
        {
            return;
        }
        var run = NewRun();
        var endpoint = _pending with { Key = _keyProvider };
        AppStepRefused = false;
        // Every way into a check passes the app's step first, inside the gesture that pressed "Check" (Chrome's permission).
        switch (await RunStepsAsync(WizardPoint.BeforeEndpointCheck,
            new WizardStepContext(WizardPoint.BeforeEndpointCheck, Endpoint: endpoint), WizardStep.BeforeCheck, run))
        {
            case Steps.Stale:
                return;
            case Steps.Stopped:
                AddressError = AddressProblem.None;
                Go(WizardStep.OwnPick);
                return;
        }
        Go(WizardStep.OwnChecking);
        EndpointReport report;
        try
        {
            report = await _checker.CheckAsync(endpoint, run.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
        {
            await ToUi();
            if (IsCurrent(run))
            {
                Problem = EndpointProblem.Failed;
                Go(WizardStep.OwnFailed);
            }
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return;
        }
        Report = report;
        if (!report.Usable)
        {
            Problem = Blocking.FirstOrDefault(report.Problems.Contains) is var first && report.Problems.Contains(first)
                ? first : EndpointProblem.Failed;
            Go(WizardStep.OwnFailed);
            return;
        }
        Problem = null;
        Models = report.Models.Count > 0 ? report.Models : string.IsNullOrEmpty(report.Model) ? Array.Empty<string>() : [report.Model];
        _pending = _pending with { Model = report.Model };
        SelectedModel = report.Model;
        Go(WizardStep.OwnResult);
    }

    private async Task ContinueAsync()
    {
        if (Is(WizardStep.OwnResult))
        {
            if (_modelChosen || _models.Count <= 1)
            {
                await FinishEndpointAsync();
                return;
            }
            Go(WizardStep.OwnModel);
            return;
        }
        if (!Is(WizardStep.OwnModel) || string.IsNullOrEmpty(_selectedModel) || _pending is null)
        {
            return;
        }
        _modelChosen = true;
        if (string.Equals(_selectedModel, _report?.Model, StringComparison.Ordinal))
        {
            await FinishEndpointAsync();
            return;
        }
        // Another model than the one checked: the check runs again for it (tools and context differ by model).
        _pending = _pending with { Model = _selectedModel! };
        await RunCheckAsync();
    }

    private async Task FinishEndpointAsync()
    {
        var run = NewRun();
        Result = new ConnectionResult
        {
            Mode = ConnectionMode.Endpoint,
            Endpoint = _pending! with { Key = _keyProvider },
            Report = _report,
            PresetId = _selectedPreset?.Id,
        };
        Go(WizardStep.OwnDone);
        await AfterConnectAsync(run);
    }

    // ---- Both paths ----

    private async Task AfterConnectAsync(CancellationTokenSource run)
    {
        var result = _result!;
        switch (await RunStepsAsync(WizardPoint.AfterConnect, new WizardStepContext(WizardPoint.AfterConnect, result.Server,
            result.Endpoint, result), WizardStep.AfterConnect, run))
        {
            case Steps.Stale:
                return;
            case Steps.Stopped:
                BackToStart(); // the connection is not used
                return;
        }
        if (!await StoreAsync(ct => _store.SaveAsync(result, ct), run))
        {
            if (IsCurrent(run))
            {
                BackToStart(); // not kept, so not connected (the server's tokens are in the token store already)
                StoreFailed = true;
            }
            return;
        }
        Finish(WizardOutcome.Connected);
    }

    private async Task ConfirmSignOutAsync()
    {
        var run = NewRun();
        var endpoint = _options.Current is { Mode: ConnectionMode.Endpoint, Endpoint: { } e } ? e : null;
        if (await StoreAsync(async ct =>
            {
                await _store.SignOutAsync(_deleteLocalChats, ct).ConfigureAwait(false);
                if (endpoint is not null && _keys is not null)
                {
                    await _keys.RemoveAsync(endpoint.BaseUrl, ct).ConfigureAwait(false);
                }
            }, run))
        {
            Finish(WizardOutcome.SignedOut);
        }
        // Otherwise it stays at the sign-out, with StoreFailed: try again, or cancel.
    }

    /// <summary>
    /// Runs one of the app's store calls; false when it was cancelled or failed. A failure is the app's (its settings could
    /// not be written): <see cref="StoreFailed"/> says so, and the wizard never stays on a busy step for it.
    /// </summary>
    private async Task<bool> StoreAsync(Func<CancellationToken, Task> call, CancellationTokenSource run)
    {
        Exception? failure = null;
        try
        {
            await call(run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            failure = e;
        }
        await ToUi();
        if (!IsCurrent(run))
        {
            return false;
        }
        if (failure is not null)
        {
            StoreFailed = true;
            Refresh();
            return false;
        }
        return true;
    }

    private async Task RetryAsync()
    {
        switch (_step)
        {
            case WizardStep.Unreachable:
                await CheckServerAsync(NewRun());
                break;
            case WizardStep.StartFailed:
            case WizardStep.PairFailed:
                await StartPairingAsync();
                break;
            case WizardStep.OwnFailed:
            case WizardStep.OwnHelp:
                await RunCheckAsync();
                break;
            case WizardStep.PasswordError:
                PasswordFailure = null;
                Go(WizardStep.Password);
                break;
        }
    }

    private bool CanRetry() => _step switch
    {
        WizardStep.Unreachable or WizardStep.PairFailed or WizardStep.OwnFailed or WizardStep.OwnHelp or WizardStep.PasswordError => true,
        WizardStep.StartFailed => _retryAt is null || _clock.Now >= _retryAt,
        _ => false,
    };

    /// <summary>Stops what runs, one step back: a server step to the address, an endpoint step to the pick.</summary>
    private void Cancel()
    {
        switch (_step)
        {
            case WizardStep.AppPrecondition or WizardStep.Checking or WizardStep.PairStarting or WizardStep.PairCode
                or WizardStep.Redeeming or WizardStep.Approved:
                CancelRun();
                ClearPairing();
                OpenServerAddress();
                break;
            case WizardStep.Password when _passwordBusy:
                CancelRun();
                SetPasswordBusy(false);
                break;
            case WizardStep.OwnSearching or WizardStep.BeforeCheck or WizardStep.OwnChecking:
                OpenPick();
                break;
            case WizardStep.SignOut:
                Go(WizardStep.Settings);
                break;
        }
    }

    private bool CanCancel() => _step is WizardStep.AppPrecondition or WizardStep.Checking or WizardStep.PairStarting
        or WizardStep.PairCode or WizardStep.Redeeming or WizardStep.Approved or WizardStep.OwnSearching or WizardStep.BeforeCheck
        or WizardStep.OwnChecking or WizardStep.SignOut
        || (_step == WizardStep.Password && _passwordBusy);

    private void Back()
    {
        switch (_step)
        {
            case WizardStep.ServerAddress or WizardStep.OwnAccept:
                BackToStart();
                break;
            case WizardStep.OwnPick:
                OpenOwnAccept();
                break;
            case WizardStep.OwnPlainHttp or WizardStep.OwnKey or WizardStep.OwnResult or WizardStep.OwnFailed or WizardStep.OwnHelp:
                OpenPick();
                break;
            case WizardStep.OwnModel:
                Go(WizardStep.OwnResult);
                break;
        }
    }

    private bool CanGoBack() => _step switch
    {
        WizardStep.ServerAddress or WizardStep.OwnAccept => _options.Entry == WizardEntry.Settings || CanChooseDemo
            || (CanChooseServer && CanChooseOwnAi),
        WizardStep.OwnPick or WizardStep.OwnPlainHttp or WizardStep.OwnKey or WizardStep.OwnResult or WizardStep.OwnFailed
            or WizardStep.OwnHelp or WizardStep.OwnModel => true,
        _ => false,
    };

    private bool CanChangeServer() => !ServerLocked && _step is WizardStep.Unreachable or WizardStep.CertificateNotTrusted
        or WizardStep.NotFadenstack or WizardStep.StartFailed or WizardStep.ServerTooOld or WizardStep.PairCode
        or WizardStep.Denied or WizardStep.Expired or WizardStep.PairFailed or WizardStep.NoAccess;

    // ---- Plumbing ----

    private enum Steps
    {
        Done,
        Stopped,
        Stale,
    }

    /// <summary>Runs the app's steps at <paramref name="point"/>, showing <paramref name="showing"/> while one runs.</summary>
    private async Task<Steps> RunStepsAsync(WizardPoint point, WizardStepContext context, WizardStep showing, CancellationTokenSource run)
    {
        foreach (var step in _options.Steps.Where(s => s.Point == point))
        {
            Go(showing);
            AppStep = step;
            bool ok;
            try
            {
                ok = await step.RunAsync(context, run.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !run.IsCancellationRequested)
            {
                ok = false; // a step that fails stops the flow, as one that says no
            }
            catch (OperationCanceledException)
            {
                ok = false;
            }
            await ToUi();
            if (!IsCurrent(run))
            {
                return Steps.Stale;
            }
            AppStep = null;
            if (!ok)
            {
                AppStepRefused = true;
                return Steps.Stopped;
            }
        }
        return IsCurrent(run) ? Steps.Done : Steps.Stale;
    }

    private void Finish(WizardOutcome outcome)
    {
        CancelRun();
        StopTimer();
        _retryTimer?.Dispose();
        _retryTimer = null;
        AppStep = null;
        Outcome = outcome;
        Go(WizardStep.Finished);
        Completed?.Invoke(this, outcome);
    }

    private void Go(WizardStep step) => Step = step;

    private bool Is(WizardStep step) => _step == step;

    private bool Is(WizardStep a, WizardStep b) => _step == a || _step == b;

    private CancellationTokenSource NewRun()
    {
        CancelRun();
        StoreFailed = false;
        return _run = new CancellationTokenSource();
    }

    /// <summary>Cancels what runs; its answers become stale. The source is not disposed: an answer may still read its token.</summary>
    private void CancelRun()
    {
        var run = _run;
        _run = null;
        run?.Cancel();
    }

    private bool IsCurrent(CancellationTokenSource run) => ReferenceEquals(run, _run) && !run.IsCancellationRequested;

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Back on the UI thread: at once when already there, else through the dispatcher.</summary>
    private Task ToUi()
    {
        if (_dispatcher.CheckAccess())
        {
            return Task.CompletedTask;
        }
        var back = new TaskCompletionSource<bool>();
        _dispatcher.Post(() => back.SetResult(true)); // the continuation runs inside the posted action, on the UI thread
        return back.Task;
    }

    private void Refresh()
    {
        foreach (var command in new IRelayCommand[]
        {
            ChooseServerCommand, ChooseOwnAiCommand, ChooseDemoCommand, CloseCommand, CancelCommand, BackCommand, SubmitServerCommand,
            ChangeServerCommand, RetryCommand, NewCodeCommand, UsePasswordCommand, BackToCodeCommand, SignInWithPasswordCommand,
            OpenPairingPageCommand, CopyCodeCommand, AcceptCommand, ServerModeInsteadCommand, SelectPresetCommand, SelectFoundCommand,
            UseEndpointCommand, ConfirmPlainHttpCommand, SubmitKeyCommand, EnterKeyCommand, ShowHelpCommand, ContinueCommand,
            ConnectOrganisationCommand, ChangeEndpointCommand, SignOutCommand, ConfirmSignOutCommand,
        })
        {
            command?.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        CancelRun();
        StopTimer();
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

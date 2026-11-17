using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;

namespace Fadenstack.UI.Wizard;

/// <summary>How the wizard is opened (sdk/51 I15 §1).</summary>
public enum WizardEntry
{
    /// <summary>The first start: the app's steps, then the choice.</summary>
    FirstRun,

    /// <summary>
    /// The server is known (after the app restarted onto it, a machine policy's server, a saved one): straight to a pairing
    /// code (<see cref="WizardOptions.Server"/>).
    /// </summary>
    ServerKnown,

    /// <summary>The same, with the "your sign-in ended" banner: the sign-in expired or was revoked.</summary>
    ReSignIn,

    /// <summary>Settings › Connection: change, connect to the organisation, sign out, remove the endpoint.</summary>
    Settings,
}

/// <summary>Where the wizard stands: one value per state of sdk/51 I15 §1-§3; a skin draws one view per step.</summary>
public enum WizardStep
{
    /// <summary>The app's own steps before the choice (<see cref="WizardPoint.BeforeChoice"/>); <see cref="ConnectionWizardViewModel.AppStep"/> is the one running.</summary>
    BeforeChoice,

    /// <summary>"Connect to my organisation", "Use my own AI", the app's demo: what the app offers and the policy allows.</summary>
    Choose,

    /// <summary>Settings › Connection.</summary>
    Settings,

    /// <summary>Sign out, or remove the endpoint: the person confirms, and says whether the chats kept here go too.</summary>
    SignOut,

    // Path A: connect to my organisation.

    /// <summary>The server's address (locked when the machine policy names it).</summary>
    ServerAddress,

    /// <summary>The app's step before the server is checked (<see cref="WizardPoint.BeforeServerCheck"/>).</summary>
    AppPrecondition,

    /// <summary><c>GET /api/health</c>, at most 15 s.</summary>
    Checking,

    Unreachable,

    /// <summary>The server's certificate is not trusted: ask IT. A CA comes only from the policy or the app's settings, never from here.</summary>
    CertificateNotTrusted,

    /// <summary>Something answers, but not like the platform.</summary>
    NotFadenstack,

    /// <summary>The address answers; it is saved (and an app that restarts onto a new server finishes here).</summary>
    ServerAccepted,

    /// <summary>A pairing code is being asked for.</summary>
    PairStarting,

    /// <summary>The first pairing request failed: unreachable, certificate, rate limited, page elsewhere, a server error.</summary>
    StartFailed,

    /// <summary>The server has no pairing (404, 405).</summary>
    ServerTooOld,

    /// <summary>The code is shown; the wizard waits for the approval in the console.</summary>
    PairCode,

    /// <summary>Approved: the agents' sign-ins are collected next.</summary>
    Approved,

    Denied,
    Expired,

    /// <summary>The wait for the approval failed for good.</summary>
    PairFailed,

    /// <summary>The sign-in is collected for each of the app's agents.</summary>
    Redeeming,

    /// <summary>A required agent was refused (<see cref="WizardOptions.RequiredAgents"/>).</summary>
    NoAccess,

    /// <summary>The password sign-in (<see cref="WizardOptions.PasswordFallback"/>).</summary>
    Password,

    PasswordError,

    /// <summary>"Signed in as …", for a moment, then the app gets the result.</summary>
    SignedIn,

    // Path B: use my own AI.

    /// <summary>Where the data goes, the provider's terms, the standalone notice, the consent box (unticked), the search consent.</summary>
    OwnAccept,

    /// <summary>Looking for model servers on this computer (only with the person's consent).</summary>
    OwnSearching,

    /// <summary>A server found here, a preset, or an address typed by hand.</summary>
    OwnPick,

    /// <summary>Plain http to the local network: the person confirms; never with a key (D3).</summary>
    OwnPlainHttp,

    /// <summary>The key, straight into the key store.</summary>
    OwnKey,

    /// <summary>The app's step before every check (<see cref="WizardPoint.BeforeEndpointCheck"/>; Chrome's host permission).</summary>
    BeforeCheck,

    /// <summary>The endpoint check (F03).</summary>
    OwnChecking,

    /// <summary>The check found the endpoint unusable (<see cref="ConnectionWizardViewModel.Problem"/>).</summary>
    OwnFailed,

    /// <summary>The endpoint refused this origin (403): how to allow it (Ollama's <c>OLLAMA_ORIGINS</c>).</summary>
    OwnHelp,

    /// <summary>What the check found, in plain words.</summary>
    OwnResult,

    /// <summary>The model to use.</summary>
    OwnModel,

    /// <summary>The endpoint is ready.</summary>
    OwnDone,

    /// <summary>The app's steps after connecting (<see cref="WizardPoint.AfterConnect"/>).</summary>
    AfterConnect,

    /// <summary>The wizard is over: <see cref="ConnectionWizardViewModel.Outcome"/> says how.</summary>
    Finished,
}

/// <summary>How the wizard ended.</summary>
public enum WizardOutcome
{
    /// <summary>Not ended yet.</summary>
    None,

    /// <summary>Connected: <see cref="ConnectionWizardViewModel.Result"/> is saved; the app builds the agent (<c>UseServer</c> or <c>UseEndpoint</c>).</summary>
    Connected,

    /// <summary>The person chose the app's demo.</summary>
    Demo,

    /// <summary>The address answered and is saved; the app restarts onto it and opens the wizard as <see cref="WizardEntry.ServerKnown"/>.</summary>
    RestartOnServer,

    /// <summary>Signed out, or the endpoint removed: the app cleared its tokens or key.</summary>
    SignedOut,

    /// <summary>Closed without a connection.</summary>
    Closed,
}

/// <summary>Where the app's own steps run (<see cref="IWizardStep"/>).</summary>
public enum WizardPoint
{
    /// <summary>Before the choice, on the first start (the desktop's licence).</summary>
    BeforeChoice,

    /// <summary>Before the server is checked (a permission the app needs to reach it).</summary>
    BeforeServerCheck,

    /// <summary>Before every endpoint check, inside the gesture that pressed "Check" (Chrome's host permission).</summary>
    BeforeEndpointCheck,

    /// <summary>After connecting, before the result is saved.</summary>
    AfterConnect,
}

/// <summary>
/// One of the app's own steps (F15): the desktop's licence, Chrome's host permission. It runs at its <see cref="Point"/>; a
/// skin may draw it while <see cref="ConnectionWizardViewModel.AppStep"/> is this step.
/// </summary>
public interface IWizardStep
{
    WizardPoint Point { get; }

    /// <summary>
    /// Runs the step. True goes on; false stops the flow: before the choice it closes the wizard, before a check it goes
    /// back to the address, after connecting nothing is saved and the wizard goes back to its start.
    /// </summary>
    Task<bool> RunAsync(WizardStepContext context, CancellationToken cancellationToken);
}

/// <summary>What an app step may look at.</summary>
/// <param name="Point">Where it runs.</param>
/// <param name="Server">The server's address, on path A.</param>
/// <param name="Endpoint">The endpoint, on path B (its key is a provider over the key store, never the key).</param>
/// <param name="Result">The connection, after connecting.</param>
public sealed record WizardStepContext(WizardPoint Point, Uri? Server = null, ModelEndpoint? Endpoint = null,
    ConnectionResult? Result = null);

/// <summary>Which way the app is connected.</summary>
public enum ConnectionMode
{
    /// <summary>To the organisation's Fadenstack server.</summary>
    Server,

    /// <summary>To the person's own model endpoint (standalone).</summary>
    Endpoint,
}

/// <summary>
/// What the wizard connected (F15): the app saves it (<see cref="IConnectionStore"/>) and builds the agent from it. Keys
/// and tokens are never in it; they are in the key and token stores.
/// </summary>
public sealed record ConnectionResult
{
    public ConnectionMode Mode { get; init; }

    /// <summary>The server, on path A.</summary>
    public Uri? Server { get; init; }

    /// <summary>Who signed in, when the server said.</summary>
    public SignedInUser? Person { get; init; }

    /// <summary>Each of the app's agents and whether it is signed in.</summary>
    public IReadOnlyList<AgentSignIn> Agents { get; init; } = Array.Empty<AgentSignIn>();

    /// <summary>The endpoint, on path B, with the model picked; its <see cref="ModelEndpoint.Key"/> reads the key store.</summary>
    public ModelEndpoint? Endpoint { get; init; }

    /// <summary>The check's report, kept with the connection (the records cite it, F10).</summary>
    public EndpointReport? Report { get; init; }

    /// <summary>The preset it came from (<see cref="EndpointPreset.Id"/>), if any.</summary>
    public string? PresetId { get; init; }
}

/// <summary>The person's acceptance of the own-AI terms, kept on the device (F15): what, when, in which language, for which mode.</summary>
public sealed record AcceptanceRecord(string TermsVersion, DateTimeOffset AcceptedAt, string Language, string Mode);

/// <summary>
/// Where the app keeps the connection (F15, layer 2): the desktop's <c>settings.json</c>, Office's settings, Chrome's
/// storage. Never a key or token.
/// </summary>
public interface IConnectionStore
{
    /// <summary>Saves the server the person typed once it answers (before the pairing, so an app that restarts finds it).</summary>
    Task SaveServerAsync(Uri server, CancellationToken cancellationToken);

    /// <summary>Saves the finished connection.</summary>
    Task SaveAsync(ConnectionResult result, CancellationToken cancellationToken);

    /// <summary>Keeps the acceptance record.</summary>
    Task SaveAcceptanceAsync(AcceptanceRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Signs out (path A: revoke at the server as far as it can, delete the tokens) or removes the endpoint (its key), and
    /// forgets the connection; the chats kept on this computer only with <paramref name="deleteLocalChats"/>.
    /// </summary>
    Task SignOutAsync(bool deleteLocalChats, CancellationToken cancellationToken);
}

/// <summary>
/// The key store for endpoint keys (DPAPI, Keychain, libsecret; Chrome's WebCrypto key). The wizard hands the key over at
/// once and keeps only "a key is set".
/// </summary>
public interface IEndpointKeyStore
{
    /// <summary>Saves the key for the endpoint; the provider reads it from the store when a request needs it.</summary>
    Task<ITokenProvider> SaveAsync(Uri endpoint, string key, CancellationToken cancellationToken);

    /// <summary>Removes the endpoint's key.</summary>
    Task RemoveAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>Opens a page in the person's browser (http and https only).</summary>
public interface ILinkOpener
{
    bool TryOpen(Uri address);
}

/// <summary>The wizard's time: now, and a timer (the code's countdown, the "signed in" moment). Tests move it by hand.</summary>
public interface IWizardClock
{
    DateTimeOffset Now { get; }

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>; disposing the result stops it.</summary>
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>A pairing the server started, as the wizard shows it; <see cref="Handle"/> keeps what the client polls with (the secret).</summary>
/// <param name="UserCode">The code to show, e.g. <c>BDWP-HTQX</c>.</param>
/// <param name="VerificationUri">The console page.</param>
/// <param name="VerificationUriComplete">The page with the code filled in: what the browser opens.</param>
/// <param name="ExpiresAt">When the code expires, on this device's clock.</param>
/// <param name="Agents">The agents the pairing asked for, the main one first.</param>
/// <param name="Handle">The client's own object (the SDK's <see cref="PairingStart"/>).</param>
public sealed record PairingTicket(string UserCode, Uri VerificationUri, Uri VerificationUriComplete, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Agents, object? Handle = null)
{
    /// <summary>No code: only that this is a pairing and when it ends.</summary>
    public override string ToString() => $"PairingTicket(expires {ExpiresAt:u})";
}

/// <summary>Whether one of the app's agents is signed in.</summary>
public enum AgentAccess
{
    /// <summary>Signed in: its tokens are in the token store.</summary>
    Approved,

    /// <summary>The person did not approve it, or the server refused it.</summary>
    Refused,

    /// <summary>An older server signs in one agent per pairing: this one needs a pairing of its own.</summary>
    Left,
}

/// <summary>One agent's sign-in.</summary>
public sealed record AgentSignIn(string Agent, AgentAccess Access);

/// <summary>An approval: who approved it, and the agents collected so far (the main one).</summary>
public sealed record PairingApproval(SignedInUser? Person, IReadOnlyList<AgentSignIn> Agents);

/// <summary>
/// The server side of path A, as the wizard drives it (<see cref="ServerSignIn"/> over the SDK's pairing, password login and
/// health check; tests pass a fake). The failures are the SDK's typed errors: <see cref="FadenstackUnreachableException"/>,
/// <see cref="ServerCertificateException"/>, <see cref="FadenstackRateLimitedException"/>,
/// <see cref="FadenstackPairingUnsupportedException"/>, <see cref="FadenstackPairingPageElsewhereException"/>,
/// <see cref="FadenstackPairingDeniedException"/>, <see cref="FadenstackPairingExpiredException"/>,
/// <see cref="FadenstackBadCredentialsException"/>, <see cref="FadenstackHttpsRequiredException"/>, and a
/// <see cref="FadenstackApiException"/> with code <c>not_fadenstack</c>.
/// </summary>
public interface IServerSignIn
{
    /// <summary><c>GET {server}/api/health</c>: 200 and JSON within 15 s, or a typed error.</summary>
    Task CheckAsync(Uri server, CancellationToken cancellationToken);

    /// <summary>Asks for a pairing code for the app's agents.</summary>
    Task<PairingTicket> StartAsync(Uri server, CancellationToken cancellationToken);

    /// <summary>Waits for the approval and collects the main agent; tells <paramref name="progress"/> when it is retrying.</summary>
    Task<PairingApproval> WaitAsync(PairingTicket ticket, IProgress<bool> progress, CancellationToken cancellationToken);

    /// <summary>Collects one more of the pairing's agents after the approval.</summary>
    Task<AgentSignIn> RedeemAsync(PairingTicket ticket, string agent, CancellationToken cancellationToken);

    /// <summary>The password sign-in (an older server, or the person's choice): every agent of the app.</summary>
    Task<PairingApproval> SignInWithPasswordAsync(Uri server, string email, string password, CancellationToken cancellationToken);
}

/// <summary>The endpoint side of path B (<see cref="EndpointChecker"/> over <see cref="EndpointCheck"/>; tests pass a fake).</summary>
public interface IEndpointChecker
{
    /// <summary>Model servers on this computer; only called after the person agreed.</summary>
    Task<IReadOnlyList<LocalEndpoint>> FindLocalAsync(CancellationToken cancellationToken);

    /// <summary>Where the address points, decided on the resolved addresses.</summary>
    Task<EndpointPlace> PlaceOfAsync(Uri baseUrl, CancellationToken cancellationToken);

    /// <summary>The endpoint check (F03). <paramref name="options"/> add to the checker's own (the wizard asks for the picture probe).</summary>
    Task<EndpointReport> CheckAsync(ModelEndpoint endpoint, CancellationToken cancellationToken, EndpointCheckOptions? options = null);
}

/// <summary>What the app declares, and what the wizard needs to run (F15).</summary>
public sealed record WizardOptions
{
    public WizardEntry Entry { get; init; } = WizardEntry.FirstRun;

    /// <summary>The app offers "Connect to my organisation".</summary>
    public bool OffersServer { get; init; } = true;

    /// <summary>The app offers "Use my own AI".</summary>
    public bool OffersOwnAi { get; init; } = true;

    /// <summary>The app has a demo.</summary>
    public bool OffersDemo { get; init; }

    /// <summary>The app's name, for the texts.</summary>
    public string AppName { get; init; } = "";

    /// <summary>
    /// The app's trusted CA (DER bytes), from the machine policy or the app's settings; never from a click in the wizard. It goes
    /// to every endpoint the wizard checks and builds (<see cref="ModelEndpoint.TrustedCaCertificate"/>), as the app passes it to
    /// <see cref="ServerSignIn"/> for the server (F15). The wizard offers no CA trust in its UI.
    /// </summary>
    public byte[]? TrustedCaCertificate { get; init; }

    /// <summary>The app's licence and privacy texts.</summary>
    public Uri? LicenceLink { get; init; }

    public Uri? PrivacyLink { get; init; }

    /// <summary>The machine policy (F09): its server opens path A with the address locked; <c>standalone: forbidden</c> hides own AI; <c>endpoints</c> narrows the picks; <c>plainHttp</c>.</summary>
    public MachinePolicy Policy { get; init; } = MachinePolicy.None;

    /// <summary>The server, for <see cref="WizardEntry.ServerKnown"/> and <see cref="WizardEntry.ReSignIn"/>; a policy's server wins.</summary>
    public Uri? Server { get; init; }

    /// <summary>The connection the app has now, for Settings.</summary>
    public ConnectionResult? Current { get; init; }

    /// <summary>
    /// The agents the app signs in with one approval, the main one first (Office's four). Required to sign in:
    /// <see cref="RequiredAgents"/>.
    /// </summary>
    public IReadOnlyList<string> Agents { get; init; } = Array.Empty<string>();

    /// <summary>The agents that must be signed in, or <see cref="WizardStep.NoAccess"/>; the main agent when empty.</summary>
    public IReadOnlyList<string> RequiredAgents { get; init; } = Array.Empty<string>();

    /// <summary>Offer the password sign-in (Office and Chrome; not the desktop).</summary>
    public bool PasswordFallback { get; init; }

    /// <summary>The app restarts onto a new server (the desktop): the wizard finishes at <see cref="WizardStep.ServerAccepted"/>.</summary>
    public bool RestartsOnNewServer { get; init; }

    /// <summary>The app can search this computer for model servers (not in a browser).</summary>
    public bool CanSearchThisComputer { get; init; } = true;

    /// <summary>The version of the own-AI terms the person accepts, kept in the acceptance record.</summary>
    public string TermsVersion { get; init; } = "";

    /// <summary>The app's own steps.</summary>
    public IReadOnlyList<IWizardStep> Steps { get; init; } = Array.Empty<IWizardStep>();

    /// <summary>How long "Signed in as …" shows.</summary>
    public TimeSpan SignedInPause { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The check's options (the context the agent needs).</summary>
    public EndpointCheckOptions? CheckOptions { get; init; }
}

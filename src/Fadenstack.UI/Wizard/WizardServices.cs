using System.Net.Http;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.Http;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Wizard;

/// <summary>
/// The server side of the wizard over the SDK (<see cref="IServerSignIn"/>): <see cref="ServerCheck"/>, one
/// <see cref="AgentPairing"/> for all of the app's agents (sdk/47: one approval), and the password sign-in
/// (<see cref="PasswordLogin"/>, then <see cref="AgentTokenProvider.SignInAsync"/> per agent). Tokens go to the token store;
/// the pairing code stays inside the SDK's start object; the password is passed once and kept nowhere.
/// </summary>
public sealed class ServerSignIn : IServerSignIn
{
    private readonly ITokenStore _store;
    private readonly IReadOnlyList<string> _agents;
    private readonly string _deviceLabel;
    private readonly PairingClient _client;
    private readonly MachinePolicy _policy;
    private readonly byte[]? _trustedCa;
    private readonly Func<Uri, HttpClient>? _http;

    /// <param name="store">The token store the app's <see cref="AgentTokenProvider"/>s use.</param>
    /// <param name="agents">The app's agents, the main one first (at most <see cref="AgentPairing.MaxAgents"/>).</param>
    /// <param name="deviceLabel">Names the sign-in in the person's list, e.g. "Word assistant on SACHI-PC".</param>
    /// <param name="client">What the app says about itself on the approval page.</param>
    /// <param name="policy">The machine policy: plain http goes only to its own http server (or this machine).</param>
    /// <param name="trustedCaCertificate">The organisation's CA, from the policy or the app's settings; never from the wizard.</param>
    /// <param name="http">A client per server (tests, a host's proxy); the SDK's transport, which follows no redirect, when null.</param>
    public ServerSignIn(ITokenStore store, IReadOnlyList<string> agents, string deviceLabel, PairingClient client,
        MachinePolicy? policy = null, byte[]? trustedCaCertificate = null, Func<Uri, HttpClient>? http = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (agents is null || agents.Count == 0)
        {
            throw new ArgumentException("Name the app's agents, the main one first.", nameof(agents));
        }
        _agents = agents;
        _deviceLabel = deviceLabel ?? "";
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _policy = policy ?? MachinePolicy.None;
        _trustedCa = trustedCaCertificate;
        _http = http;
    }

    /// <summary>Plain http only to the server the machine policy names (or this machine, which the SDK allows anyway).</summary>
    private bool AllowsHttp(Uri server) =>
        _policy.Server is { } named && named.Scheme == Uri.UriSchemeHttp && Same(named, server);

    private static bool Same(Uri a, Uri b) =>
        string.Equals(a.GetLeftPart(UriPartial.Path).TrimEnd('/'), b.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private HttpClient Http(Uri server) =>
        _http?.Invoke(server) ?? new HttpClient(FadenstackTransport.CreateHandler(new FadenstackClientOptions
        {
            ServerUrl = server, AllowInsecureHttp = true, TrustedCaCertificate = _trustedCa,
        }));

    public Task CheckAsync(Uri server, CancellationToken cancellationToken) =>
        ServerCheck.CheckAsync(server, _http?.Invoke(server), AllowsHttp(server), _trustedCa, cancellationToken);

    public async Task<PairingTicket> StartAsync(Uri server, CancellationToken cancellationToken)
    {
        var pairing = new AgentPairing(server, Http(server), _store, allowInsecureHttp: AllowsHttp(server));
        var start = await pairing.StartAsync(_agents[0], _deviceLabel, _client, _agents.Skip(1), cancellationToken).ConfigureAwait(false);
        return new PairingTicket(start.UserCode, start.VerificationUri, start.VerificationUriComplete, start.ExpiresAt,
            start.RequestedAgents, new Pairing(pairing, start));
    }

    public async Task<PairingApproval> WaitAsync(PairingTicket ticket, IProgress<bool> progress, CancellationToken cancellationToken)
    {
        var (pairing, start) = Of(ticket);
        try
        {
            var result = await pairing.WaitAsync(start, new Retrying(progress), cancellationToken).ConfigureAwait(false);
            return new PairingApproval(result.SignedInUser, [new AgentSignIn(result.AgentSlug, AgentAccess.Approved)]);
        }
        catch (FadenstackPairingAgentException)
        {
            // The person approved others but not the main agent; the first approved other one was collected to tell this
            // from an unknown code.
            var left = start.AgentsLeft;
            var agents = start.RequestedAgents.Select(a => new AgentSignIn(a,
                string.Equals(a, start.RequestedAgents[0], StringComparison.OrdinalIgnoreCase) ? AgentAccess.Refused
                : left.Contains(a, StringComparer.OrdinalIgnoreCase) ? AgentAccess.Left : AgentAccess.Approved))
                .Where(a => a.Access != AgentAccess.Left).ToList();
            return new PairingApproval(null, agents);
        }
    }

    public async Task<AgentSignIn> RedeemAsync(PairingTicket ticket, string agent, CancellationToken cancellationToken)
    {
        var (pairing, start) = Of(ticket);
        if (start.Agents.Count == 0)
        {
            return new AgentSignIn(agent, AgentAccess.Left); // an older server: one agent per pairing
        }
        try
        {
            await pairing.RedeemAsync(start, agent, cancellationToken).ConfigureAwait(false);
            return new AgentSignIn(agent, AgentAccess.Approved);
        }
        catch (FadenstackPairingAgentException)
        {
            return new AgentSignIn(agent, AgentAccess.Refused);
        }
    }

    public async Task<PairingApproval> SignInWithPasswordAsync(Uri server, string email, string password,
        CancellationToken cancellationToken)
    {
        using var http = Http(server);
        var loginToken = await PasswordLogin.LoginAsync(server, email, password, http, AllowsHttp(server), cancellationToken)
            .ConfigureAwait(false);
        SignedInUser? person = null;
        var agents = new List<AgentSignIn>();
        foreach (var agent in _agents)
        {
            var provider = new AgentTokenProvider(server, agent, _store, http, allowInsecureHttp: AllowsHttp(server),
                trustedCaCertificate: _trustedCa);
            try
            {
                var tokens = await provider.SignInAsync(loginToken, _deviceLabel, cancellationToken).ConfigureAwait(false);
                person ??= tokens.User;
                agents.Add(new AgentSignIn(agent, AgentAccess.Approved));
            }
            catch (FadenstackPermissionException)
            {
                agents.Add(new AgentSignIn(agent, AgentAccess.Refused));
            }
        }
        return new PairingApproval(person, agents);
    }

    private static (AgentPairing Pairing, PairingStart Start) Of(PairingTicket ticket) =>
        ticket.Handle is Pairing p ? (p.Client, p.Start)
        : throw new ArgumentException("The ticket is not this sign-in's.", nameof(ticket));

    private sealed record Pairing(AgentPairing Client, PairingStart Start);

    private sealed class Retrying(IProgress<bool> progress) : IProgress<PairingProgress>
    {
        public void Report(PairingProgress value) => progress.Report(value.State == PairingState.Retrying);
    }
}

/// <summary>The endpoint side of the wizard over <see cref="EndpointCheck"/>.</summary>
public sealed class EndpointChecker : IEndpointChecker
{
    private readonly EndpointCheckOptions? _options;

    /// <param name="options">The check's options (the context the agent needs).</param>
    public EndpointChecker(EndpointCheckOptions? options = null)
    {
        _options = options;
    }

    public Task<IReadOnlyList<LocalEndpoint>> FindLocalAsync(CancellationToken cancellationToken) =>
        EndpointCheck.FindLocalAsync(personConsented: true, cancellationToken: cancellationToken);

    public Task<EndpointPlace> PlaceOfAsync(Uri baseUrl, CancellationToken cancellationToken) =>
        EndpointCheck.PlaceOfAsync(baseUrl, cancellationToken);

    public Task<EndpointReport> CheckAsync(ModelEndpoint endpoint, CancellationToken cancellationToken, EndpointCheckOptions? options = null) =>
        new EndpointCheck(endpoint).RunAsync(options is null ? _options : (_options ?? new EndpointCheckOptions()) with
        {
            ProbeVision = _options?.ProbeVision == true || options.ProbeVision,
        }, cancellationToken);
}

/// <summary>The wizard's clock on this computer: timers that run their action on the UI thread.</summary>
public sealed class SystemWizardClock : IWizardClock
{
    private readonly IUiDispatcher _dispatcher;

    public SystemWizardClock(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new Timer(_ => _dispatcher.Post(action), null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

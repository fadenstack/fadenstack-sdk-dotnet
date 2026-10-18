using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Resources;
using Microsoft.Extensions.AI;

namespace Fadenstack;

/// <summary>
/// The connection to a Fadenstack server. With an agent (<see cref="FadenstackClientOptions.Agent"/>) it is that
/// agent's client -- one agent per client -- and <see cref="Chat"/> calls the agent's endpoint; without one it
/// uses the plain <c>/v1</c> routes with an API token.
/// </summary>
public sealed class FadenstackClient : IDisposable
{
    public FadenstackClient(FadenstackClientOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Transport = new FadenstackTransport(options);
        Chat = new FadenstackChatClient(Transport, options);
        Agent = options.Agent is { } agent ? new AgentResource(Transport, agent) : null;
        Sessions = new SessionsResource(Transport);
        Projects = new ProjectsResource(Transport);
        Tools = new ToolsResource(Transport);
        Memory = new MemoryResource(Transport);
        Attachments = new AttachmentsResource(Transport);
        Services = new ServicesResource(Transport);
        Provenance = new ProvenanceResource(Transport);
    }

    public FadenstackClientOptions Options { get; }

    /// <summary>Authenticated requests, for extensions that add their own resources.</summary>
    public FadenstackTransport Transport { get; }

    /// <summary>The gateway as an <see cref="IChatClient"/>: works with anything built on Microsoft.Extensions.AI.</summary>
    public IChatClient Chat { get; }

    /// <summary>The client's agent (profile, events, approvals); null without an agent.</summary>
    public AgentResource? Agent { get; }

    /// <summary>Server sessions (web chat and API); an agent's sessions are local (Fadenstack.Agents).</summary>
    public SessionsResource Sessions { get; }

    public ProjectsResource Projects { get; }

    public ToolsResource Tools { get; }

    public MemoryResource Memory { get; }

    public AttachmentsResource Attachments { get; }

    public ServicesResource Services { get; }

    /// <summary>What anyone checking this installation's marks needs (the anchors); an agent's marking is <see cref="AgentResource.Provenance"/>.</summary>
    public ProvenanceResource Provenance { get; }

    /// <summary>The server's edition and features (<c>GET /v1/features</c>).</summary>
    public Task<ServerFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default) =>
        Transport.GetAsync<ServerFeatures>(Transport.Gateway("v1/features"), cancellationToken);

    /// <summary>
    /// A client for <paramref name="agent"/> that keeps its sign-in in <paramref name="store"/>
    /// (<see cref="FileTokenStore"/> by default). Sign in once with <see cref="AgentTokenProvider.SignInAsync"/>.
    /// </summary>
    public static FadenstackClient ForAgent(Uri serverUrl, string agent, out AgentTokenProvider tokens,
        ITokenStore? store = null, Action<FadenstackClientOptions>? configure = null)
    {
        var options = new FadenstackClientOptions { ServerUrl = serverUrl, Agent = agent };
        configure?.Invoke(options);
        tokens = new AgentTokenProvider(options.ApiUrl ?? serverUrl, agent, store ?? new FileTokenStore(),
            options.HttpClient ?? new System.Net.Http.HttpClient(FadenstackTransport.CreateHandler(options)),
            allowInsecureHttp: options.AllowInsecureHttp);
        options.Credentials = tokens;
        return new FadenstackClient(options);
    }

    public void Dispose() => Transport.Dispose();
}

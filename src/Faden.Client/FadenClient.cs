using Faden.Auth;
using Faden.Chat;
using Faden.Http;
using Faden.Models;
using Faden.Resources;
using Microsoft.Extensions.AI;

namespace Faden;

/// <summary>
/// The connection to a Fadenstack server. With an agent (<see cref="FadenClientOptions.Agent"/>) it is that
/// agent's client -- one agent per client -- and <see cref="Chat"/> calls the agent's endpoint; without one it
/// uses the plain <c>/v1</c> routes with an API token.
/// </summary>
public sealed class FadenClient : IDisposable
{
    public FadenClient(FadenClientOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Transport = new FadenTransport(options);
        Chat = new FadenChatClient(Transport, options);
        Agent = options.Agent is { } agent ? new AgentResource(Transport, agent) : null;
        Sessions = new SessionsResource(Transport);
        Projects = new ProjectsResource(Transport);
        Tools = new ToolsResource(Transport);
        Memory = new MemoryResource(Transport);
        Attachments = new AttachmentsResource(Transport);
        Services = new ServicesResource(Transport);
    }

    public FadenClientOptions Options { get; }

    /// <summary>Authenticated requests, for extensions that add their own resources (<c>Faden.Enterprise</c>).</summary>
    public FadenTransport Transport { get; }

    /// <summary>The gateway as an <see cref="IChatClient"/>: works with anything built on Microsoft.Extensions.AI.</summary>
    public IChatClient Chat { get; }

    /// <summary>The client's agent (profile, events, approvals); null without an agent.</summary>
    public AgentResource? Agent { get; }

    /// <summary>Server sessions (web chat and API); an agent's sessions are local (Faden.Agents).</summary>
    public SessionsResource Sessions { get; }

    public ProjectsResource Projects { get; }

    public ToolsResource Tools { get; }

    public MemoryResource Memory { get; }

    public AttachmentsResource Attachments { get; }

    public ServicesResource Services { get; }

    /// <summary>The server's edition and features (<c>GET /v1/features</c>).</summary>
    public Task<ServerFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default) =>
        Transport.GetAsync<ServerFeatures>(Transport.Gateway("v1/features"), cancellationToken);

    /// <summary>
    /// A client for <paramref name="agent"/> that keeps its sign-in in <paramref name="store"/>
    /// (<see cref="FileTokenStore"/> by default). Sign in once with <see cref="AgentTokenProvider.SignInAsync"/>.
    /// </summary>
    public static FadenClient ForAgent(Uri serverUrl, string agent, out AgentTokenProvider tokens,
        ITokenStore? store = null, Action<FadenClientOptions>? configure = null)
    {
        var options = new FadenClientOptions { ServerUrl = serverUrl, Agent = agent };
        configure?.Invoke(options);
        tokens = new AgentTokenProvider(options.ApiUrl ?? serverUrl, agent, store ?? new FileTokenStore(),
            options.HttpClient ?? new System.Net.Http.HttpClient(FadenTransport.CreateHandler(options)));
        options.Credentials = tokens;
        return new FadenClient(options);
    }

    public void Dispose() => Transport.Dispose();
}

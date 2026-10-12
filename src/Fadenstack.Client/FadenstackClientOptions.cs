using System.Net.Http;
using Fadenstack.Auth;
using Fadenstack.Tools;

namespace Fadenstack;

/// <summary>How a <see cref="FadenstackClient"/> reaches its server, and as whom.</summary>
public sealed class FadenstackClientOptions
{
    /// <summary>
    /// The Fadenstack server, e.g. <c>https://ai.example.internal</c>. The gateway's <c>/v1</c> routes are
    /// under it; so is the backend's <c>/api</c>, unless <see cref="ApiUrl"/> says otherwise.
    /// </summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>The backend (sign-in, agent tokens), when it is not at <see cref="ServerUrl"/>.</summary>
    public Uri? ApiUrl { get; set; }

    /// <summary>
    /// The agent this client works for (its slug or id): chat goes to <c>/v1/agents/{agent}/...</c>. One
    /// agent per client. Without one, the client uses the plain <c>/v1</c> routes with an API token.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>Where access tokens come from: an API token, or an agent sign-in (<see cref="AgentTokenProvider"/>).</summary>
    public ITokenProvider? Credentials { get; set; }

    /// <summary>The model for the plain routes; an agent's model is the agent's, whatever this says.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// The tool mode sent with each request (<c>X-Faden-Tool-Mode</c>) unless a request says otherwise.
    /// Every session starts in <see cref="ToolMode.Ask"/>.
    /// </summary>
    public ToolMode ToolMode { get; set; } = ToolMode.Ask;

    /// <summary>Ask the gateway for its events (<c>X-Faden-Events: ag-ui</c>): tool rounds, approvals, notices.</summary>
    public bool GatewayEvents { get; set; } = true;

    /// <summary>
    /// Allow <c>http://</c>. Off: agents use TLS. Only for a development server on this machine.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Trust this CA (PEM or DER) for the server's certificate, in addition to the OS store -- for an
    /// install whose own CA is not deployed to the machines.
    /// </summary>
    public byte[]? TrustedCaCertificate { get; set; }

    /// <summary>An <see cref="HttpClient"/> to use instead of the client's own (tests, proxies, handlers).</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>How long one request may take; streams are not cut by it once they started.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    internal Uri RequireServerUrl()
    {
        if (ServerUrl is null)
        {
            throw new ArgumentException("FadenstackClientOptions.ServerUrl is required.");
        }
        if (ServerUrl.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
        {
            throw new ArgumentException(
                "Fadenstack is reached over https. Set AllowInsecureHttp only for a development server.");
        }
        return ServerUrl;
    }
}

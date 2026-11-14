using System.Net.Http;
using Fadenstack.Auth;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Endpoints;

/// <summary>
/// The person's own model endpoint, for standalone mode (sdk/51 F03): any server that speaks the OpenAI chat completions
/// API, such as Ollama, LM Studio, vLLM, an OpenAI-compatible cloud. <c>FadenstackAgent.CreateBuilder().UseEndpoint(...)</c>.
/// </summary>
/// <remarks>
/// The transport rules (OWASP ASVS 5.0 V12, V13): https, or plain http to this device (loopback); plain http to a private
/// network only with <see cref="AllowPlainHttpInLan"/>; a key never over plain http; no redirect to another origin.
/// </remarks>
public sealed record ModelEndpoint
{
    /// <summary>The base, including <c>/v1</c> (the OpenAI convention): <c>http://localhost:11434/v1</c>.</summary>
    public Uri BaseUrl { get; init; } = null!;

    /// <summary>The model's id on that server (<c>qwen3:14b</c>); <c>ChatOptions.ModelId</c> overrides it per request.</summary>
    public string Model { get; init; } = "";

    /// <summary>
    /// Where the key comes from (the OS key store, DPAPI in Office, the app's own store); null for a server without one. It
    /// never goes over plain http.
    /// </summary>
    public ITokenProvider? Key { get; init; }

    /// <summary>What the server expects where servers differ (a preset or the check gives it).</summary>
    public EndpointDialect Dialect { get; init; } = EndpointDialect.OpenAICompatible;

    /// <summary>
    /// Plain http to a private network (RFC 1918, link-local, fc00::/7), after the person opted in for this endpoint and saw
    /// the warning (D3). Never with a key; a machine policy can forbid it (F09).
    /// </summary>
    public bool AllowPlainHttpInLan { get; init; }

    /// <summary>A CA (PEM or DER) to trust for this endpoint's certificate, besides the OS store; per connection, never process-wide.</summary>
    public byte[]? TrustedCaCertificate { get; init; }

    /// <summary>How long a request may take; for a streamed answer, the wait for its headers.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a streamed answer may send nothing at all before it is given up (a stalled server).</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The handler requests go through instead of the SDK's own (tests, a host's proxy). The SDK still applies the transport
    /// rules and follows no redirect itself; with its own handler the host decides TLS and must not follow redirects.
    /// </summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Where requests are logged (method, route, status, duration; never content or the key). Default: nowhere.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>The base with one trailing slash; throws when it is not an absolute http(s) URL without query, fragment or user info.</summary>
    internal Uri RequireBase() =>
        BaseUrl is null ? throw new ArgumentException("ModelEndpoint.BaseUrl is required.") : EndpointUrl.Base(BaseUrl);
}

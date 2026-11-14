using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fadenstack.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Endpoints;

/// <summary>
/// Requests to a person's own endpoint, under the transport rules (sdk/51 F03, D3; OWASP ASVS 5.0 V12 secure communication,
/// V13 configuration and secrets):
/// <list type="bullet">
/// <item>https, or plain http to this device; plain http to a private network only with the endpoint's opt-in; a key never
/// over plain http, so a request that would carry one is refused before anything is sent;</item>
/// <item>no automatic redirect: one to the same origin is followed (at most three), any other is refused, so the key and the
/// request's body never leave the origin they were meant for;</item>
/// <item>a custom CA for this connection only; TLS 1.2 or 1.3 on .NET Framework;</item>
/// <item>errors as typed exceptions: a refused key, a refused origin, no such model, the context length, not compatible,
/// unreachable, the certificate, the rate limit (a <see cref="FadenstackRateLimitedException"/>).</item>
/// </list>
/// </summary>
internal sealed class EndpointTransport : IDisposable
{
    /// <summary>The longest answer body read whole (not streamed), in characters.</summary>
    internal const int MaxBodyChars = 32 * 1024 * 1024;

    private const int MaxRedirects = 3;

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public EndpointTransport(ModelEndpoint endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Base = endpoint.RequireBase();
        _log = (endpoint.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger(Log.Category);
        var handler = endpoint.Handler ?? CreateHandler(endpoint);
        _http = new HttpClient(handler, disposeHandler: endpoint.Handler is null) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public ModelEndpoint Endpoint { get; }

    /// <summary>The base, with one trailing slash.</summary>
    public Uri Base { get; }

    /// <summary>
    /// Sends the request <paramref name="build"/> makes for a URL: first for <paramref name="url"/>, then for each same-origin
    /// redirect. A 401 is tried once more when the key provider gives a different key.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Uri url, Func<Uri, HttpRequestMessage> build, HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        var origin = EndpointUrl.Origin(Base);
        string? rejected = null;
        for (int attempt = 0, redirects = 0; ; attempt++)
        {
            if (EndpointUrl.Origin(url) != origin)
            {
                throw new ArgumentException("A request to an endpoint stays at the endpoint's own origin.", nameof(url));
            }
            await CheckPlainHttpAsync(url, Endpoint, cancellationToken).ConfigureAwait(false);
            var request = build(url);
            var key = Endpoint.Key is { } provider ? await provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) : null;
            if (key is not null)
            {
                if (Endpoint.Dialect.KeyHeader == KeyHeader.ApiKey)
                {
                    request.Headers.TryAddWithoutValidation("api-key", key);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Endpoint.Timeout);
            var (method, route) = (request.Method.Method, RouteTemplate.Of(url));
            using var activity = FadenstackTelemetry.Client.StartActivity("HTTP " + method, ActivityKind.Client);
            activity?.SetTag("http.request.method", method);
            activity?.SetTag("http.route", route);
            var watch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, timeout.Token).ConfigureAwait(false);
                timeout.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan); // the headers are in: a stream reads on
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                Failed(activity, method, route, e, attempt, watch);
                throw new EndpointUnreachableException($"The endpoint did not answer within {Endpoint.Timeout.TotalSeconds:0} s.", e);
            }
            catch (HttpRequestException e)
            {
                Failed(activity, method, route, e, attempt, watch);
                throw Unreachable(e);
            }
            finally
            {
                request.Dispose();
            }
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            Log.Request(_log, method, route, (int)response.StatusCode, watch.ElapsedMilliseconds, attempt);
            var status = (int)response.StatusCode;
            if (status == 401 && key is not null && rejected is null)
            {
                // An Entra token may have expired; an API key gives itself again, and then one try is the end of it.
                response.Dispose();
                rejected = key;
                await Endpoint.Key!.InvalidateAsync(key, cancellationToken).ConfigureAwait(false);
                if (await Endpoint.Key.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) != key)
                {
                    continue;
                }
                throw new EndpointKeyRejectedException(HttpStatusCode.Unauthorized, "The endpoint refused the key (401).");
            }
            if (status is >= 300 and <= 399)
            {
                var to = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l : new Uri(url, l)) : null;
                var code = response.StatusCode;
                response.Dispose();
                if (to is null || EndpointUrl.Origin(to) != origin || ++redirects > MaxRedirects)
                {
                    throw Redirected(code, to);
                }
                url = to;
                continue;
            }
            return response;
        }
    }

    /// <summary>A redirect that is not followed: another origin, too many, or none named.</summary>
    private static FadenstackApiException Redirected(HttpStatusCode status, Uri? to) => new(status,
        $"The endpoint redirected the request to {(to is null ? "another address" : $"{to.Scheme}://{to.Authority}{to.AbsolutePath}")}. "
        + "Requests to an endpoint never leave its own origin: check the endpoint's address (https, the right path).",
        code: "redirect");

    /// <summary>
    /// D3: plain http only to this device, or to a private network after the endpoint's opt-in; never with a key. Decided by
    /// the addresses the host resolves to, so a name that points outside counts as outside (on .NET 8 the connection itself is
    /// checked too, <see cref="CreateHandler"/>).
    /// </summary>
    internal static async Task CheckPlainHttpAsync(Uri url, ModelEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }
        if (endpoint.Key is not null)
        {
            throw new EndpointPlainHttpRefusedException(PlainHttpRefusal.KeyOverPlainHttp,
                "A key never travels over plain http: use the endpoint's https address, or leave the key out.");
        }
        var scope = await NetworkAddress.OfHostAsync(url.Host, cancellationToken).ConfigureAwait(false);
        if (scope == AddressScope.Loopback)
        {
            return;
        }
        if (scope == AddressScope.Private)
        {
            if (endpoint.PlainHttpForbidden)
            {
                throw new EndpointPlainHttpRefusedException(PlainHttpRefusal.ForbiddenByPolicy,
                    "This computer's policy forbids plain http in the local network: use the endpoint's https address.");
            }
            if (endpoint.AllowPlainHttpInLan)
            {
                return;
            }
            throw new EndpointPlainHttpRefusedException(PlainHttpRefusal.NoOptIn,
                "Plain http to another computer in the local network needs the person's opt-in for this endpoint; https is better.");
        }
        throw new EndpointPlainHttpRefusedException(PlainHttpRefusal.NotLocal,
            "Plain http goes only to this computer or, with an opt-in, to the local network: use the endpoint's https address.");
    }

    /// <summary>A request that got no answer: unreachable, or a certificate that is not trusted.</summary>
    private static FadenstackException Unreachable(HttpRequestException e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is EndpointPlainHttpRefusedException refused)
            {
                return refused; // the connection's own check (.NET 8), wrapped by the handler
            }
            if (inner is AuthenticationException)
            {
                return new ServerCertificateException(
                    "The endpoint's certificate is not trusted (an unknown CA, a name that does not match, or expired).", e);
            }
        }
        return new EndpointUnreachableException($"The endpoint could not be reached: {e.Message}", e);
    }

    private void Failed(Activity? activity, string method, string route, Exception error, int attempt, Stopwatch watch)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", error.GetType().Name);
        Log.RequestFailed(_log, method, route, error.GetType().Name, watch.ElapsedMilliseconds, attempt);
    }

    /// <summary>An answer's body as text, at most <see cref="MaxBodyChars"/> long.</summary>
    internal static async Task<string> TextOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return "";
        }
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length + read > MaxBodyChars)
            {
                throw new FadenstackException("The endpoint's answer was larger than the SDK accepts.");
            }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    /// <summary>The typed error for an error answer (review R2: the 429 stays a <see cref="FadenstackRateLimitedException"/>).</summary>
    internal static FadenstackException ToException(HttpResponseMessage response, string body, bool keySent)
    {
        var status = response.StatusCode;
        var (message, code) = Parse(body, $"{(int)status} {response.ReasonPhrase}");
        var said = (message + " " + code).ToLowerInvariant();
        var detail = string.IsNullOrWhiteSpace(message) ? "" : ": " + message;
        switch ((int)status)
        {
            case 401:
                return new EndpointKeyRejectedException(status,
                    (keySent ? "The endpoint refused the key (401)" : "The endpoint needs a key (401)") + detail, code);
            case 403 when !keySent || said.Contains("origin") || said.Contains("cors"):
                return new EndpointOriginRefusedException(status, "The endpoint refused this origin (403)" + detail, code);
            case 403:
                return new EndpointKeyRejectedException(status, "The endpoint refused the key (403)" + detail, code);
            case 429:
                return new FadenstackRateLimitedException(status, "The endpoint's rate limit applied (429)" + detail, code,
                    ErrorReader.RetryAfter(response));
        }
        if (IsContextLength(said))
        {
            return new EndpointContextLengthException(status, "The request is longer than the model's context" + detail, code);
        }
        if (IsModelNotFound(said, code))
        {
            return new EndpointModelNotFoundException(status, "The endpoint does not have that model" + detail, code);
        }
        if ((int)status is 404 or 405)
        {
            return new EndpointNotCompatibleException(status,
                $"No OpenAI-compatible chat API at this address ({(int)status}): check the base, which includes /v1" + detail, code);
        }
        return new FadenstackApiException(status, $"The endpoint answered {(int)status}" + detail, code);
    }

    private static bool IsContextLength(string said) =>
        said.Contains("context_length_exceeded") || said.Contains("context length") || said.Contains("context size")
        || said.Contains("maximum context") || said.Contains("context window") || said.Contains("exceed_context_size");

    private static bool IsModelNotFound(string said, string? code) =>
        code == "model_not_found" || (said.Contains("model") && (said.Contains("not found") || said.Contains("does not exist")
            || said.Contains("not exist") || said.Contains("unknown model") || said.Contains("no such model")));

    /// <summary>The message and code of an error body: OpenAI's <c>{"error": {...}}</c>, Ollama's <c>{"error": "..."}</c>, a <c>detail</c>.</summary>
    internal static (string Message, string? Code) Parse(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return (Truncate(error.GetString() ?? fallback), null);
                }
                if (error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var c) ? c.ValueKind switch
                    {
                        JsonValueKind.String => c.GetString(),
                        JsonValueKind.Number => c.GetRawText(),
                        _ => null,
                    } : null;
                    return (Truncate(Str(error, "message") ?? fallback), code ?? Str(error, "type"));
                }
            }
            if (root.ValueKind == JsonValueKind.Object && Str(root, "object") == "error")
            {
                return (Truncate(Str(root, "message") ?? fallback), Str(root, "type"));
            }
        }
        catch (JsonException)
        {
            // not JSON: the status says it
        }
        var parsed = ErrorReader.Parse(body, fallback);
        return (Truncate(parsed.Message), parsed.Code);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "...";

    /// <summary>The endpoint's own handler: no redirect followed, the endpoint's CA, modern TLS on .NET Framework.</summary>
    internal static HttpMessageHandler CreateHandler(ModelEndpoint endpoint)
    {
#if NET8_0_OR_GREATER
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        // Plain http: the address actually connected to is checked as well, so a name that changes what it resolves to
        // between the check and the connection (DNS rebinding) still reaches only this device or, with the opt-in, the LAN.
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var plain = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttp;
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var allowed = addresses.Where(a => !plain || NetworkAddress.Of(a) == AddressScope.Loopback
                || (endpoint.AllowPlainHttpInLan && NetworkAddress.Of(a) == AddressScope.Private)).ToArray();
            if (allowed.Length == 0)
            {
                throw new EndpointPlainHttpRefusedException(endpoint.AllowPlainHttpInLan ? PlainHttpRefusal.NotLocal : PlainHttpRefusal.NoOptIn,
                    "Plain http goes only to this computer or, with an opt-in, to the local network: the address connected to was outside.");
            }
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        if (endpoint.TrustedCaCertificate is { } ca)
        {
            var authority = new X509Certificate2(ca);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || FadenstackTransport.ChainsTo(certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate)), authority, errors);
        }
        return handler;
#else
        var options = new FadenstackClientOptions { TrustedCaCertificate = endpoint.TrustedCaCertificate };
        return FadenstackTransport.CreateHandler(options); // no redirect, the CA, TLS 1.2/1.3 and connections on .NET Framework
#endif
    }

    public void Dispose() => _http.Dispose();
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fadenstack.Auth;

namespace Fadenstack.Http;

/// <summary>
/// Authenticated requests to a Fadenstack server, with the client's tokens, TLS settings and typed errors.
/// Public, so extensions add resources without copying the plumbing.
/// </summary>
public sealed class FadenstackTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ITokenProvider? _tokens;

    public FadenstackTransport(FadenstackClientOptions options)
    {
        ServerUrl = WithSlash(options.RequireServerUrl());
        ApiUrl = WithSlash(options.ApiUrl ?? ServerUrl);
        if (ApiUrl.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureHttp)
        {
            throw new ArgumentException("The API URL must be https unless AllowInsecureHttp is set.");
        }
        _tokens = options.Credentials;
        if (options.HttpClient is not null)
        {
            _http = options.HttpClient;
        }
        else
        {
            _http = new HttpClient(CreateHandler(options)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        Timeout = options.Timeout;
    }

    /// <summary>The gateway side: <c>/v1</c> routes are relative to it.</summary>
    public Uri ServerUrl { get; }

    /// <summary>The backend side: <c>/api</c> routes are relative to it.</summary>
    public Uri ApiUrl { get; }

    public TimeSpan Timeout { get; }

    public HttpClient HttpClient => _http;

    /// <summary>Sends a request with the bearer token; a 401 gets one retry with a fresh token.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = build();
            string? token = null;
            if (_tokens is not null && request.Headers.Authorization is null)
            {
                token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (completion == HttpCompletionOption.ResponseContentRead)
            {
                timeout.CancelAfter(Timeout);
            }
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, timeout.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FadenstackException($"Fadenstack did not answer within {Timeout.TotalSeconds:0} s.", e);
            }
            catch (HttpRequestException e)
            {
                throw new FadenstackException($"Fadenstack could not be reached: {e.Message}", e);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null && attempt == 0)
            {
                response.Dispose();
                await _tokens!.InvalidateAsync(token, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    /// <summary>JSON in, JSON out; errors as typed exceptions.</summary>
    public async Task<T> SendJsonAsync<T>(HttpMethod method, Uri url, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => JsonRequest(method, url, body),
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ErrorReader.ToException(response, text);
        }
        if (typeof(T) == typeof(bool) || string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }
        return JsonSerializer.Deserialize<T>(text, FadenstackJson.Options)!;
    }

    public Task<T> GetAsync<T>(Uri url, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(HttpMethod.Get, url, null, cancellationToken);

    public Task<T> PostAsync<T>(Uri url, object? body, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(HttpMethod.Post, url, body, cancellationToken);

    public Task<T> PutAsync<T>(Uri url, object? body, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(HttpMethod.Put, url, body, cancellationToken);

    public Task<T> PatchAsync<T>(Uri url, object? body, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(new HttpMethod("PATCH"), url, body, cancellationToken);

    public Task DeleteAsync(Uri url, CancellationToken cancellationToken = default) =>
        SendJsonAsync<bool>(HttpMethod.Delete, url, null, cancellationToken);

    /// <summary>A gateway route: <c>Gateway("v1/sessions")</c>.</summary>
    public Uri Gateway(string relative) => new(ServerUrl, relative.TrimStart('/'));

    /// <summary>A backend route: <c>Api("api/agent-tokens")</c>.</summary>
    public Uri Api(string relative) => new(ApiUrl, relative.TrimStart('/'));

    internal static HttpRequestMessage JsonRequest(HttpMethod method, Uri url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            var json = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body, FadenstackJson.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>
    /// A handler with the client's TLS settings (the trusted CA, <see cref="FadenstackClientOptions.AllowInsecureHttp"/>),
    /// for a host's own requests to the same server (e.g. its sign-in).
    /// </summary>
    public static HttpMessageHandler CreateHandler(FadenstackClientOptions options)
    {
        var handler = new HttpClientHandler();
        if (IsNetFramework)
        {
            // A native host (Word, any native application) gives .NET Framework no target framework, so it keeps the
            // legacy defaults -- SSL 3 and TLS 1.0 -- and servers refuse the handshake ("Could not create SSL/TLS
            // secure channel"). TLS 1.2 and 1.3 on this handler, not ServicePointManager: that is the host's,
            // shared with every other add-in in the process.
            handler.SslProtocols = ModernTls();
            // .NET Framework allows a client process 2 connections per server, and a streamed answer holds one
            // the whole time: in an Office application with four agent panes, two waited for the other two
            // (measured). Raised on this handler, for this server: the process default stays the host's.
            handler.MaxConnectionsPerServer = NetFrameworkConnectionsPerServer;
        }
        if (options.TrustedCaCertificate is { } ca)
        {
            var authority = new X509Certificate2(ca);
            // Per connection, never process-wide: the SDK runs inside other people's processes.
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || ChainsTo(certificate, authority, errors);
        }
        return handler;
    }

    /// <summary>Connections to the server one process may hold on .NET Framework (its default is 2; .NET 8+ has no limit).</summary>
    internal const int NetFrameworkConnectionsPerServer = 32;

    private static readonly bool IsNetFramework =
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    /// <summary>TLS 1.2 and 1.3 (1.3 is unknown to .NET Framework before 4.8, which then gets 1.2).</summary>
    internal static System.Security.Authentication.SslProtocols ModernTls()
    {
        const System.Security.Authentication.SslProtocols tls13 = (System.Security.Authentication.SslProtocols)12288;
        return Enum.IsDefined(typeof(System.Security.Authentication.SslProtocols), tls13)
            ? System.Security.Authentication.SslProtocols.Tls12 | tls13
            : System.Security.Authentication.SslProtocols.Tls12;
    }

    private static bool ChainsTo(X509Certificate2? certificate, X509Certificate2 authority,
        System.Net.Security.SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false; // a name mismatch is never excused
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.Add(authority);
        if (!chain.Build(certificate))
        {
            return false;
        }
        var root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
        return root.Thumbprint == authority.Thumbprint;
    }

    private static Uri WithSlash(Uri url) =>
        url.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? url : new Uri(url.AbsoluteUri + "/");

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

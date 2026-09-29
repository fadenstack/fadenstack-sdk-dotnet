using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Faden.Auth;

namespace Faden.Http;

/// <summary>
/// Authenticated requests to a Fadenstack server, with the client's tokens, TLS settings and typed errors.
/// Public, so extensions (<c>Faden.Enterprise</c>) add resources without copying the plumbing (01-design §3a).
/// </summary>
public sealed class FadenTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ITokenProvider? _tokens;

    public FadenTransport(FadenClientOptions options)
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
                throw new FadenException($"Fadenstack did not answer within {Timeout.TotalSeconds:0} s.", e);
            }
            catch (HttpRequestException e)
            {
                throw new FadenException($"Fadenstack could not be reached: {e.Message}", e);
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
        return JsonSerializer.Deserialize<T>(text, FadenJson.Options)!;
    }

    public Task<T> GetAsync<T>(Uri url, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(HttpMethod.Get, url, null, cancellationToken);

    public Task<T> PostAsync<T>(Uri url, object? body, CancellationToken cancellationToken = default) =>
        SendJsonAsync<T>(HttpMethod.Post, url, body, cancellationToken);

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
            var json = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body, FadenJson.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>
    /// A handler with the client's TLS settings (the trusted CA, <see cref="FadenClientOptions.AllowInsecureHttp"/>),
    /// for a host's own requests to the same server (e.g. its sign-in).
    /// </summary>
    public static HttpMessageHandler CreateHandler(FadenClientOptions options)
    {
        var handler = new HttpClientHandler();
        if (options.TrustedCaCertificate is { } ca)
        {
            var authority = new X509Certificate2(ca);
            // Per connection, never process-wide: the SDK runs inside other people's processes (01-design §11).
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || ChainsTo(certificate, authority, errors);
        }
        return handler;
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

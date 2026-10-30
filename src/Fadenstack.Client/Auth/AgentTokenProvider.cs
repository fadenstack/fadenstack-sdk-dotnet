using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Fadenstack.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Auth;

/// <summary>
/// An agent sign-in on this device: a short access token for one agent, renewed with a refresh token that
/// the server replaces on every use. Kept in an <see cref="ITokenStore"/> between runs.
/// </summary>
public sealed class AgentTokenProvider : ITokenProvider
{
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(30);
    private readonly Uri _apiUrl;
    private readonly string _agent;
    private readonly ITokenStore _store;
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger _log;
    private TokenSet? _tokens;

    /// <param name="apiUrl">The backend (usually the server URL).</param>
    /// <param name="agent">The agent's slug or id.</param>
    /// <param name="store">Where the sign-in is kept; <see cref="FileTokenStore"/> for desktop apps.</param>
    /// <param name="httpClient">The client to reach the backend with; the provider's own if not given.</param>
    /// <param name="clock">The time, for tests.</param>
    /// <param name="allowInsecureHttp">
    /// Allow <c>http://</c> to another machine, for a development server. Off: sign-ins go over https, or plain http
    /// only to this machine (localhost).
    /// </param>
    /// <param name="logger">Where renewals and their failures are logged (no token, ever); nowhere if not given.</param>
    public AgentTokenProvider(Uri apiUrl, string agent, ITokenStore? store = null, HttpClient? httpClient = null,
        Func<DateTimeOffset>? clock = null, bool allowInsecureHttp = false, ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        if (apiUrl.Scheme != Uri.UriSchemeHttps && !(apiUrl.Scheme == Uri.UriSchemeHttp && (allowInsecureHttp || apiUrl.IsLoopback)))
        {
            throw new ArgumentException(
                "The API URL must be https unless allowInsecureHttp is set or it is this machine (localhost).", nameof(apiUrl));
        }
        _apiUrl = new Uri(apiUrl.AbsoluteUri.TrimEnd('/') + "/");
        _agent = agent;
        _store = store ?? new MemoryTokenStore();
        _http = httpClient ?? new HttpClient();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _key = KeyOf(_apiUrl, agent);
    }

    /// <summary>The store key of an agent sign-in on a server: pairing saves under the same one.</summary>
    internal static string KeyOf(Uri apiUrl, string agent) =>
        $"{new Uri(apiUrl.AbsoluteUri.TrimEnd('/') + "/").AbsoluteUri}|{agent}";

    /// <summary>The device's sign-in id, once signed in.</summary>
    public string? DeviceId => _tokens?.DeviceId;

    /// <summary>
    /// Who is signed in, or null when nobody is or the server did not say. It reads what this provider has loaded:
    /// after <see cref="IsSignedInAsync"/>, <see cref="GetAccessTokenAsync"/> or a sign-in. Use
    /// <see cref="GetSignedInUserAsync"/> to read it from the store.
    /// </summary>
    public SignedInUser? SignedInUser => _tokens?.User;

    /// <summary>Who is signed in, read from the store when this provider has not loaded the sign-in yet.</summary>
    public async Task<SignedInUser?> GetSignedInUserAsync(CancellationToken cancellationToken = default) =>
        (await CurrentAsync(cancellationToken).ConfigureAwait(false))?.User;

    /// <summary>
    /// Signs in to the agent with the user's login token (the web console's session token today; OAuth or
    /// device code when the server offers them). Refused with 403 when the user has no grant.
    /// </summary>
    public async Task<TokenSet> SignInAsync(string loginToken, string? deviceLabel = null,
        CancellationToken cancellationToken = default)
    {
        var body = new { agent = _agent, device_label = deviceLabel ?? Environment.MachineName };
        var tokens = await RequestAsync("api/agent-tokens", body, loginToken, cancellationToken).ConfigureAwait(false);
        await RememberAsync(tokens, cancellationToken).ConfigureAwait(false);
        return tokens;
    }

    /// <summary>Whether a sign-in is kept (it may still have expired).</summary>
    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) =>
        (await CurrentAsync(cancellationToken).ConfigureAwait(false)) is { } t && t.RefreshExpiresAt > _now();

    /// <summary>
    /// Signs out: asks the server to revoke the sign-in (<c>POST /api/agent-tokens/revoke</c> with the refresh token), then
    /// deletes the tokens on this device. The revoke is best effort: when the server cannot be reached or refuses, the
    /// tokens are deleted anyway (the sign-in then ends at the server on its own, or in the web console's list of
    /// sign-ins). Afterwards the provider reports that sign-in is required. The refresh token is never logged.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tokens ?? await _store.LoadAsync(_key, cancellationToken).ConfigureAwait(false);
            var revoked = false;
            try
            {
                if (current is not null && !string.IsNullOrEmpty(current.RefreshToken))
                {
                    revoked = await RevokeAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await ForgetAsync(cancellationToken).ConfigureAwait(false);
            }
            Log.SignedOut(_log, _agent, revoked);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether the server confirmed (any 2xx); a failure of any kind is not an error here.</summary>
    private async Task<bool> RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = FadenstackTransport.JsonRequest(HttpMethod.Post, new Uri(_apiUrl, "api/agent-tokens/revoke"),
                new { refresh_token = refreshToken });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.SignOutRevokeFailed(_log, _agent, e.GetType().Name);
            return false;
        }
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        _tokens = null;
        await _store.DeleteAsync(_key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && current.AccessExpiresAt - Early > _now())
        {
            return current.AccessToken;
        }
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _tokens;
            if (current is not null && current.AccessExpiresAt - Early > _now())
            {
                return current.AccessToken; // another caller refreshed it
            }
            return (await RefreshLockedAsync(current, cancellationToken).ConfigureAwait(false)).AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is not null && _tokens.AccessToken == rejectedToken)
            {
                _tokens = _tokens with { AccessExpiresAt = _now() - TimeSpan.FromMinutes(1) };
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TokenSet> RefreshLockedAsync(TokenSet? current, CancellationToken cancellationToken)
    {
        if (current is null || current.RefreshExpiresAt <= _now())
        {
            throw new FadenstackSignInRequiredException($"Sign in to the agent '{_agent}' on this device.");
        }
        TokenSet renewed;
        try
        {
            renewed = await RequestAsync("api/agent-tokens/refresh", new { refresh_token = current.RefreshToken },
                null, cancellationToken).ConfigureAwait(false);
        }
        catch (FadenstackApiException e) when ((int)e.Status is 401 or 403)
        {
            // Revoked, reused elsewhere (the family is signed out), or the user lost access.
            Log.TokenRefreshFailed(_log, _agent, $"status {(int)e.Status}");
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
            throw new FadenstackSignInRequiredException($"The sign-in to '{_agent}' ended: {e.Message}", e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.TokenRefreshFailed(_log, _agent, e.GetType().Name);
            throw;
        }
        // A renewal does not repeat who signed in: keep what the sign-in said.
        await RememberAsync(renewed.User is null ? renewed with { User = current.User } : renewed, cancellationToken).ConfigureAwait(false);
        Log.TokenRefreshed(_log, _agent);
        return renewed;
    }

    private async Task<TokenSet?> CurrentAsync(CancellationToken cancellationToken) =>
        _tokens ??= await _store.LoadAsync(_key, cancellationToken).ConfigureAwait(false);

    private async Task RememberAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        _tokens = tokens;
        await _store.SaveAsync(_key, tokens, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenSet> RequestAsync(string path, object body, string? bearer, CancellationToken cancellationToken)
    {
        using var request = FadenstackTransport.JsonRequest(HttpMethod.Post, new Uri(_apiUrl, path), body);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new FadenstackException($"Fadenstack could not be reached: {e.Message}", e);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ErrorReader.ToException(response, text);
            }
            return FromWire(text, _now());
        }
    }

    /// <summary>The server's <c>AgentTokensDTO</c> (with its optional <c>user</c>) as a token set, expiries counted from <paramref name="now"/>.</summary>
    internal static TokenSet FromWire(string json, DateTimeOffset now)
    {
        var issued = JsonSerializer.Deserialize<Issued>(json, FadenstackJson.Options)!;
        return new TokenSet
        {
            AccessToken = issued.AccessToken,
            AccessExpiresAt = now.AddSeconds(issued.ExpiresIn),
            RefreshToken = issued.RefreshToken,
            RefreshExpiresAt = now.AddSeconds(issued.RefreshExpiresIn),
            DeviceId = issued.DeviceId,
            AgentId = issued.AgentId,
            Agent = issued.Agent,
            User = issued.User is { Email: { } email } user ? new SignedInUser(user.Id, email, user.DisplayName) : null,
        };
    }

    private sealed record Issued(string AccessToken, int ExpiresIn, string RefreshToken, int RefreshExpiresIn,
        string DeviceId, string AgentId, string Agent, IssuedUser? User = null);

    private sealed record IssuedUser(Guid Id, string? Email, string? DisplayName);
}

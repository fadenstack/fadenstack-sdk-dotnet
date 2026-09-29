using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Faden.Http;

namespace Faden.Auth;

/// <summary>
/// An agent sign-in on this device: a short access token for one agent, renewed with a refresh token that
/// the server replaces on every use (02 §4). Kept in an <see cref="ITokenStore"/> between runs.
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
    private TokenSet? _tokens;

    /// <param name="apiUrl">The backend (usually the server URL).</param>
    /// <param name="agent">The agent's slug or id.</param>
    /// <param name="store">Where the sign-in is kept; <see cref="FileTokenStore"/> for desktop apps.</param>
    /// <param name="httpClient">The client to reach the backend with; the provider's own if not given.</param>
    /// <param name="clock">The time, for tests.</param>
    public AgentTokenProvider(Uri apiUrl, string agent, ITokenStore? store = null, HttpClient? httpClient = null,
        Func<DateTimeOffset>? clock = null)
    {
        _apiUrl = new Uri(apiUrl.AbsoluteUri.TrimEnd('/') + "/");
        _agent = agent;
        _store = store ?? new MemoryTokenStore();
        _http = httpClient ?? new HttpClient();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _key = $"{_apiUrl.AbsoluteUri}|{agent}";
    }

    /// <summary>The device's sign-in id, once signed in.</summary>
    public string? DeviceId => _tokens?.DeviceId;

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

    /// <summary>Forgets the sign-in on this device. (Revoking it on the server: the web console or <c>DELETE /api/agent-tokens/{device}</c>.)</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
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
            throw new FadenSignInRequiredException($"Sign in to the agent '{_agent}' on this device.");
        }
        TokenSet renewed;
        try
        {
            renewed = await RequestAsync("api/agent-tokens/refresh", new { refresh_token = current.RefreshToken },
                null, cancellationToken).ConfigureAwait(false);
        }
        catch (FadenApiException e) when ((int)e.Status is 401 or 403)
        {
            // Revoked, reused elsewhere (the family is signed out), or the user lost access.
            await SignOutAsync(cancellationToken).ConfigureAwait(false);
            throw new FadenSignInRequiredException($"The sign-in to '{_agent}' ended: {e.Message}", e);
        }
        await RememberAsync(renewed, cancellationToken).ConfigureAwait(false);
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
        using var request = FadenTransport.JsonRequest(HttpMethod.Post, new Uri(_apiUrl, path), body);
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
            throw new FadenException($"Fadenstack could not be reached: {e.Message}", e);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ErrorReader.ToException(response, text);
            }
            var issued = JsonSerializer.Deserialize<Issued>(text, FadenJson.Options)!;
            var now = _now();
            return new TokenSet
            {
                AccessToken = issued.AccessToken,
                AccessExpiresAt = now.AddSeconds(issued.ExpiresIn),
                RefreshToken = issued.RefreshToken,
                RefreshExpiresAt = now.AddSeconds(issued.RefreshExpiresIn),
                DeviceId = issued.DeviceId,
                AgentId = issued.AgentId,
                Agent = issued.Agent,
            };
        }
    }

    private sealed record Issued(string AccessToken, int ExpiresIn, string RefreshToken, int RefreshExpiresIn,
        string DeviceId, string AgentId, string Agent);
}

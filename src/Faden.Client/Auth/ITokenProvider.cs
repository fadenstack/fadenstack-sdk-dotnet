namespace Faden.Auth;

/// <summary>Where a client's access tokens come from.</summary>
public interface ITokenProvider
{
    /// <summary>A token valid now. Refreshes when it has to.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>The server refused the last token (401): the next call must not reuse it.</summary>
    Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken = default);
}

/// <summary>A fixed token: an API token for the plain routes, or a user's login token for tests and tools.</summary>
public sealed class StaticTokenProvider : ITokenProvider
{
    private readonly string _token;

    public StaticTokenProvider(string token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? throw new ArgumentException("A token is required.", nameof(token)) : token;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(_token);

    public Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

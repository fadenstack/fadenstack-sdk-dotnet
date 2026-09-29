namespace Faden.Auth;

/// <summary>An agent sign-in on one device: a short access token and the refresh token that renews it.</summary>
public sealed record TokenSet
{
    public string AccessToken { get; init; } = "";

    public DateTimeOffset AccessExpiresAt { get; init; }

    public string RefreshToken { get; init; } = "";

    public DateTimeOffset RefreshExpiresAt { get; init; }

    /// <summary>This device's sign-in; the server lists and revokes sign-ins by it.</summary>
    public string DeviceId { get; init; } = "";

    public string AgentId { get; init; } = "";

    public string Agent { get; init; } = "";
}

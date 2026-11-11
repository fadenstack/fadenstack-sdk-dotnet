namespace Fadenstack.Auth;

/// <summary>An agent sign-in on one device: a short access token and the refresh token that renews it.</summary>
public sealed record TokenSet
{
    /// <summary>The short-lived JWT that is sent as the bearer token.</summary>
    public string AccessToken { get; init; } = "";

    public DateTimeOffset AccessExpiresAt { get; init; }

    public string RefreshToken { get; init; } = "";

    public DateTimeOffset RefreshExpiresAt { get; init; }

    /// <summary>This device's sign-in; the server lists and revokes sign-ins by it.</summary>
    public string DeviceId { get; init; } = "";

    public string AgentId { get; init; } = "";

    public string Agent { get; init; } = "";

    /// <summary>
    /// Who is signed in, when the server said (sign-in by pairing does). Null for sign-ins kept before this existed:
    /// stored token sets without it still load.
    /// </summary>
    public SignedInUser? User { get; init; }
}

/// <summary>The person an agent sign-in belongs to, for showing "Signed in as ...". Kept with the tokens.</summary>
/// <param name="Id">The user's id on the server.</param>
/// <param name="Email">The user's e-mail address.</param>
/// <param name="DisplayName">The user's name, when they have one.</param>
public sealed record SignedInUser(Guid Id, string Email, string? DisplayName = null);

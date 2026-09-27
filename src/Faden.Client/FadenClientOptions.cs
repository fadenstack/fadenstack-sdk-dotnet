namespace Faden.Client;

/// <summary>Configuration for <see cref="FadenClient"/>.</summary>
public sealed class FadenClientOptions
{
    /// <summary>Base URL of the Fadenstack instance (e.g. "https://faden.example.com").</summary>
    public required string BaseUrl { get; init; }

    /// <summary>JWT bearer token for authentication.</summary>
    public required string ApiKey { get; init; }

    /// <summary>HTTP request timeout in seconds. Defaults to 30.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

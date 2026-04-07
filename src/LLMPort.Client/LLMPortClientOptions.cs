namespace LLMPort.Client;

/// <summary>Configuration for <see cref="LLMPortClient"/>.</summary>
public sealed class LLMPortClientOptions
{
    /// <summary>Base URL of the LLM.port instance (e.g. "https://llmport.example.com").</summary>
    public required string BaseUrl { get; init; }

    /// <summary>JWT bearer token for authentication.</summary>
    public required string ApiKey { get; init; }

    /// <summary>HTTP request timeout in seconds. Defaults to 30.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

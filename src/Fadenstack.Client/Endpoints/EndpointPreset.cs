using System.Text.Json.Serialization;

namespace Fadenstack.Endpoints;

/// <summary>The kind of server behind an endpoint, as far as the SDK can tell; it decides the watermark status (F03 item 8).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointServer
{
    Unknown,
    OpenAI,
    AzureOpenAI,
    Anthropic,
    XAi,
    Google,
    Mistral,
    OpenRouter,
    LiteLlm,
    Ollama,
    LMStudio,
    LlamaCpp,
    LocalAI,
    Vllm,
    Sglang,
}

/// <summary>
/// A ready-made endpoint (sdk/51 F03): its base and dialect, and whether it needs a key. The connection wizard (F15) offers
/// them; the local ones are also what the local search looks for.
/// </summary>
public sealed record EndpointPreset
{
    /// <summary>Kept in settings: <c>ollama</c>, <c>azure-openai</c>, ...</summary>
    public string Id { get; init; } = "";

    /// <summary>The product's own name (not translated).</summary>
    public string Name { get; init; } = "";

    /// <summary>The usual base; null where it is the person's own (Azure's resource, LiteLLM's host).</summary>
    public Uri? BaseUrl { get; init; }

    public EndpointDialect Dialect { get; init; } = EndpointDialect.OpenAICompatible;

    public EndpointServer Server { get; init; }

    public bool NeedsKey { get; init; }

    /// <summary>A server on this computer, found by the local search (with the person's consent).</summary>
    public bool Local { get; init; }

    public static EndpointPreset OpenAI { get; } = new()
    {
        Id = "openai", Name = "OpenAI", BaseUrl = new Uri("https://api.openai.com/v1"), Dialect = EndpointDialect.OpenAI,
        Server = EndpointServer.OpenAI, NeedsKey = true,
    };

    /// <summary>Azure OpenAI's v1 API: <c>https://{resource}.openai.azure.com/openai/v1</c>.</summary>
    public static EndpointPreset AzureOpenAI { get; } = new()
    {
        Id = "azure-openai", Name = "Azure OpenAI", Dialect = EndpointDialect.AzureOpenAI, Server = EndpointServer.AzureOpenAI,
        NeedsKey = true,
    };

    public static EndpointPreset Ollama { get; } = new()
    {
        Id = "ollama", Name = "Ollama", BaseUrl = new Uri("http://localhost:11434/v1"), Dialect = EndpointDialect.Ollama,
        Server = EndpointServer.Ollama, Local = true,
    };

    public static EndpointPreset LMStudio { get; } = new()
    {
        Id = "lm-studio", Name = "LM Studio", BaseUrl = new Uri("http://localhost:1234/v1"), Dialect = EndpointDialect.LMStudio,
        Server = EndpointServer.LMStudio, Local = true,
    };

    public static EndpointPreset Vllm { get; } = new()
    {
        Id = "vllm", Name = "vLLM", BaseUrl = new Uri("http://localhost:8000/v1"), Dialect = EndpointDialect.Vllm,
        Server = EndpointServer.Vllm, Local = true,
    };

    public static EndpointPreset Sglang { get; } = new()
    {
        Id = "sglang", Name = "SGLang", BaseUrl = new Uri("http://localhost:30000/v1"), Dialect = EndpointDialect.Sglang,
        Server = EndpointServer.Sglang,
    };

    public static EndpointPreset LlamaCpp { get; } = new()
    {
        Id = "llama.cpp", Name = "llama.cpp", BaseUrl = new Uri("http://localhost:8080/v1"), Dialect = EndpointDialect.LlamaCpp,
        Server = EndpointServer.LlamaCpp, Local = true,
    };

    public static EndpointPreset LiteLlm { get; } = new()
    {
        Id = "litellm", Name = "LiteLLM", Dialect = EndpointDialect.LiteLlm, Server = EndpointServer.LiteLlm, NeedsKey = true,
    };

    public static EndpointPreset OpenRouter { get; } = new()
    {
        Id = "openrouter", Name = "OpenRouter", BaseUrl = new Uri("https://openrouter.ai/api/v1"), Dialect = EndpointDialect.OpenRouter,
        Server = EndpointServer.OpenRouter, NeedsKey = true,
    };

    public static EndpointPreset Mistral { get; } = new()
    {
        Id = "mistral", Name = "Mistral", BaseUrl = new Uri("https://api.mistral.ai/v1"), Dialect = EndpointDialect.Mistral,
        Server = EndpointServer.Mistral, NeedsKey = true,
    };

    public static IReadOnlyList<EndpointPreset> All { get; } =
        [OpenAI, AzureOpenAI, Ollama, LMStudio, Vllm, Sglang, LlamaCpp, LiteLlm, OpenRouter, Mistral];

    /// <summary>The preset of that id, or null.</summary>
    public static EndpointPreset? Named(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

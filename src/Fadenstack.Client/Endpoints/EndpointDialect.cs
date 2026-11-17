using System.Text.Json.Serialization;

namespace Fadenstack.Endpoints;

/// <summary>Which field caps an answer's length.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaxTokensField
{
    /// <summary><c>max_tokens</c>: most servers.</summary>
    MaxTokens,

    /// <summary><c>max_completion_tokens</c>: OpenAI and Azure OpenAI, whose reasoning models refuse <c>max_tokens</c>.</summary>
    MaxCompletionTokens,
}

/// <summary>How the request says how much the model reasons (<c>ChatOptions.Reasoning</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThinkingSwitch
{
    /// <summary>Not sent: the model's own default.</summary>
    None,

    /// <summary>OpenAI's <c>reasoning_effort</c> (<c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>).</summary>
    ReasoningEffort,

    /// <summary>
    /// The chat template's own switch, <c>chat_template_kwargs.enable_thinking</c> (Qwen and others on vLLM, SGLang,
    /// llama.cpp): <c>false</c> for no reasoning, <c>true</c> for any level.
    /// </summary>
    ChatTemplateKwargs,
}

/// <summary>How a request asks a server to leave its text watermark off; sent only for calls no person reads (F10).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WatermarkSwitchOff
{
    /// <summary>The server has no such switch, or the SDK never sends it.</summary>
    None,

    /// <summary><c>watermarking: false</c>: vLLM (0.30+) and TensorRT-LLM.</summary>
    WatermarkingFalse,

    /// <summary><c>watermark: {"enabled": false}</c>: SGLang (0.5.22+).</summary>
    WatermarkEnabledFalse,
}

/// <summary>Which header carries the key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyHeader
{
    /// <summary><c>Authorization: Bearer {key}</c>.</summary>
    Bearer,

    /// <summary><c>api-key: {key}</c>: Azure OpenAI's API keys.</summary>
    ApiKey,
}

/// <summary>
/// What a kind of OpenAI-compatible server expects in a chat request where they differ (sdk/51 F03): the length field, the
/// thinking switch, whether a stream may ask for usage figures, the key's header, and whether the request asks for the
/// server's text watermark. A preset or the endpoint check picks one; an app may keep it with the endpoint.
/// </summary>
public sealed record EndpointDialect
{
    /// <summary>The dialect's name, as kept in settings: <c>openai</c>, <c>ollama</c>, <c>vllm</c>, ...</summary>
    public string Name { get; init; } = "openai-compatible";

    public MaxTokensField MaxTokens { get; init; } = MaxTokensField.MaxTokens;

    public ThinkingSwitch Thinking { get; init; } = ThinkingSwitch.None;

    /// <summary>Whether a streamed request sends <c>stream_options.include_usage</c> (servers that refuse unknown fields may not take it).</summary>
    public bool StreamUsage { get; init; } = true;

    public KeyHeader KeyHeader { get; init; } = KeyHeader.Bearer;

    /// <summary>
    /// Whether <c>reasoning_effort</c> may say <c>xhigh</c>; elsewhere the highest level is sent as <c>high</c>.
    /// </summary>
    public bool ExtraHighEffort { get; init; }

    /// <summary>
    /// Whether every request for text people read asks the server for its text watermark: SGLang's per-request
    /// <c>watermark</c> setting (<c>{"enabled": true}</c>; part of D18, owner 14 Nov). Other servers mark, or not, by their
    /// own configuration.
    /// </summary>
    public bool RequestWatermark { get; init; }

    /// <summary>
    /// How a request for an answer no person reads (<see cref="Chat.FadenstackChatOptions.Unread"/>) asks the server to leave
    /// its watermark off. Never sent for text people read (owner, 14 Nov; part of D18).
    /// </summary>
    public WatermarkSwitchOff WatermarkSwitchOff { get; init; }

    /// <summary>
    /// The check found that the model thinks although the off switch was sent (gpt-oss ignores <c>reasoning_effort: "none"</c>; it
    /// knows only low, medium and high). With a <see cref="ThinkingSwitch.ReasoningEffort"/> switch, Off then sends <c>low</c>, the
    /// lowest level the model has; a <see cref="ThinkingSwitch.ChatTemplateKwargs"/> switch has no lower level and still sends the
    /// off switch. Set from the check's report (<see cref="EndpointReport.ThinksWhenOff"/>); false means no. TypeScript:
    /// <c>EndpointDialect.thinksWhenOff</c>.
    /// </summary>
    public bool ThinksWhenOff { get; init; }

    /// <summary>Any server that speaks the OpenAI chat completions API: nothing beyond the common fields.</summary>
    public static EndpointDialect OpenAICompatible { get; } = new();

    public static EndpointDialect OpenAI { get; } = new()
    {
        Name = "openai", MaxTokens = MaxTokensField.MaxCompletionTokens, Thinking = ThinkingSwitch.ReasoningEffort,
        ExtraHighEffort = true,
    };

    /// <summary>Azure OpenAI's v1 API (<c>https://{resource}.openai.azure.com/openai/v1</c>) with an API key.</summary>
    public static EndpointDialect AzureOpenAI { get; } = new()
    {
        Name = "azure-openai", MaxTokens = MaxTokensField.MaxCompletionTokens, Thinking = ThinkingSwitch.ReasoningEffort,
        KeyHeader = KeyHeader.ApiKey, ExtraHighEffort = true,
    };

    public static EndpointDialect Ollama { get; } = new() { Name = "ollama", Thinking = ThinkingSwitch.ReasoningEffort };

    public static EndpointDialect LMStudio { get; } = new() { Name = "lm-studio" };

    public static EndpointDialect Vllm { get; } = new()
    {
        Name = "vllm", Thinking = ThinkingSwitch.ChatTemplateKwargs, WatermarkSwitchOff = WatermarkSwitchOff.WatermarkingFalse,
    };

    public static EndpointDialect Sglang { get; } = new()
    {
        Name = "sglang", Thinking = ThinkingSwitch.ChatTemplateKwargs, RequestWatermark = true,
        WatermarkSwitchOff = WatermarkSwitchOff.WatermarkEnabledFalse,
    };

    public static EndpointDialect LlamaCpp { get; } = new() { Name = "llama.cpp", Thinking = ThinkingSwitch.ChatTemplateKwargs };

    public static EndpointDialect LiteLlm { get; } = new() { Name = "litellm", Thinking = ThinkingSwitch.ReasoningEffort };

    public static EndpointDialect OpenRouter { get; } = new() { Name = "openrouter", Thinking = ThinkingSwitch.ReasoningEffort };

    /// <summary>Mistral's API, which refuses fields it does not know: no thinking switch, no usage request.</summary>
    public static EndpointDialect Mistral { get; } = new() { Name = "mistral", StreamUsage = false };

    /// <summary>Every built-in dialect.</summary>
    public static IReadOnlyList<EndpointDialect> All { get; } =
    [
        OpenAICompatible, OpenAI, AzureOpenAI, Ollama, LMStudio, Vllm, Sglang, LlamaCpp, LiteLlm, OpenRouter, Mistral,
    ];

    /// <summary>The built-in dialect of that name, or the plain OpenAI-compatible one when there is none.</summary>
    public static EndpointDialect Named(string? name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ?? OpenAICompatible;
}

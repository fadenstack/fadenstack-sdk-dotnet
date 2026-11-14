using System.Text.Json.Serialization;

namespace Fadenstack.Endpoints;

/// <summary>Whether text from a connection carries the server's own watermark (F03 item 8; part of D18, owner 14 Nov).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextWatermark
{
    /// <summary>Nobody has documented it for this server, or it depends on a provider behind it.</summary>
    Unknown,

    /// <summary>The provider marks text by default (Anthropic).</summary>
    Marks,

    /// <summary>Only when the account turned text watermarking on (OpenAI, xAI).</summary>
    IfAccountEnabled,

    /// <summary>Only when the server's operator configured it (vLLM 0.30+, SGLang 0.5.22+).</summary>
    IfOperatorConfigured,

    /// <summary>The server has no text watermark (Ollama, llama.cpp, LM Studio, LocalAI; vLLM and SGLang before it).</summary>
    Never,
}

/// <summary>A connection's text watermark status, with the source it rests on; kept for the records (F10).</summary>
public sealed record WatermarkStatus
{
    public TextWatermark Status { get; init; }

    /// <summary>Where the status comes from: the provider's or project's own page.</summary>
    public string? Source { get; init; }

    /// <summary>When the source was read (ISO date).</summary>
    public string? Checked { get; init; }

    /// <summary>Whether every request for text people read asks for the mark (SGLang's per-request setting).</summary>
    public bool Requested { get; init; }

    /// <summary>What the server said of the check's answer, where it says (xAI's <c>x-watermark</c>); null: nothing.</summary>
    public bool? Confirmed { get; init; }
}

/// <summary>
/// The sourced table by server type (sdk/51 F03 item 8), from fadenstack-dev <c>reports/AI Act marking in standalone tools.md</c>,
/// read 14 November 2026. Update the row and its source when a provider changes; never claim a mark the source does not give.
/// </summary>
internal static class TextWatermarks
{
    internal const string Checked = "2026-11-14";

    private static readonly Version VllmFirst = new(0, 30, 0);
    private static readonly Version SglangFirst = new(0, 5, 22);

    public static WatermarkStatus For(EndpointServer server, string? version, bool requested, bool? confirmed)
    {
        var (status, source) = server switch
        {
            EndpointServer.Anthropic => (TextWatermark.Marks, "https://support.claude.com/en/articles/16266773"),
            EndpointServer.OpenAI => (TextWatermark.IfAccountEnabled,
                "https://techcrunch.com/2026/10/05/openai-will-start-watermarking-chatgpts-text-in-the-eu/"),
            EndpointServer.XAi => (TextWatermark.IfAccountEnabled, "https://docs.x.ai/developers/advanced-api-usage/watermarking"),
            EndpointServer.Vllm => (AtLeast(version, VllmFirst), "https://docs.vllm.ai/en/latest/features/watermarking/"),
            EndpointServer.Sglang => (AtLeast(version, SglangFirst), "https://docs.sglang.io/docs/advanced_features/text_watermarking"),
            // No watermark shipped (searched 14 Nov 2026): the projects' own pages.
            EndpointServer.Ollama => (TextWatermark.Never, "https://github.com/ollama/ollama"),
            EndpointServer.LMStudio => (TextWatermark.Never, "https://lmstudio.ai/docs/developer"),
            EndpointServer.LocalAI => (TextWatermark.Never, "https://github.com/mudler/LocalAI"),
            EndpointServer.LlamaCpp => (TextWatermark.Never, "https://github.com/ggml-org/llama.cpp/pull/28350"),
            EndpointServer.AzureOpenAI => (TextWatermark.Unknown,
                "https://learn.microsoft.com/azure/foundry/responsible-ai/content-safety/provenance-disclosure"),
            EndpointServer.Google => (TextWatermark.Unknown,
                "https://discuss.ai.google.dev/t/does-gemini-api-text-output-carry-synthid-watermarking-gemini-2-5-flash-lite-gemini-3-1-flash-lite-eu-ai-act-art-50-2/177241"),
            _ => (TextWatermark.Unknown, (string?)null), // Mistral, OpenRouter, LiteLLM, unknown: none found, or the provider behind it decides
        };
        return new WatermarkStatus { Status = status, Source = source, Checked = Checked, Requested = requested, Confirmed = confirmed };
    }

    /// <summary>A known version before the first with the feature: never; at or after it, or unknown: only if configured.</summary>
    private static TextWatermark AtLeast(string? version, Version first) =>
        Parse(version) is { } v && v < first ? TextWatermark.Never : TextWatermark.IfOperatorConfigured;

    internal static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var core = new string(version!.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = core.Split(['.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? new Version(major, minor, parts.Length > 2 && int.TryParse(parts[2], out var patch) ? patch : 0)
            : null;
    }
}

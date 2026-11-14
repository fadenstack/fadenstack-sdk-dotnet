using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Chat;
using Microsoft.Extensions.AI;

namespace Fadenstack.Endpoints;

/// <summary>
/// The chat request for a person's own endpoint: <see cref="ChatWire"/>'s OpenAI shape, without anything that is the
/// gateway's (review R2: <c>ChatWire</c> writes <c>faden.session_id</c> and <c>faden.skills</c> as <c>session_id</c> and
/// <c>skills</c>, and copies unprefixed keys into the body), and in the endpoint's dialect.
/// </summary>
internal static class EndpointWire
{
    /// <summary>
    /// The extra request fields an app may pass through <c>ChatOptions.AdditionalProperties</c>: sampling knobs that many
    /// servers know. Nothing else goes to the endpoint; in particular never a field that switches a watermark off (F10).
    /// </summary>
    internal static readonly IReadOnlyCollection<string> PassThrough = ["top_k", "min_p", "repetition_penalty", "parallel_tool_calls"];

    public static JsonObject Request(IEnumerable<ChatMessage> messages, ChatOptions? options, string model, bool stream,
        EndpointDialect dialect)
    {
        var body = ChatWire.Request(messages, Clean(options), model, stream);
        if (!dialect.StreamUsage)
        {
            body.Remove("stream_options");
        }
        if (dialect.MaxTokens == MaxTokensField.MaxCompletionTokens && body["max_tokens"] is { } max)
        {
            body.Remove("max_tokens");
            body["max_completion_tokens"] = max.DeepClone();
        }
        var effort = body["reasoning_effort"]?.GetValue<string>();
        body.Remove("reasoning_effort");
        if (effort is not null)
        {
            switch (dialect.Thinking)
            {
                case ThinkingSwitch.ReasoningEffort:
                    body["reasoning_effort"] = effort == "xhigh" && !dialect.ExtraHighEffort ? "high" : effort;
                    break;
                case ThinkingSwitch.ChatTemplateKwargs:
                    body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = effort != "none" };
                    break;
            }
        }
        if (body["messages"] is JsonArray list)
        {
            foreach (var message in list.OfType<JsonObject>())
            {
                message.Remove("name"); // the person's name stays on the device (data minimisation)
            }
        }
        if (dialect.RequestWatermark)
        {
            body["watermark"] = new JsonObject { ["enabled"] = true };
        }
        return body;
    }

    /// <summary>The options without the gateway's keys and without any extra key that is not passed through.</summary>
    private static ChatOptions? Clean(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { Count: > 0 } extra)
        {
            return options;
        }
        var clean = options.Clone();
        clean.AdditionalProperties = new AdditionalPropertiesDictionary();
        foreach (var pair in extra)
        {
            if (PassThrough.Contains(pair.Key))
            {
                clean.AdditionalProperties[pair.Key] = pair.Value;
            }
        }
        return clean;
    }

    /// <summary>
    /// Whether the request's sampling is deterministic (temperature 0, top-k 1), which defeats a text watermark (F10): the
    /// record then says the endpoint's mark may not apply.
    /// </summary>
    public static bool Deterministic(ChatOptions? options) =>
        options?.Temperature == 0f
        || (options?.AdditionalProperties is { } extra && extra.TryGetValue("top_k", out var k) && k is not null
            && double.TryParse(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var topK) && topK == 1);
}

/// <summary>
/// What an endpoint's answer says about its text watermark (part of D18), on the answer's
/// <c>AdditionalProperties[FadenstackChatOptions.EndpointMarking]</c>: whether the request asked for it, whether the server
/// confirmed it (xAI's <c>x-watermark</c> header, the only per-answer confirmation found), and whether the sampling was
/// deterministic, which defeats it.
/// </summary>
public sealed record EndpointMarking
{
    /// <summary>The request asked the server for its watermark (SGLang's per-request setting).</summary>
    public bool Requested { get; init; }

    /// <summary>The server said whether this answer is watermarked (<c>x-watermark</c>); null when it said nothing.</summary>
    public bool? Confirmed { get; init; }

    /// <summary>Temperature 0 or top-k 1: a watermark may not apply to this answer.</summary>
    public bool Deterministic { get; init; }

    internal static bool? Header(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "true" or "1" or "yes" or "on" or "watermarked" or "applied" => true,
        "false" or "0" or "no" or "off" or "none" or "not_applied" => false,
        _ => null,
    };
}

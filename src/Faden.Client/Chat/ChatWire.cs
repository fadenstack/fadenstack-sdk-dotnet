using System.Text.Json;
using System.Text.Json.Nodes;
using Faden.Governance;
using Microsoft.Extensions.AI;

namespace Faden.Chat;

/// <summary>Microsoft.Extensions.AI ↔ the OpenAI chat completions shape the gateway speaks.</summary>
internal static class ChatWire
{
    /// <summary>Microsoft.Extensions.AI's defaults, compact (they indent).</summary>
    internal static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public static JsonObject Request(IEnumerable<ChatMessage> messages, ChatOptions? options, string model, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? model,
            ["messages"] = Messages(messages, options?.Instructions),
        };
        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (options is null)
        {
            return body;
        }
        if (options.Temperature is { } temperature) body["temperature"] = temperature;
        if (options.TopP is { } topP) body["top_p"] = topP;
        if (options.MaxOutputTokens is { } max) body["max_tokens"] = max;
        if (options.Seed is { } seed) body["seed"] = seed;
        if (options.FrequencyPenalty is { } fp) body["frequency_penalty"] = fp;
        if (options.PresencePenalty is { } pp) body["presence_penalty"] = pp;
        if (options.StopSequences is { Count: > 0 } stop) body["stop"] = new JsonArray(stop.Select(s => (JsonNode?)s).ToArray());
        // OpenAI's field; the gateway turns "none" into a vLLM template's own switch (Qwen's enable_thinking).
        if (options.Reasoning?.Effort is { } effort && Effort(effort) is { } wire) body["reasoning_effort"] = wire;
        if (options.ResponseFormat is ChatResponseFormatJson json)
        {
            body["response_format"] = json.Schema is { } schema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = json.SchemaName ?? "response",
                        ["schema"] = JsonNode.Parse(schema.GetRawText()),
                    },
                }
                : new JsonObject { ["type"] = "json_object" };
        }
        var tools = (options.Tools ?? []).OfType<AIFunctionDeclaration>().ToList();
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(Tool).ToArray());
            if (options.ToolMode is { } mode)
            {
                body["tool_choice"] = mode switch
                {
                    RequiredChatToolMode { RequiredFunctionName: { } name } => new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name },
                    },
                    RequiredChatToolMode => "required",
                    NoneChatToolMode => "none",
                    _ => "auto",
                };
            }
        }
        foreach (var pair in options.AdditionalProperties ?? new AdditionalPropertiesDictionary())
        {
            if (pair.Key == FadenChatOptions.SessionId)
            {
                body["session_id"] = pair.Value?.ToString();
            }
            else if (pair.Key == FadenChatOptions.Skills)
            {
                body["skills"] = JsonSerializer.SerializeToNode(pair.Value, Compact);
            }
            else if (!pair.Key.StartsWith("faden.", StringComparison.Ordinal))
            {
                body[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, Compact);
            }
        }
        return body;
    }

    private static string? Effort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => null,
    };

    private static JsonNode Tool(AIFunctionDeclaration tool)
    {
        var function = new JsonObject { ["name"] = tool.Name };
        if (!string.IsNullOrEmpty(tool.Description))
        {
            function["description"] = tool.Description;
        }
        function["parameters"] = JsonNode.Parse(tool.JsonSchema.ValueKind == JsonValueKind.Undefined
            ? "{\"type\":\"object\",\"properties\":{}}" : tool.JsonSchema.GetRawText());
        return new JsonObject { ["type"] = "function", ["function"] = function };
    }

    private static JsonArray Messages(IEnumerable<ChatMessage> messages, string? instructions)
    {
        var array = new JsonArray();
        if (!string.IsNullOrEmpty(instructions))
        {
            array.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        }
        foreach (var message in messages)
        {
            foreach (var node in Message(message))
            {
                array.Add(node);
            }
        }
        return array;
    }

    private static IEnumerable<JsonObject> Message(ChatMessage message)
    {
        var role = message.Role.Value;
        if (message.Role == ChatRole.Tool)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                yield return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["content"] = ResultText(result),
                };
            }
            yield break;
        }

        var node = new JsonObject { ["role"] = role };
        var texts = message.Contents.OfType<TextContent>().Select(t => t.Text).ToList();
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
        var calls = message.Contents.OfType<FunctionCallContent>().Where(c => !c.InformationalOnly).ToList();

        if (images.Count > 0 && message.Role == ChatRole.User)
        {
            var parts = new JsonArray();
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                }
                else if (content is DataContent data && data.HasTopLevelMediaType("image"))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = data.Uri },
                    });
                }
            }
            node["content"] = parts;
        }
        else
        {
            node["content"] = texts.Count > 0 ? string.Concat(texts) : (calls.Count > 0 ? null : "");
        }
        if (message.AuthorName is { Length: > 0 } author && message.Role == ChatRole.User)
        {
            node["name"] = author;
        }
        if (calls.Count > 0 && message.Role == ChatRole.Assistant)
        {
            node["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,
                    ["arguments"] = JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>(),
                        Compact),
                },
            }).ToArray());
        }
        yield return node;
    }

    internal static string ResultText(FunctionResultContent result)
    {
        if (result.Exception is { } e && result.Result is null)
        {
            return $"Error: {e.Message}";
        }
        return result.Result switch
        {
            null => "",
            string s => s,
            JsonElement j when j.ValueKind == JsonValueKind.String => j.GetString() ?? "",
            JsonElement j => j.GetRawText(),
            _ => JsonSerializer.Serialize(result.Result, Compact),
        };
    }

    public static IDictionary<string, object?>? Arguments(string? json, out Exception? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = new FormatException("Tool arguments are not a JSON object.");
                return null;
            }
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException e)
        {
            error = e;
            return null;
        }
    }

    public static FunctionCallContent Call(string id, string name, string? arguments)
    {
        var parsed = Arguments(arguments, out var error);
        return new FunctionCallContent(id, name, parsed) { Exception = error };
    }

    public static ChatFinishReason? Finish(string? value) => value switch
    {
        null or "" => null,
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => new ChatFinishReason(value!),
    };

    public static UsageDetails? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        long? Read(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        var details = new UsageDetails
        {
            InputTokenCount = Read("prompt_tokens"),
            OutputTokenCount = Read("completion_tokens"),
            TotalTokenCount = Read("total_tokens"),
        };
        if (usage.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object
            && pd.TryGetProperty("cached_tokens", out var cached) && cached.ValueKind == JsonValueKind.Number)
        {
            details.CachedInputTokenCount = cached.GetInt64();
        }
        return details;
    }

    /// <summary>A whole answer.</summary>
    public static ChatResponse Response(JsonElement root)
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>());
        ChatFinishReason? finish = null;
        if (root.TryGetProperty("faden", out var faden) && faden.ValueKind == JsonValueKind.Object
            && faden.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
            {
                message.Contents.Add(new FadenEventContent(e));
            }
        }
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            finish = Finish(Str(choice, "finish_reason"));
            if (choice.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                var reasoning = Str(m, "reasoning_content") ?? Str(m, "reasoning");
                if (!string.IsNullOrEmpty(reasoning))
                {
                    message.Contents.Add(new TextReasoningContent(reasoning));
                }
                if (Str(m, "content") is { Length: > 0 } text)
                {
                    message.Contents.Add(new TextContent(text));
                }
                if (m.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var function = call.GetProperty("function");
                        message.Contents.Add(Call(Str(call, "id") ?? "", Str(function, "name") ?? "",
                            Str(function, "arguments")));
                    }
                }
            }
        }
        var response = new ChatResponse(message)
        {
            ResponseId = Str(root, "id"),
            ModelId = Str(root, "model"),
            FinishReason = finish,
            Usage = Usage(root),
            RawRepresentation = root.Clone(),
        };
        if (faden.ValueKind == JsonValueKind.Object && faden.TryGetProperty("governance", out var g)
            && g.ValueKind == JsonValueKind.Object)
        {
            Refused(response, Notice(g));
        }
        return response;
    }

    /// <summary>Marks a response the gateway refused, and adds the notice to its contents.</summary>
    public static void Refused(ChatResponse response, GovernanceNotice notice)
    {
        response.FinishReason = ChatFinishReason.ContentFilter;
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[FadenChatOptions.Outcome] = notice.Outcome;
        response.AdditionalProperties[FadenChatOptions.Governance] = notice;
        if (response.Messages.Count == 0)
        {
            response.Messages.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>()));
        }
        response.Messages[response.Messages.Count - 1].Contents.Add(new GovernanceNoticeContent(notice));
    }

    public static GovernanceNotice Notice(JsonElement g, TimeSpan? retryAfter = null) => new()
    {
        Outcome = Str(g, "outcome") ?? "",
        Stage = Str(g, "stage"),
        Category = Str(g, "category"),
        Message = Str(g, "message") ?? "",
        DecisionId = Str(g, "decision_id"),
        RequestId = Str(g, "request_id"),
        RetryAfter = retryAfter,
    };

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

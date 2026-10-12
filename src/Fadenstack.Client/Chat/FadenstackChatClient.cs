using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fadenstack.Governance;
using Fadenstack.Http;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Chat;

/// <summary>
/// The gateway as an <see cref="IChatClient"/>, over <see cref="HttpClient"/> -- not the OpenAI SDK: the
/// gateway's events (tool rounds, approvals, notices) arrive as <see cref="FadenstackEventContent"/>, and a
/// refused prompt or answer as a <see cref="GovernanceNoticeContent"/> on a <c>content_filter</c> response.
/// For an agent it calls <c>/v1/agents/{agent}/chat/completions</c>; otherwise <c>/v1/chat/completions</c>.
/// </summary>
public sealed class FadenstackChatClient : IChatClient
{
    private readonly FadenstackTransport _transport;
    private readonly FadenstackClientOptions _options;
    private readonly Uri _endpoint;
    private readonly ChatClientMetadata _metadata;

    internal FadenstackChatClient(FadenstackTransport transport, FadenstackClientOptions options)
    {
        _transport = transport;
        _options = options;
        _endpoint = options.Agent is { } agent
            ? transport.Gateway($"v1/agents/{Uri.EscapeDataString(agent)}/chat/completions")
            : transport.Gateway("v1/chat/completions");
        _metadata = new ChatClientMetadata("fadenstack", _endpoint, options.Model);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = ChatWire.Request(messages, options, Model(options), stream: false).ToJsonString();
        using var response = await _transport.SendAsync(() => Build(body, options),
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Refused(response, text) ?? throw ErrorReader.ToException(response, text);
        }
        using var doc = JsonDocument.Parse(text);
        return ChatWire.Response(doc.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = ChatWire.Request(messages, options, Model(options), stream: true).ToJsonString();
        using var response = await _transport.SendAsync(() => Build(body, options),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var refused = Refused(response, error) ?? throw ErrorReader.ToException(response, error);
            foreach (var update in refused.ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var closeOnCancel = cancellationToken.Register(() => stream.Dispose());
        var assembler = new StreamAssembler();
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            foreach (var update in assembler.Read(sse))
            {
                yield return update;
            }
            if (assembler.Done)
            {
                break;
            }
        }
        foreach (var update in assembler.Flush())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        // The transport belongs to the FadenstackClient.
    }

    private string Model(ChatOptions? options) =>
        options?.ModelId ?? _options.Model ?? (_options.Agent is not null
            ? "agent" // the agent's model is the agent's; the field is required by the API
            : throw new InvalidOperationException("Set FadenstackClientOptions.Model or ChatOptions.ModelId."));

    private HttpRequestMessage Build(string body, ChatOptions? options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.GatewayEvents)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Events", "ag-ui");
        }
        var extra = options?.AdditionalProperties;
        var mode = extra?.TryGetValue(FadenstackChatOptions.ToolMode, out var m) == true
            ? m switch { ToolMode t => t, string s => ToolModes.ParseMode(s) ?? _options.ToolMode, _ => _options.ToolMode }
            : _options.ToolMode;
        request.Headers.TryAddWithoutValidation(ToolModes.Header, mode.ToWire());
        if (extra?.TryGetValue(FadenstackChatOptions.ConversationId, out var conversation) == true && conversation is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Conversation-Id", conversation.ToString());
        }
        if (extra?.TryGetValue(FadenstackChatOptions.TurnId, out var turn) == true && turn is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Turn-Id", turn.ToString());
        }
        return request;
    }

    /// <summary>A prompt the gateway refused (422 policy_blocked) is an answer, not an error.</summary>
    private static ChatResponse? Refused(HttpResponseMessage response, string body)
    {
        if (response.StatusCode != (HttpStatusCode)422)
        {
            return null;
        }
        var parsed = ErrorReader.Parse(body, "The prompt was refused.");
        if (parsed.Code != "policy_blocked")
        {
            return null;
        }
        var notice = parsed.Fadenstack is { } faden && faden.TryGetProperty("governance", out var g)
            ? ChatWire.Notice(g)
            : new GovernanceNotice { Outcome = "prompt_blocked", Stage = "input", Message = parsed.Message };
        if (string.IsNullOrEmpty(notice.Message))
        {
            notice = notice with { Message = parsed.Message };
        }
        var refused = new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>()));
        ChatWire.Refused(refused, notice);
        return refused;
    }

    /// <summary>Turns the chunks of a stream into updates; tool calls come out whole, at their end.</summary>
    private sealed class StreamAssembler
    {
        private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)> _calls = new();
        private string? _responseId;
        private string? _model;

        public bool Done { get; private set; }

        public IEnumerable<ChatResponseUpdate> Read(SseEvent sse)
        {
            if (sse.Name == "faden")
            {
                using var doc = JsonDocument.Parse(sse.Data);
                var content = new FadenstackEventContent(doc.RootElement);
                var update = Update(content);
                if (content.Name == "faden.governance" && content.Value.ValueKind == JsonValueKind.Object)
                {
                    var notice = ChatWire.Notice(content.Value);
                    update.Contents.Add(new GovernanceNoticeContent(notice));
                    update.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [FadenstackChatOptions.Outcome] = notice.Outcome,
                        [FadenstackChatOptions.Governance] = notice,
                    };
                }
                yield return update;
                yield break;
            }
            if (sse.Data == "[DONE]")
            {
                Done = true;
                yield break;
            }
            using var chunk = JsonDocument.Parse(sse.Data);
            var root = chunk.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && (!root.TryGetProperty("choices", out var c) || c.GetArrayLength() == 0))
            {
                throw new FadenstackStreamException(ChatWire.Str(error, "message") ?? "The answer broke off.",
                    ChatWire.Str(error, "code"));
            }
            _responseId ??= ChatWire.Str(root, "id");
            _model ??= ChatWire.Str(root, "model");
            if (ChatWire.Usage(root) is { } usage)
            {
                yield return Update(new UsageContent(usage));
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var reasoning = ChatWire.Str(delta, "reasoning_content") ?? ChatWire.Str(delta, "reasoning");
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        yield return Update(new TextReasoningContent(reasoning));
                    }
                    if (ChatWire.Str(delta, "content") is { Length: > 0 } text)
                    {
                        yield return Update(new TextContent(text));
                    }
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray())
                        {
                            Collect(call);
                        }
                    }
                }
                if (ChatWire.Finish(ChatWire.Str(choice, "finish_reason")) is { } finish)
                {
                    var update = Update();
                    update.Contents = Calls();
                    update.FinishReason = finish;
                    yield return update;
                }
            }
        }

        public IEnumerable<ChatResponseUpdate> Flush()
        {
            if (_calls.Count > 0)
            {
                var update = Update();
                update.Contents = Calls();
                yield return update;
            }
        }

        private void Collect(JsonElement call)
        {
            var index = call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
            if (!_calls.TryGetValue(index, out var entry))
            {
                entry = ("", "", new StringBuilder());
            }
            var id = ChatWire.Str(call, "id");
            var function = call.TryGetProperty("function", out var f) ? f : default;
            var name = function.ValueKind == JsonValueKind.Object ? ChatWire.Str(function, "name") : null;
            var arguments = function.ValueKind == JsonValueKind.Object ? ChatWire.Str(function, "arguments") : null;
            entry.Arguments.Append(arguments);
            _calls[index] = (string.IsNullOrEmpty(id) ? entry.Id : id!, string.IsNullOrEmpty(name) ? entry.Name : name!,
                entry.Arguments);
        }

        private IList<AIContent> Calls()
        {
            var calls = _calls.Values.Select(c => (AIContent)ChatWire.Call(c.Id, c.Name, c.Arguments.ToString())).ToList();
            _calls.Clear();
            return calls;
        }

        private ChatResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, contents.ToList())
        {
            ResponseId = _responseId,
            MessageId = _responseId,
            ModelId = _model,
        };
    }
}

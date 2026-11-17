using System.Text;
using System.Text.Json;
using Fadenstack.Governance;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Chat;

/// <summary>
/// Turns the chunks of a stream into updates; tool calls come out whole, at their end. Used for the gateway and, with
/// <c>gateway: false</c>, for a person's own endpoint (sdk/51 F03), which may be anything: it cannot speak the gateway's
/// own events (a forged refusal, approval or receipt), and what is not an OpenAI chunk ends the answer with a typed error.
/// </summary>
internal sealed class StreamAssembler
{
    /// <summary>The longest a tool call's arguments may grow, in characters (N9).</summary>
    internal const int MaxArgumentChars = 8 * 1024 * 1024;

    private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)> _calls = new();
    private readonly Dictionary<int, int> _slots = new();
    private readonly ILogger _log;
    private readonly bool _gateway;
    private string? _responseId;
    private string? _model;
    private int _current = -1;

    public StreamAssembler(ILogger log, bool gateway = true)
    {
        _log = log;
        _gateway = gateway;
    }

    public bool Done { get; private set; }

    public IEnumerable<ChatResponseUpdate> Read(SseEvent sse)
    {
        if (sse.Name is { Length: > 0 } && sse.Name != "message" && !_gateway)
        {
            yield break; // an endpoint's own named events (a ping, its progress) are no part of the answer
        }
        if (sse.Name == "faden")
        {
            using var doc = ParseEvent(sse.Data);
            if (doc is null)
            {
                yield break;
            }
            var content = new FadenstackEventContent(doc.RootElement);
            var update = Update(content);
            if (content.Name == ChatWire.ProvenanceEvent)
            {
                update.Contents.Add(ProvenanceReceiptContent.FromEvent(content.Value));
            }
            // Only a refusal refuses; a later outcome (observe, restrict, ...) stays an event for the session to report.
            if (content.Name == "faden.governance" && content.Value.ValueKind == JsonValueKind.Object
                && ChatWire.IsRefusal(ChatWire.Str(content.Value, "outcome")))
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
        using var chunk = Chunk(sse.Data);
        var root = chunk.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw NotAnAnswer();
        }
        var hasChoices = root.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;
        if (root.TryGetProperty("error", out var error) && !hasChoices)
        {
            // OpenAI's {"error": {...}}; Ollama's {"error": "..."}
            throw error.ValueKind == JsonValueKind.String
                ? new FadenstackStreamException(error.GetString() is { Length: > 0 } m ? m : "The answer broke off.", null)
                : new FadenstackStreamException(ChatWire.Str(error, "message") ?? "The answer broke off.", ChatWire.CodeText(error));
        }
        if (!_gateway && ChatWire.Str(root, "object") == "error" && !hasChoices)
        {
            // vLLM's and SGLang's {"object": "error", "message": ...}
            throw new FadenstackStreamException(ChatWire.Str(root, "message") ?? "The answer broke off.", ChatWire.Str(root, "type"));
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

    /// <summary>
    /// A <c>faden</c> event's JSON, or null (and a logged warning) when it is not an object: the stream goes on without
    /// it. The log says why, never what the event held: a parser's message can quote it.
    /// </summary>
    private JsonDocument? ParseEvent(string data)
    {
        try
        {
            var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc;
            }
            doc.Dispose();
        }
        catch (JsonException)
        {
            Log.EventSkipped(_log, "not valid JSON");
            return null;
        }
        Log.EventSkipped(_log, "not a JSON object");
        return null;
    }

    /// <summary>A chunk's JSON; what is not JSON at all ends the answer with a typed error, never a parser's exception.</summary>
    private static JsonDocument Chunk(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw NotAnAnswer(); // the parser's message could quote what was sent
        }
    }

    private static FadenstackStreamException NotAnAnswer() =>
        new("The answer broke off: the server sent something that is not part of an answer.", "invalid_stream");

    /// <summary>
    /// Adds a tool call's chunk. A chunk names its call by <c>index</c> (the gateway always does); one without an index belongs
    /// to the call before it unless it brings a new id, which starts the next call (servers that omit the index), and a new id
    /// under an index already used starts a new call too (review R2), so parallel calls are never merged into one.
    /// </summary>
    private void Collect(JsonElement call)
    {
        var id = ChatWire.Str(call, "id");
        var hasId = !string.IsNullOrEmpty(id);
        int slot;
        if (call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var wire))
        {
            if (!_slots.TryGetValue(wire, out slot))
            {
                slot = _calls.ContainsKey(wire) ? Next() : wire; // the gateway's own index whenever it is free
                _slots[wire] = slot;
            }
            else if (hasId && _calls[slot].Id is { Length: > 0 } known && known != id)
            {
                slot = Next();
                _slots[wire] = slot;
            }
        }
        else
        {
            slot = _current >= 0 && !(hasId && _calls[_current].Id is { Length: > 0 } known && known != id) ? _current : Next();
        }
        _current = slot;
        if (!_calls.TryGetValue(slot, out var entry))
        {
            entry = ("", "", new StringBuilder());
        }
        var function = call.TryGetProperty("function", out var f) ? f : default;
        var name = function.ValueKind == JsonValueKind.Object ? ChatWire.Str(function, "name") : null;
        var arguments = function.ValueKind == JsonValueKind.Object ? ChatWire.Str(function, "arguments") : null;
        if (entry.Arguments.Length + (arguments?.Length ?? 0) > MaxArgumentChars)
        {
            throw SseReader.TooLarge("a tool call's arguments");
        }
        entry.Arguments.Append(arguments);
        _calls[slot] = (hasId ? id! : entry.Id, string.IsNullOrEmpty(name) ? entry.Name : name!, entry.Arguments);
    }

    private int Next() => _calls.Count == 0 ? 0 : _calls.Keys.Max() + 1;

    private IList<AIContent> Calls()
    {
        _slots.Clear();
        _current = -1;
        var calls = _calls.Select(c => (AIContent)ChatWire.Call(c.Value.Id, c.Value.Name, c.Value.Arguments.ToString(), c.Key)).ToList();
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

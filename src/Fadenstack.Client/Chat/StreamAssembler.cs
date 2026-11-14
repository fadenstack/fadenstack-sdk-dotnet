using System.Text;
using System.Text.Json;
using Fadenstack.Governance;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Chat;

/// <summary>Turns the chunks of a stream into updates; tool calls come out whole, at their end.</summary>
internal sealed class StreamAssembler
{
    private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)> _calls = new();
    private readonly ILogger _log;
    private string? _responseId;
    private string? _model;

    public StreamAssembler(ILogger log) => _log = log;

    public bool Done { get; private set; }

    public IEnumerable<ChatResponseUpdate> Read(SseEvent sse)
    {
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

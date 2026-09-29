using System.Text.Json;
using Faden.Chat;
using Microsoft.Extensions.AI;

namespace Faden.Agents.Sessions;

/// <summary>
/// Builds a request's history from the transcript, with the session rule of 04 §8 applied on the device:
/// a refused prompt, a failed or cancelled turn is left out whole; of a refused answer only the question
/// stays; consecutive user messages are merged; and the oldest turns go when the history passes the budget.
/// </summary>
public sealed class ContextBuilder
{
    /// <summary>About this many tokens of history (4 characters a token); the newest turns are kept.</summary>
    public int TokenBudget { get; set; } = 24_000;

    public IList<ChatMessage> Build(IReadOnlyList<TranscriptEntry> entries)
    {
        var status = entries.Where(e => e.Type == EntryTypes.TurnEnd && e.TurnId is not null)
            .GroupBy(e => e.TurnId!).ToDictionary(g => g.Key, g => g.Last().Status ?? TurnStatus.Ok);
        var turns = new List<List<ChatMessage>>();
        string? currentTurn = null;
        foreach (var entry in entries)
        {
            if (entry.TurnId is null)
            {
                continue;
            }
            var turnStatus = status.TryGetValue(entry.TurnId, out var s) ? s : TurnStatus.Ok; // in progress: all of it
            if (turnStatus is TurnStatus.PromptBlocked or TurnStatus.Error or TurnStatus.Cancelled)
            {
                continue;
            }
            if (turnStatus == TurnStatus.ResponseBlocked && entry.Type != EntryTypes.UserMessage)
            {
                continue;
            }
            var message = ToMessage(entry);
            if (message is null)
            {
                continue;
            }
            if (entry.TurnId != currentTurn)
            {
                turns.Add(new List<ChatMessage>());
                currentTurn = entry.TurnId;
            }
            turns[turns.Count - 1].Add(message);
        }
        return Merge(Trim(turns));
    }

    private IEnumerable<ChatMessage> Trim(List<List<ChatMessage>> turns)
    {
        var kept = new List<List<ChatMessage>>();
        var budget = TokenBudget * 4L;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var size = turns[i].Sum(Size);
            if (kept.Count > 0 && size > budget)
            {
                break; // the newest turn always goes, whatever its size
            }
            budget -= size;
            kept.Insert(0, turns[i]);
        }
        return kept.SelectMany(t => t);
    }

    private static long Size(ChatMessage message) => message.Contents.Sum(c => c switch
    {
        TextContent t => t.Text?.Length ?? 0,
        FunctionCallContent f => 32 + JsonSerializer.Serialize(f.Arguments).Length,
        FunctionResultContent r => ChatWire.ResultText(r).Length,
        _ => 0,
    });

    /// <summary>Strict chat templates refuse roles that do not alternate: adjacent user messages become one.</summary>
    private static IList<ChatMessage> Merge(IEnumerable<ChatMessage> messages)
    {
        var merged = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (merged.Count > 0 && message.Role == ChatRole.User && merged[merged.Count - 1].Role == ChatRole.User)
            {
                var previous = merged[merged.Count - 1];
                merged[merged.Count - 1] = new ChatMessage(ChatRole.User, previous.Text + "\n\n" + message.Text);
                continue;
            }
            merged.Add(message);
        }
        return merged;
    }

    internal static ChatMessage? ToMessage(TranscriptEntry entry)
    {
        var data = entry.Data;
        switch (entry.Type)
        {
            case EntryTypes.UserMessage:
                return new ChatMessage(ChatRole.User, WithAttachments(data));
            case EntryTypes.AssistantMessage:
            {
                var contents = new List<AIContent>();
                if (Str(data, "text") is { Length: > 0 } text)
                {
                    contents.Add(new TextContent(text));
                }
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("tool_calls", out var calls))
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        contents.Add(ChatWire.Call(Str(call, "id") ?? "", Str(call, "name") ?? "", Str(call, "arguments")));
                    }
                }
                return contents.Count == 0 ? null : new ChatMessage(ChatRole.Assistant, contents);
            }
            case EntryTypes.ToolComplete:
                return new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent(Str(data, "call_id") ?? "", Str(data, "result") ?? ""),
                });
            default:
                return null;
        }
    }

    /// <summary>
    /// The user's text after what they attached, each in an <c>&lt;attachment&gt;</c> block the model can tell
    /// apart from the question.
    /// </summary>
    internal static string WithAttachments(JsonElement data)
    {
        var text = Str(data, "text") ?? "";
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("attachments", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return text;
        }
        var builder = new System.Text.StringBuilder();
        foreach (var a in list.EnumerateArray())
        {
            builder.Append("<attachment title=\"").Append(Attr(Str(a, "title"))).Append('"');
            foreach (var name in new[] { "kind", "source", "reference" })
            {
                if (Str(a, name) is { Length: > 0 } value)
                {
                    builder.Append(' ').Append(name).Append("=\"").Append(Attr(value)).Append('"');
                }
            }
            builder.Append(">\n").Append(Str(a, "content") ?? "").Append("\n</attachment>\n\n");
        }
        return builder.Append(text).ToString();
    }

    private static string Attr(string? value) => (value ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    private static string? Str(JsonElement e, string name) => ChatWire.Str(e, name);
}

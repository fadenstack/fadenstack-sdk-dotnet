using System.Text.Json;
using Fadenstack.Chat;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents.Sessions;

/// <summary>
/// Builds a request's history from the transcript, with the session rule applied on the device:
/// a refused prompt, a failed or cancelled turn is left out whole; of a refused answer only the question
/// stays; consecutive user messages are merged; and the oldest turns go when the history passes the budget.
/// Pictures the user attached go with the newest messages only (<see cref="MaxHistoryPictures"/>).
/// </summary>
public sealed class ContextBuilder
{
    /// <summary>
    /// Pictures the history carries at most, the newest: each costs the model about as much as a page of text, and the
    /// question is mostly about the last one. Older ones are named in their place.
    /// </summary>
    public const int MaxHistoryPictures = 4;

    /// <summary>What a picture counts for in the history budget, in characters (about a thousand tokens).</summary>
    private const int PictureCharacters = 4_000;

    /// <summary>About this many tokens of history (4 characters a token); the newest turns are kept.</summary>
    public int TokenBudget { get; set; } = 24_000;

    public IList<ChatMessage> Build(IReadOnlyList<TranscriptEntry> entries)
    {
        var status = entries.Where(e => e.Type == EntryTypes.TurnEnd && e.TurnId is not null)
            .GroupBy(e => e.TurnId!).ToDictionary(g => g.Key, g => g.Last().Status ?? TurnStatus.Ok);
        bool Included(TranscriptEntry entry)
        {
            var turnStatus = status.TryGetValue(entry.TurnId!, out var s) ? s : TurnStatus.Ok; // in progress: all of it
            if (turnStatus is TurnStatus.PromptBlocked or TurnStatus.Error or TurnStatus.Cancelled)
            {
                return false;
            }
            return turnStatus != TurnStatus.ResponseBlocked || entry.Type == EntryTypes.UserMessage;
        }
        var withPictures = NewestPictures(entries.Where(e => e.TurnId is not null && Included(e)).ToList());
        var turns = new List<List<ChatMessage>>();
        string? currentTurn = null;
        foreach (var entry in entries)
        {
            if (entry.TurnId is null || !Included(entry))
            {
                continue;
            }
            var message = ToMessage(entry, withPictures.Contains(entry.Id));
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

    /// <summary>
    /// The user messages whose pictures still go (by entry id): the newest, up to <see cref="MaxHistoryPictures"/> in all.
    /// The newest message with pictures keeps all of its own, however many.
    /// </summary>
    private static HashSet<string> NewestPictures(IReadOnlyList<TranscriptEntry> entries)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var left = MaxHistoryPictures;
        for (var i = entries.Count - 1; i >= 0 && left > 0; i--)
        {
            var entry = entries[i];
            if (entry.Type != EntryTypes.UserMessage)
            {
                continue;
            }
            var count = PicturesOf(entry.Data).Count;
            if (count == 0)
            {
                continue;
            }
            if (count > left && chosen.Count > 0)
            {
                break;
            }
            chosen.Add(entry.Id);
            left -= count;
        }
        return chosen;
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
        DataContent d when d.HasTopLevelMediaType("image") => PictureCharacters,
        FunctionCallContent f => 32 + JsonSerializer.Serialize(f.Arguments).Length,
        FunctionResultContent r => ChatWire.ResultText(r).Length,
        _ => 0,
    });

    /// <summary>
    /// Strict chat templates refuse roles that do not alternate: adjacent user messages become one. With pictures, their
    /// parts are kept in order, the text between two messages apart as it was.
    /// </summary>
    private static IList<ChatMessage> Merge(IEnumerable<ChatMessage> messages)
    {
        var merged = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (merged.Count > 0 && message.Role == ChatRole.User && merged[merged.Count - 1].Role == ChatRole.User)
            {
                var previous = merged[merged.Count - 1];
                merged[merged.Count - 1] = TextOnly(previous) && TextOnly(message)
                    ? new ChatMessage(ChatRole.User, previous.Text + "\n\n" + message.Text)
                    : new ChatMessage(ChatRole.User, JoinParts(previous.Contents, message.Contents));
                continue;
            }
            merged.Add(message);
        }
        return merged;
    }

    private static bool TextOnly(ChatMessage message) => message.Contents.All(c => c is TextContent);

    /// <summary>Two messages' parts as one, the text between them kept apart as it is between two messages.</summary>
    private static List<AIContent> JoinParts(IList<AIContent> first, IList<AIContent> second)
    {
        var parts = new List<AIContent>(first);
        var rest = second.AsEnumerable();
        if (parts.Count > 0 && parts[parts.Count - 1] is TextContent last && second.Count > 0 && second[0] is TextContent next)
        {
            parts[parts.Count - 1] = new TextContent(last.Text + "\n\n" + next.Text);
            rest = second.Skip(1);
        }
        parts.AddRange(rest);
        return parts;
    }

    /// <summary>
    /// One transcript entry as a message for the model, or null. A user message's pictures go as image parts before its
    /// text unless <paramref name="pictures"/> is false (an older message: they are named instead).
    /// </summary>
    internal static ChatMessage? ToMessage(TranscriptEntry entry, bool pictures = true)
    {
        var data = entry.Data;
        switch (entry.Type)
        {
            case EntryTypes.UserMessage:
            {
                var sent = pictures ? PicturesOf(data) : new List<DataContent>();
                var text = WithAttachments(data, pictures);
                if (sent.Count == 0)
                {
                    return new ChatMessage(ChatRole.User, text);
                }
                // The pictures first, then what was said about them: the question comes last, as with the other attachments.
                var contents = new List<AIContent>(sent) { new TextContent(text) };
                return new ChatMessage(ChatRole.User, contents);
            }
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
    /// apart from the question. A picture goes as an image part, not here; one that is no longer sent
    /// (<paramref name="pictures"/> false) is named.
    /// </summary>
    internal static string WithAttachments(JsonElement data, bool pictures = true)
    {
        var text = Str(data, "text") ?? "";
        var list = AttachmentsOf(data);
        if (list.Count == 0)
        {
            return text;
        }
        var builder = new System.Text.StringBuilder();
        foreach (var a in list)
        {
            var picture = MessageAttachment.IsPictureType(Str(a, "media_type"));
            if (picture && pictures && Picture(a) is not null)
            {
                continue; // an image part
            }
            builder.Append("<attachment title=\"").Append(Attr(Str(a, "title"))).Append('"');
            foreach (var name in new[] { "kind", "source", "reference" })
            {
                if (Str(a, name) is { Length: > 0 } value)
                {
                    builder.Append(' ').Append(name).Append("=\"").Append(Attr(value)).Append('"');
                }
            }
            var content = picture ? "[A picture the user attached earlier; it is not sent again.]" : Str(a, "content") ?? "";
            builder.Append(">\n").Append(content).Append("\n</attachment>\n\n");
        }
        return builder.Append(text).ToString();
    }

    private static List<JsonElement> AttachmentsOf(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : new List<JsonElement>();

    /// <summary>A user message's pictures, as the model gets them.</summary>
    private static List<DataContent> PicturesOf(JsonElement data) =>
        AttachmentsOf(data).Select(Picture).Where(p => p is not null).Select(p => p!).ToList();

    /// <summary>
    /// The attachment's picture, for the model: an <c>image/*</c> attachment whose content is a <c>data:image/</c> URL.
    /// Null for any other, and for one whose URL is broken (it is named instead: its content is never text to send).
    /// </summary>
    private static DataContent? Picture(JsonElement attachment)
    {
        if (!MessageAttachment.IsPictureType(Str(attachment, "media_type"))
            || Str(attachment, "content") is not { } url || !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            return new DataContent(url);
        }
        catch (Exception e) when (e is UriFormatException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string Attr(string? value) => (value ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    private static string? Str(JsonElement e, string name) => ChatWire.Str(e, name);
}

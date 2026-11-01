using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.ViewModels;

/// <summary>Reads a session's transcript back into turns, a page at a time.</summary>
internal static class TranscriptTurns
{
    /// <summary>The transcript's entries grouped by turn, oldest first.</summary>
    public static List<List<TranscriptEntry>> Group(IReadOnlyList<TranscriptEntry> entries)
    {
        var order = new List<List<TranscriptEntry>>();
        var byId = new Dictionary<string, List<TranscriptEntry>>();
        foreach (var entry in entries)
        {
            if (entry.TurnId is null)
            {
                continue;
            }
            if (!byId.TryGetValue(entry.TurnId, out var list))
            {
                byId[entry.TurnId] = list = new List<TranscriptEntry>();
                order.Add(list);
            }
            list.Add(entry);
        }
        return order;
    }

    public static TurnViewModel Build(AgentChatViewModel chat, List<TranscriptEntry> entries, int previewLength)
    {
        var user = entries.FirstOrDefault(e => e.Type == EntryTypes.UserMessage);
        var data = user?.Data ?? default;
        var turn = new TurnViewModel(chat, Json.Str(data, "text") ?? "", user?.Timestamp ?? entries[0].Timestamp,
            previewLength, TurnState.Completed)
        {
            Id = entries[0].TurnId,
            // The skills the user picked, then what they attached: as the chips were when it was sent.
            Attachments = Items(data, "skills").Select(s => new AttachmentChip(Json.Str(s, "name") ?? Json.Str(s, "slug") ?? "", null) { IsSkill = true })
                .Concat(Items(data, "attachments").Select(Chip))
                .ToList(),
        };
        var state = TurnState.Stopped; // a turn without an end was interrupted
        ChatOutcome? outcome = null;
        long? usage = null;
        // A card's class: from its own entry, or (written before entries kept it) from the call's start, which follows it.
        var startedClass = entries.Where(e => e.Type == EntryTypes.ToolStart && Json.Str(e.Data, "call_id") is { Length: > 0 })
            .GroupBy(e => Json.Str(e.Data, "call_id")!)
            .ToDictionary(g => g.Key, g => ToolModes.ParseClass(Json.Str(g.First().Data, "tool_class")));
        foreach (var entry in entries)
        {
            var d = entry.Data;
            switch (entry.Type)
            {
                case EntryTypes.AssistantMessage:
                    if (Json.Str(d, "text") is { Length: > 0 } text)
                    {
                        turn.AddRestoredText(text);
                    }
                    if (Json.Str(d, "origin") == "gateway" && d.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray())
                        {
                            turn.AddRestoredTool(Json.Str(call, "id") ?? "", Json.Str(call, "name") ?? "", "gateway", ToolClass.Read);
                        }
                    }
                    break;
                case EntryTypes.ToolStart:
                    turn.AddRestoredTool(Json.Str(d, "call_id") ?? "", Json.Str(d, "name") ?? "", Json.Str(d, "source") ?? "host",
                        ToolModes.ParseClass(Json.Str(d, "tool_class")));
                    break;
                case EntryTypes.ToolComplete:
                    var callId = Json.Str(d, "call_id") ?? "";
                    var name = Json.Str(d, "name") ?? "";
                    var part = turn.FindTool(callId, name) ?? turn.AddRestoredTool(callId, name, Json.Str(d, "source") ?? "host", ToolClass.Unknown);
                    part.Complete(Json.Str(d, "outcome") ?? "ok", Json.Str(d, "result"),
                        TimeSpan.FromMilliseconds(Json.Long(d, "duration_ms") ?? 0));
                    break;
                case EntryTypes.ApprovalDecision:
                    if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("asked", out var asked) && asked.ValueKind == JsonValueKind.False)
                    {
                        break; // let through without asking (this chat, always, bypass): no card, as it ran
                    }
                    var approvalCall = Json.Str(d, "call_id") ?? "";
                    var request = new ApprovalRequest
                    {
                        CallId = approvalCall,
                        ToolName = Json.Str(d, "name") ?? "",
                        Source = Json.Str(d, "source") ?? "host",
                        ToolClass = Json.Str(d, "tool_class") is { } wire ? ToolModes.ParseClass(wire)
                            : startedClass.TryGetValue(approvalCall, out var started) ? started : ToolClass.Unknown,
                        Server = Json.Str(d, "server"),
                    };
                    var decided = Json.Bool(d, "timed_out") ? ApprovalState.Expired
                        : !Json.Bool(d, "approved") ? ApprovalState.Denied
                        : Json.Str(d, "scope") == ApprovalScopes.Always ? ApprovalState.ApprovedAlways
                        : Json.Str(d, "scope") == ApprovalScopes.Time ? ApprovalState.ApprovedForTime
                        : Json.Bool(d, "for_session") ? ApprovalState.ApprovedForSession : ApprovalState.Approved;
                    turn.AddApprovalPart(new ApprovalPart(request, decided, previewLength, chat.Strings));
                    break;
                case EntryTypes.GovernanceNotice:
                    turn.AddRestoredNotice(new GovernanceNotice
                    {
                        Outcome = Json.Str(d, "outcome") ?? "",
                        Stage = Json.Str(d, "stage"),
                        Category = Json.Str(d, "category"),
                        Message = Json.Str(d, "message") ?? "",
                        DecisionId = Json.Str(d, "decision_id"),
                    });
                    break;
                case EntryTypes.TurnEnd:
                    state = entry.Status switch
                    {
                        TurnStatus.PromptBlocked => TurnState.Blocked,
                        TurnStatus.Error => TurnState.Failed,
                        TurnStatus.Cancelled => TurnState.Stopped,
                        _ => TurnState.Completed,
                    };
                    outcome = Enum.TryParse<ChatOutcome>(Json.Str(d, "outcome"), out var o) ? o : null;
                    if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("usage", out var u) && Json.Long(u, "total_tokens") is { } tokens and > 0)
                    {
                        usage = tokens;
                    }
                    break;
            }
        }
        turn.Restore(state, outcome, usage);
        return turn;
    }

    private static IEnumerable<JsonElement> Items(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>An attachment's chip; a picture's shows it small.</summary>
    private static AttachmentChip Chip(JsonElement attachment)
    {
        var content = Json.Str(attachment, "content");
        var picture = MessageAttachment.IsPictureType(Json.Str(attachment, "media_type"))
            && content is not null && content.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
        return new AttachmentChip(Json.Str(attachment, "title") ?? "", Json.Str(attachment, "kind")) { Image = picture ? content : null };
    }
}

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
        var turn = new TurnViewModel(chat, Json.Str(user?.Data ?? default, "text") ?? "", user?.Timestamp ?? entries[0].Timestamp,
            previewLength, TurnState.Completed)
        {
            Id = entries[0].TurnId,
            Attachments = user is not null && user.Data.ValueKind == JsonValueKind.Object && user.Data.TryGetProperty("attachments", out var list)
                && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(a => new AttachmentChip(Json.Str(a, "title") ?? "", Json.Str(a, "kind"))).ToList()
                : Array.Empty<AttachmentChip>(),
        };
        var state = TurnState.Stopped; // a turn without an end was interrupted
        ChatOutcome? outcome = null;
        var usage = "";
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
                    var request = new ApprovalRequest
                    {
                        CallId = Json.Str(d, "call_id") ?? "",
                        ToolName = Json.Str(d, "name") ?? "",
                        Source = Json.Str(d, "source") ?? "host",
                    };
                    var decided = Json.Bool(d, "timed_out") ? ApprovalState.Expired
                        : !Json.Bool(d, "approved") ? ApprovalState.Denied
                        : Json.Str(d, "scope") == ApprovalScopes.Always ? ApprovalState.ApprovedAlways
                        : Json.Bool(d, "for_session") ? ApprovalState.ApprovedForSession : ApprovalState.Approved;
                    turn.AddApprovalPart(new ApprovalPart(request, decided, previewLength));
                    break;
                case EntryTypes.GovernanceNotice:
                    turn.Parts.Add(NoticePart.From(new GovernanceNotice
                    {
                        Outcome = Json.Str(d, "outcome") ?? "",
                        Stage = Json.Str(d, "stage"),
                        Category = Json.Str(d, "category"),
                        Message = Json.Str(d, "message") ?? "",
                        DecisionId = Json.Str(d, "decision_id"),
                    }));
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
                        usage = $"{tokens:N0} tokens";
                    }
                    break;
            }
        }
        turn.Restore(state, outcome, usage);
        return turn;
    }
}

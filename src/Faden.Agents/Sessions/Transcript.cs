using System.Text.Json;

namespace Faden.Agents.Sessions;

/// <summary>
/// One line of a local session's transcript. The names are the same in both SDKs. The transcript is
/// the truth about a session: requests are built from it, and nothing of it is kept on the server.
/// </summary>
public sealed record TranscriptEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string? ParentId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>One of <see cref="EntryTypes"/>.</summary>
    public string Type { get; init; } = "";

    public string? TurnId { get; init; }

    /// <summary>For a turn's end: <see cref="TurnStatus"/>.</summary>
    public string? Status { get; init; }

    /// <summary>The entry's content: text, tool calls, results, notices.</summary>
    public JsonElement Data { get; init; }
}

public static class EntryTypes
{
    public const string SessionStart = "session.start";
    public const string UserMessage = "user.message";
    public const string TurnStart = "assistant.turn_start";
    public const string AssistantMessage = "assistant.message";
    public const string ToolStart = "tool.execution_start";
    public const string ToolComplete = "tool.execution_complete";
    public const string ApprovalDecision = "approval.decision";
    public const string GovernanceNotice = "governance.notice";
    public const string Compaction = "compaction";
    public const string TurnEnd = "assistant.turn_end";
}

/// <summary>How a turn ended; blocked, failed and cancelled turns are not sent to the model again.</summary>
public static class TurnStatus
{
    public const string Ok = "ok";
    public const string PromptBlocked = "prompt_blocked";
    public const string ResponseBlocked = "response_blocked";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}

/// <summary>A local session, as listed.</summary>
public sealed record SessionInfo(string Id, string Agent, DateTimeOffset Started, DateTimeOffset LastActivity, string? Title);

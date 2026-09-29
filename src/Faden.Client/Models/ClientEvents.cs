namespace Faden.Models;

/// <summary>
/// What the SDK reports about its work (02 §9, 06 §5): names, counts, durations, outcomes -- never
/// arguments, results or messages. The server refuses any other field.
/// </summary>
public abstract record AgentClientEvent
{
    public abstract string Type { get; }

    public DateTimeOffset? OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public string? ConversationId { get; init; }
}

/// <summary>A tool ran on the device.</summary>
public sealed record ToolExecutedEvent : AgentClientEvent
{
    public override string Type => "tool.executed";
    public string Tool { get; init; } = "";

    /// <summary><c>host</c>, <c>mcp</c> or <c>gateway</c>.</summary>
    public string? Source { get; init; }

    public string? Server { get; init; }
    public long DurationMs { get; init; }

    /// <summary><c>ok</c>, <c>error</c>, <c>denied</c> or <c>cancelled</c>.</summary>
    public string Outcome { get; init; } = "ok";
}

/// <summary>The user decided on a tool that needed approval.</summary>
public sealed record ApprovalDecidedEvent : AgentClientEvent
{
    public override string Type => "approval.decided";
    public string Tool { get; init; } = "";

    /// <summary><c>approved</c>, <c>denied</c> or <c>timeout</c>.</summary>
    public string Decision { get; init; } = "";

    public long? WaitMs { get; init; }
}

/// <summary>A local MCP server was attached, and the tools it offers.</summary>
public sealed record LocalMcpAttachedEvent : AgentClientEvent
{
    public override string Type => "local_mcp.attached";
    public string Label { get; init; } = "";
    public string? Command { get; init; }
    public string? Url { get; init; }
    public List<string> Tools { get; init; } = new();
}

/// <summary>Counts of one local session.</summary>
public sealed record SessionStatsEvent : AgentClientEvent
{
    public override string Type => "session.stats";
    public int Turns { get; init; }
    public int Compactions { get; init; }
    public int? RetentionSessions { get; init; }
    public int? RetentionDays { get; init; }
}

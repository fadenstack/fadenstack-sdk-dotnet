namespace Fadenstack.Models;

/// <summary>
/// What the SDK reports about its work: names, counts, durations, outcomes -- never
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

/// <summary>A call that needed approval was decided: by the user, or by an approval that stood (this chat, always, bypass).</summary>
public sealed record ApprovalDecidedEvent : AgentClientEvent
{
    public override string Type => "approval.decided";
    public string Tool { get; init; } = "";

    /// <summary><c>approved</c>, <c>denied</c> or <c>timeout</c>.</summary>
    public string Decision { get; init; } = "";

    /// <summary>How long the user took; null when nobody was asked.</summary>
    public long? WaitMs { get; init; }

    /// <summary>
    /// What let an approved call through: <c>once</c> (the user, for this call), <c>chat</c>, <c>always</c> or
    /// <c>bypass</c>. Null when it was not approved.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// When an approval for a while ends (UTC), set only with scope <c>time</c>. Sent only to a server whose profile
    /// advertises <c>approval_scopes.time</c>: the server refuses fields it does not know.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
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

/// <summary>
/// An agent handed a task to another agent on the device (<c>Fadenstack.Agents.Delegation</c>): who, to whom, how it ended
/// and how long it took -- never the task or the answer. A server accepts this kind only from a release that lists it
/// (<c>agent.delegated</c>); until then the delegation is counted by the tool's own <see cref="ToolExecutedEvent"/>
/// (tool <c>delegate</c>), and the SDK sends this one only when asked to.
/// </summary>
public sealed record AgentDelegatedEvent : AgentClientEvent
{
    public override string Type => "agent.delegated";

    /// <summary>The calling agent's slug.</summary>
    public string FromAgent { get; init; } = "";

    /// <summary>The slug of the agent the task went to.</summary>
    public string ToAgent { get; init; } = "";

    /// <summary>The delegation's id, to match the two agents' records.</summary>
    public string TaskId { get; init; } = "";

    /// <summary><c>completed</c>, <c>failed</c>, <c>canceled</c>, <c>rejected</c> or <c>not_running</c>.</summary>
    public string Outcome { get; init; } = "";

    public long DurationMs { get; init; }
}

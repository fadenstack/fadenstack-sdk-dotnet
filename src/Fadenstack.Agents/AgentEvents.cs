using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// What happens during a turn, in order, for a UI to render (AG-UI's vocabulary in our own types). Every event
/// names its turn.
/// </summary>
public abstract record AgentEvent
{
    public string TurnId { get; init; } = "";
}

public sealed record TurnStarted(ToolMode ToolMode) : AgentEvent;

public sealed record TextDelta(string Text) : AgentEvent;

public sealed record ReasoningDelta(string Text) : AgentEvent;

/// <summary>A tool starts: one of the agent's (<c>host</c>, <c>mcp</c>) or one the gateway runs (<c>gateway</c>).</summary>
public sealed record ToolCallStarted(string CallId, string Name, ToolClass Class, string Source,
    IReadOnlyDictionary<string, object?> Arguments) : AgentEvent;

/// <summary>A tool ended: <c>ok</c>, <c>error</c> or <c>denied</c>. A stopped turn throws instead.</summary>
public sealed record ToolCallCompleted(string CallId, string Name, string Source, string Outcome, string? Result,
    TimeSpan Duration) : AgentEvent
{
    /// <summary>The pictures the tool returned (a rendered slide), for the UI to show; sent to the model only if it sees.</summary>
    public IReadOnlyList<DataContent> Pictures { get; init; } = Array.Empty<DataContent>();
}

/// <summary>A call waits for the user; the <see cref="IApprovalHandler"/> is asked right after.</summary>
public sealed record ApprovalRequested(ApprovalRequest Request) : AgentEvent;

public sealed record ApprovalDecided(ApprovalRequest Request, ApprovalDecision Decision) : AgentEvent;

/// <summary>A refusal or a limit, to show on the turn.</summary>
public sealed record NoticeRaised(GovernanceNotice Notice) : AgentEvent;

/// <summary>Every gateway event as it came, also the ones turned into tool or notice events (citations, PII notice,
/// tool mode, and events this SDK does not know yet).</summary>
public sealed record GatewayEvent(FadenstackEventContent Event) : AgentEvent;

public sealed record TurnCompleted(AgentResult Result) : AgentEvent;

/// <summary>How a turn ended. A refusal is a result, not an exception.</summary>
public sealed record AgentResult
{
    public string TurnId { get; init; } = "";

    public ChatOutcome Outcome { get; init; }

    /// <summary>The answer's text (what was shown before a refusal, for a refused answer).</summary>
    public string Text { get; init; } = "";

    public IReadOnlyList<GovernanceNotice> Notices { get; init; } = Array.Empty<GovernanceNotice>();

    public UsageDetails? Usage { get; init; }

    /// <summary>The first notice that decided the outcome, if any.</summary>
    public GovernanceNotice? Notice => Notices.Count == 0 ? null : Notices[0];
}

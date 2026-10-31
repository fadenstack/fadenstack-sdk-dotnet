using Fadenstack.Agents.Registry;
using Fadenstack.Tools;

namespace Fadenstack.Agents.Delegation;

/// <summary>A task for another agent: who, what, and the short references the caller chooses to share.</summary>
public sealed record DelegationRequest(string Agent, string Task)
{
    /// <summary>Short strings that point at things the other agent can look up itself: record ids, file paths.</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}

/// <summary>How a delegation ended.</summary>
public enum DelegationOutcome
{
    /// <summary>The agent answered; <see cref="DelegationResult.Text"/> is its answer.</summary>
    Completed,

    /// <summary>The task failed (the agent went away, was refused by the server, ran out of time); the text says why.</summary>
    Failed,

    /// <summary>The caller cancelled it.</summary>
    Canceled,

    /// <summary>The agent would not take it (busy, or the task had no text); the text says why.</summary>
    Rejected,

    /// <summary>The agent is not running with the delegation host; nothing was sent.</summary>
    NotRunning,
}

/// <summary>What came back from a delegation. <see cref="Text"/> is the answer, or the reason it did not complete.</summary>
public sealed record DelegationResult(DelegationOutcome Outcome, string Text, string TaskId, TimeSpan Duration);

/// <summary>Progress of a delegated task, for the caller's UI.</summary>
public sealed record DelegationStatus(string TaskId, string Agent, string State, string? Text);

/// <summary>An approval the other agent's loop needs, for the caller's person to decide.</summary>
public sealed record DelegationApprovalRequest(string TaskId, string ApprovalId, string Agent, string AgentName, string Tool,
    ToolClass ToolClass, string Summary, string ArgumentsPreview);

/// <summary>What the caller supplies for a delegation: where its person answers, and where progress shows.</summary>
public sealed class DelegationCallbacks
{
    /// <summary>
    /// Asks the caller's person (the desktop routes it to its own approval card); returns their decision. Without it every
    /// approval is denied. <see cref="ApprovalDecision.ForSession"/> approves the tool for the rest of that task.
    /// </summary>
    public Func<DelegationApprovalRequest, CancellationToken, Task<ApprovalDecision>>? Approve { get; set; }

    /// <summary>Progress lines (<c>working</c>, a tool's name); called on a background thread.</summary>
    public Action<DelegationStatus>? Status { get; set; }
}

/// <summary>"Run this task with that agent" (design 01 section 8): the only thing a caller needs to know about another agent.</summary>
public interface IAgentDelegate
{
    /// <summary>
    /// Hands the task to the agent and returns when it ends. A cancelled token cancels the task and returns
    /// <see cref="DelegationOutcome.Canceled"/>. Never throws for the other agent's trouble: an agent that is not
    /// running, busy, gone or refused gives a result that says so.
    /// </summary>
    Task<DelegationResult> RunAsync(DelegationRequest request, DelegationCallbacks? callbacks = null, CancellationToken cancellationToken = default);

    /// <summary>The card of the agent if it is running and takes delegation now; null otherwise.</summary>
    AgentCardSummary? FindCard(string agent);
}

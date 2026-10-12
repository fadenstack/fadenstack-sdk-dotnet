namespace Faden.Governance;

/// <summary>
/// How a turn ended. A refusal is part of the result, not an exception: the user sees the notice on
/// the turn and goes on in the same session.
/// </summary>
public enum ChatOutcome
{
    Completed,

    /// <summary>An input check refused the prompt; nothing of it reached a model.</summary>
    PromptBlocked,

    /// <summary>An output check stopped the answer; tool calls in it are never run.</summary>
    ResponseBlocked,

    /// <summary>At least one tool call was refused (the answer may still be complete).</summary>
    ToolDenied,

    /// <summary>A tool call waits for the user's decision.</summary>
    ApprovalRequired,

    RateLimited,

    BudgetExceeded,
}

/// <summary>What the user is told about a refusal, and what an auditor finds it by.</summary>
public sealed record GovernanceNotice
{
    /// <summary><c>prompt_blocked</c>, <c>response_blocked</c>, <c>tool_denied</c>, <c>rate_limited</c>, ...</summary>
    public string Outcome { get; init; } = "";

    /// <summary><c>input</c>, <c>output</c>, <c>tool</c>.</summary>
    public string? Stage { get; init; }

    public string? Category { get; init; }

    /// <summary>The admin's message for the policy: what the user is told.</summary>
    public string Message { get; init; } = "";

    public string? DecisionId { get; init; }

    public string? RequestId { get; init; }

    /// <summary>For a rate limit or budget: when to try again.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

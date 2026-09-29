using Faden.Tools;

namespace Faden.Agents;

/// <summary>A tool call that waits for the user.</summary>
public sealed record ApprovalRequest
{
    public string CallId { get; init; } = "";

    public string ToolName { get; init; } = "";

    public ToolClass ToolClass { get; init; }

    /// <summary><c>host</c>, <c>mcp</c>, or <c>gateway</c> (a gateway tool the gateway holds for the user).</summary>
    public string Source { get; init; } = "host";

    /// <summary>Why it asks: the tool mode, or the admin's message for a tool rule.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The call's arguments, for the prompt (empty for gateway tools: the gateway keeps them).</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();
}

/// <summary>What the user decided.</summary>
public sealed record ApprovalDecision
{
    public bool Approved { get; init; }

    /// <summary>Approve this tool for the rest of the session without asking again.</summary>
    public bool ForSession { get; init; }

    public string? Reason { get; init; }

    public static ApprovalDecision Approve() => new() { Approved = true };

    public static ApprovalDecision ApproveForSession() => new() { Approved = true, ForSession = true };

    public static ApprovalDecision Deny(string? reason = null) => new() { Approved = false, Reason = reason };
}

/// <summary>Shows the prompt (a dialog, a card in the chat) and returns what the user decided.</summary>
public interface IApprovalHandler
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Nobody to ask: every call that needs approval is declined. The default.</summary>
public sealed class DenyAllApprovals : IApprovalHandler
{
    public static DenyAllApprovals Instance { get; } = new();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Deny("Nobody is here to approve it."));
}

internal sealed class DelegateApprovals : IApprovalHandler
{
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> _ask;

    public DelegateApprovals(Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> ask) => _ask = ask;

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        _ask(request, cancellationToken);
}

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

    /// <summary>Why it asks: the tool mode, or the admin's message for a rule (<see cref="RequiredByRule"/>).</summary>
    public string Reason { get; init; } = "";

    /// <summary>The call's arguments, for the prompt (empty for gateway tools: the gateway keeps them).</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Whether "Allow for this chat" may be offered. Never for a destructive or unclassified tool, nor for a call an
    /// admin's "Ask first" rule covers: those ask every time.
    /// </summary>
    public bool CanAllowForSession { get; init; }

    /// <summary>Whether "Allow always" may be offered: where "Allow for this chat" may.</summary>
    public bool CanAllowAlways { get; init; }

    /// <summary>An admin's "Ask first" rule covers the call: <see cref="Reason"/> is the admin's message. It asks every time.</summary>
    public bool RequiredByRule { get; init; }
}

/// <summary>What the user decided.</summary>
public sealed record ApprovalDecision
{
    public bool Approved { get; init; }

    /// <summary>
    /// Approve this tool for the rest of this session, in memory: not when the session is opened again. Taken only
    /// where the request offers it (<see cref="ApprovalRequest.CanAllowForSession"/>); otherwise it approves this call only.
    /// </summary>
    public bool ForSession { get; init; }

    /// <summary>
    /// Approve this tool on this device until the approvals are reset (<see cref="FadenAgent.ResetApprovalsAsync"/>).
    /// Taken only where the request offers it (<see cref="ApprovalRequest.CanAllowAlways"/>); otherwise it approves
    /// this call only.
    /// </summary>
    public bool Always { get; init; }

    public string? Reason { get; init; }

    public static ApprovalDecision Approve() => new() { Approved = true };

    public static ApprovalDecision ApproveForSession() => new() { Approved = true, ForSession = true };

    public static ApprovalDecision ApproveAlways() => new() { Approved = true, Always = true };

    public static ApprovalDecision Deny(string? reason = null) => new() { Approved = false, Reason = reason };
}

/// <summary>
/// Whether the agent asks (<see cref="Ask"/>, the default) or approves every call it would ask about
/// (<see cref="Bypass"/>). Set in the app's code (<see cref="AgentBuilder.UsePermissionMode"/>), never by a user.
/// </summary>
public enum PermissionMode
{
    /// <summary>Calls the tool mode asks about wait for the user.</summary>
    Ask = 0,

    /// <summary>
    /// Calls the tool mode asks about are approved without asking, destructive and unclassified ones too; an admin's
    /// "Ask first" rule still asks. For development and trusted automation.
    /// </summary>
    Bypass,
}

/// <summary>What let a call that needed approval through: the transcript's and the server's <c>scope</c>.</summary>
public static class ApprovalScopes
{
    /// <summary>The user approved this call.</summary>
    public const string Once = "once";

    /// <summary>The user approved the tool for this chat.</summary>
    public const string Chat = "chat";

    /// <summary>The user approved the tool always, on this device.</summary>
    public const string Always = "always";

    /// <summary>The agent runs in <see cref="PermissionMode.Bypass"/>.</summary>
    public const string Bypass = "bypass";
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

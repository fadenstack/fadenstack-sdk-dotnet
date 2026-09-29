using Faden.Agents;

namespace Faden.UI.Services;

/// <summary>A call waiting for the user; resolved by the approval card's buttons.</summary>
public sealed class PendingApproval
{
    private readonly TaskCompletionSource<ApprovalDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingApproval(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        CancellationToken = cancellationToken;
    }

    public ApprovalRequest Request { get; }

    /// <summary>Cancelled when the turn is stopped or the wait runs out.</summary>
    public CancellationToken CancellationToken { get; }

    internal Task<ApprovalDecision> Decision => _decision.Task;

    public bool IsResolved => _decision.Task.IsCompleted;

    public bool Resolve(ApprovalDecision decision) => _decision.TrySetResult(decision);

    internal void Cancel() => _decision.TrySetCanceled();
}

/// <summary>
/// The agent's <see cref="IApprovalHandler"/> for a UI: each request becomes an approval card in the chat, and
/// the user's click is the decision. Give the same broker to the builder and to the chat view model.
/// Without a chat listening, requests are declined.
/// </summary>
public sealed class ApprovalBroker : IApprovalHandler
{
    public event Action<PendingApproval>? Requested;

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var handlers = Requested;
        if (handlers is null)
        {
            return ApprovalDecision.Deny("Nobody is here to approve it.");
        }
        var pending = new PendingApproval(request, cancellationToken);
        using (cancellationToken.Register(pending.Cancel))
        {
            handlers(pending);
            return await pending.Decision.ConfigureAwait(false);
        }
    }
}

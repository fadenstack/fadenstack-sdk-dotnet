using Fadenstack.Agents;

namespace Fadenstack.UI.Services;

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

/// <summary>A grant card waiting for the user; resolved by its buttons.</summary>
public sealed class PendingGrant
{
    private readonly TaskCompletionSource<GrantAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingGrant(GrantOffer offer, CancellationToken cancellationToken)
    {
        Offer = offer;
        CancellationToken = cancellationToken;
    }

    public GrantOffer Offer { get; }

    public CancellationToken CancellationToken { get; }

    internal Task<GrantAnswer> Answer => _answer.Task;

    public bool IsResolved => _answer.Task.IsCompleted;

    public bool Resolve(GrantAnswer answer) => _answer.TrySetResult(answer);

    /// <summary>Whether a chat took the card to draw (a Bypass confirmation is drawn only by its conversation's chat).</summary>
    public bool IsClaimed { get; private set; }

    /// <summary>A chat draws this card: call from <see cref="ApprovalBroker.GrantRequested"/>, before it returns.</summary>
    public void Claim() => IsClaimed = true;

    internal void Cancel() => _answer.TrySetCanceled();
}

/// <summary>
/// The agent's <see cref="IApprovalHandler"/> and <see cref="IGrantHandler"/> for a UI: each request becomes a card in
/// the chat -- an approval, or a grant to work alongside the user -- and the user's click is the answer. Give the same
/// broker to the builder and to the chat view model. Without a chat listening, requests are declined.
/// </summary>
public sealed class ApprovalBroker : IApprovalHandler, IGrantHandler, IStepUpHandler
{
    public event Action<PendingApproval>? Requested;

    /// <summary>
    /// How the second sign-in stands (sdk/41): the page is open in the browser, approved, denied, timed out, failed or
    /// cancelled. Raised on the thread that reports it; the chat that draws the Bypass card takes it from there.
    /// </summary>
    public event Action<StepUpRequest, StepUpStage>? StepUpChanged;

    /// <summary>
    /// Opens a page in the person's browser; false when it could not. The system browser unless an app (the desktop) or a
    /// test sets its own.
    /// </summary>
    public Func<Uri, bool> OpenBrowser { get; set; } = SystemBrowser.Open;

    public Task<bool> OpenAsync(StepUpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OpenBrowser(request.Link));

    public void Report(StepUpRequest request, StepUpStage stage) => StepUpChanged?.Invoke(request, stage);

    /// <summary>
    /// A grant card to show (<see cref="ChatSession.OfferWorkAsync"/>, an extension), or the Bypass confirmation
    /// (<see cref="GrantOffer.Bypass"/>, <see cref="ChatSession.StartBypassAsync"/>), which the chat of its conversation
    /// draws.
    /// </summary>
    public event Action<PendingGrant>? GrantRequested;

    public async Task<GrantAnswer> RequestGrantAsync(GrantOffer offer, CancellationToken cancellationToken)
    {
        var handlers = GrantRequested;
        if (handlers is null)
        {
            return GrantAnswer.NotNow;
        }
        var pending = new PendingGrant(offer, cancellationToken);
        using (cancellationToken.Register(pending.Cancel))
        {
            handlers(pending);
            if (offer.Bypass && !pending.IsClaimed)
            {
                return GrantAnswer.NotNow; // no chat of its conversation draws it: nobody can confirm it (sdk/09 L-58)
            }
            return await pending.Answer.ConfigureAwait(false);
        }
    }

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

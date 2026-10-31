using System.IO.Pipes;
using Fadenstack.Agents.Registry;

namespace Fadenstack.Agents.Delegation;

/// <summary>The agent could not be reached on its pipe (not running any more, or a stale registration).</summary>
internal sealed class AgentUnreachableException : Exception
{
    public AgentUnreachableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// The caller's end of the local pipe: connects to an embedded agent, presents the launch's secret, sends one task
/// and follows it to its end (status, approvals, artifact), as the wire in <c>PipeWire</c> describes.
/// </summary>
internal sealed class PipeDelegationClient : IDelegationClient
{
    private readonly int _maxBytes;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _cancelGrace;

    public PipeDelegationClient(int maxMessageBytes = FrameStream.DefaultMaxBytes, TimeSpan? connectTimeout = null,
        TimeSpan? handshakeTimeout = null, TimeSpan? cancelGrace = null)
    {
        _maxBytes = maxMessageBytes;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _cancelGrace = cancelGrace ?? TimeSpan.FromSeconds(5);
    }

    public async Task<TaskOutcome> RunAsync(AgentDelegationEndpoint endpoint, TaskRequest request, ITaskObserver observer,
        CancellationToken cancellationToken)
    {
        using var pipe = DelegationPipes.CreateClient(endpoint.Pipe);
        try
        {
            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new AgentUnreachableException("The agent does not answer on its pipe.", e);
        }
        var frames = new FrameStream(pipe, _maxBytes);
        try
        {
            await frames.WriteAsync(new Frame { Type = FrameTypes.Hello, Version = 1, Secret = endpoint.Secret }, cancellationToken).ConfigureAwait(false);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(_handshakeTimeout);
                Frame? ready;
                try
                {
                    ready = await frames.ReadAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ready = null;
                }
                if (ready is not { Type: FrameTypes.Ready })
                {
                    return new TaskOutcome(request.TaskId, TaskState.Failed, "The agent refused the connection.");
                }
            }
            await frames.WriteAsync(new Frame
            {
                Type = FrameTypes.TaskStart, TaskId = request.TaskId,
                Message = new FrameMessage { Text = request.Text, References = request.References.ToList() },
                From = new FrameParty { Slug = request.FromSlug, Name = request.FromName },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WireException e)
        {
            return new TaskOutcome(request.TaskId, TaskState.Failed, e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return new TaskOutcome(request.TaskId, TaskState.Failed, "The agent closed the connection.");
        }
        return await FollowAsync(frames, request, observer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskOutcome> FollowAsync(FrameStream frames, TaskRequest request, ITaskObserver observer, CancellationToken cancellationToken)
    {
        using var hard = new CancellationTokenSource(); // ends the wait when a cancelled task does not answer
        using var done = new CancellationTokenSource(); // ends pending approvals
        using var cancelling = cancellationToken.Register(() =>
        {
            _ = frames.WriteAsync(new Frame { Type = FrameTypes.TaskCancel, TaskId = request.TaskId }, CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default); // the agent may be gone already
            hard.CancelAfter(_cancelGrace);
        });
        string? artifact = null;
        try
        {
            while (await frames.ReadAsync(hard.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.TaskId != request.TaskId)
                {
                    continue;
                }
                switch (frame.Type)
                {
                    case FrameTypes.TaskArtifact:
                        artifact = frame.Text;
                        break;
                    case FrameTypes.TaskApproval:
                        _ = AnswerApprovalAsync(frames, request.TaskId, frame, observer, done.Token);
                        break;
                    case FrameTypes.TaskStatus when TaskStates.TryParse(frame.State, out var state):
                        observer.Status(state, frame.Text);
                        if (state.IsFinal())
                        {
                            return new TaskOutcome(request.TaskId, state, state == TaskState.Completed ? artifact ?? "" : frame.Text);
                        }
                        break;
                }
            }
            return cancellationToken.IsCancellationRequested
                ? new TaskOutcome(request.TaskId, TaskState.Canceled, "The task was canceled.")
                : new TaskOutcome(request.TaskId, TaskState.Failed, "The agent closed the connection before the task ended.");
        }
        catch (OperationCanceledException)
        {
            return new TaskOutcome(request.TaskId, TaskState.Canceled, "The task was canceled.");
        }
        catch (WireException e)
        {
            return new TaskOutcome(request.TaskId, TaskState.Failed, e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return cancellationToken.IsCancellationRequested
                ? new TaskOutcome(request.TaskId, TaskState.Canceled, "The task was canceled.")
                : new TaskOutcome(request.TaskId, TaskState.Failed, "The agent closed the connection before the task ended.");
        }
        finally
        {
            done.Cancel();
        }
    }

    /// <summary>Asks the caller's person and sends the answer; a person who cannot be asked denies.</summary>
    private static async Task AnswerApprovalAsync(FrameStream frames, string taskId, Frame ask, ITaskObserver observer, CancellationToken cancellationToken)
    {
        var choice = ApprovalChoice.Deny;
        try
        {
            choice = await observer.ApprovalAsync(new ApprovalAsk(ask.ApprovalId ?? "", ask.Tool ?? "", ask.ToolClass ?? "",
                ask.Summary ?? "", ask.ArgumentsPreview ?? ""), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // denied: nobody could decide
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await frames.WriteAsync(new Frame
            {
                Type = FrameTypes.TaskApprove, TaskId = taskId, ApprovalId = ask.ApprovalId,
                Decision = choice == ApprovalChoice.Once ? "once" : choice == ApprovalChoice.Chat ? "chat" : "deny",
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or WireException)
        {
        }
    }
}

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
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
/// The caller's end of the local pipe: connects to an embedded agent, presents the launch's secret, and speaks A2A 1.0
/// JSON-RPC over the pipe with the library's models: <c>SendStreamingMessage</c> for the task and for each approval answer,
/// <c>CancelTask</c> to cancel. (The library's HTTP client is not used: this is a small pipe client.)
/// </summary>
internal sealed class PipeDelegationClient : IDelegationClient
{
    private static readonly JsonTypeInfo RequestInfo = A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.JsonRpcRequest));
    private static readonly JsonTypeInfo ResponseInfo = A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.JsonRpcResponse));

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

    /// <summary>The agent's card, by the A2A <c>GetExtendedAgentCard</c> method.</summary>
    public async Task<A2A.AgentCard> GetAgentCardAsync(AgentDelegationEndpoint endpoint, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentUnreachableException("The agent refused the connection.");
        var response = await connection.CallAsync(A2A.A2AMethods.GetExtendedAgentCard, new A2A.GetExtendedAgentCardRequest(), cancellationToken).ConfigureAwait(false);
        return response.Result is { } result ? (A2A.AgentCard)result.Deserialize(A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.AgentCard)))!
            : throw new InvalidOperationException(response.Error?.Message);
    }

    public async Task<TaskOutcome> RunAsync(AgentDelegationEndpoint endpoint, TaskRequest request, ITaskObserver observer,
        CancellationToken cancellationToken)
    {
        Connection? connection;
        try
        {
            connection = await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (WireException e)
        {
            return new TaskOutcome(request.TaskId, TaskState.Failed, e.Message);
        }
        if (connection is null)
        {
            return new TaskOutcome(request.TaskId, TaskState.Failed, "The agent refused the connection.");
        }
        await using (connection)
        {
            return await new Run(this, connection, request, observer, cancellationToken).FollowAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Connects, presents the secret, waits for ready; null when the agent refuses.</summary>
    private async Task<Connection?> ConnectAsync(AgentDelegationEndpoint endpoint, CancellationToken cancellationToken)
    {
        var pipe = DelegationPipes.CreateClient(endpoint.Pipe);
        try
        {
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
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                    return null;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return null;
            }
            var connection = new Connection(pipe, frames);
            pipe = null!;
            return connection;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>A connection after the handshake: JSON-RPC requests out, responses routed to the request that asked.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly FrameStream _frames;
        private readonly ConcurrentDictionary<string, Channel<A2A.JsonRpcResponse>> _waiting = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reading;
        private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _next;
        private string? _failure;

        public Connection(NamedPipeClientStream pipe, FrameStream frames)
        {
            _pipe = pipe;
            _frames = frames;
            _reading = Task.Run(ReadAsync);
        }

        /// <summary>Completes when the connection has ended.</summary>
        public Task Closed => _closed.Task;

        /// <summary>Why the connection ended, when it did.</summary>
        public string? Failure => Volatile.Read(ref _failure);

        private async Task ReadAsync()
        {
            try
            {
                while (await _frames.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    var response = (A2A.JsonRpcResponse?)JsonSerializer.Deserialize(line, ResponseInfo);
                    if (response?.Id.ToString() is { } id && _waiting.TryGetValue(id, out var channel))
                    {
                        channel.Writer.TryWrite(response);
                    }
                }
                _failure ??= "The agent closed the connection before the task ended.";
            }
            catch (WireException e)
            {
                _failure = e.Message;
            }
            catch (JsonException)
            {
                _failure = "The agent sent a message that is not valid JSON-RPC.";
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _failure ??= "The agent closed the connection before the task ended.";
            }
            foreach (var channel in _waiting.Values)
            {
                channel.Writer.TryComplete();
            }
            _closed.TrySetResult(true);
        }

        /// <summary>Sends a request; the channel gets its responses (one for a plain call, one per event for a stream).</summary>
        public async Task<Channel<A2A.JsonRpcResponse>> SendAsync<T>(string method, T parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var channel = Channel.CreateUnbounded<A2A.JsonRpcResponse>();
            _waiting[id] = channel;
            var request = new A2A.JsonRpcRequest
            {
                Id = new A2A.JsonRpcId(id), Method = method,
                Params = JsonSerializer.SerializeToElement(parameters, A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))),
            };
            await _frames.WriteLineAsync(JsonSerializer.Serialize(request, RequestInfo), cancellationToken).ConfigureAwait(false);
            return channel;
        }

        public async Task<A2A.JsonRpcResponse> CallAsync<T>(string method, T parameters, CancellationToken cancellationToken)
        {
            var channel = await SendAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            try
            {
                return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new IOException(Failure ?? "The agent closed the connection.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _pipe.Dispose();
            try
            {
                await _reading.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
            _stop.Dispose();
        }
    }

    /// <summary>One delegated task, followed from its first message to its end.</summary>
    private sealed class Run
    {
        private readonly PipeDelegationClient _owner;
        private readonly Connection _connection;
        private readonly TaskRequest _request;
        private readonly ITaskObserver _observer;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<bool> _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private string? _taskId, _contextId;
        private bool _cancelSent;

        public Run(PipeDelegationClient owner, Connection connection, TaskRequest request, ITaskObserver observer, CancellationToken cancellationToken)
        {
            (_owner, _connection, _request, _observer, _cancellationToken) = (owner, connection, request, observer, cancellationToken);
        }

        public async Task<TaskOutcome> FollowAsync()
        {
            using var registration = _cancellationToken.Register(() => _ = CancelAsync());
            try
            {
                var message = new A2A.Message
                {
                    Role = A2A.Role.User, MessageId = Guid.NewGuid().ToString("N"),
                    Parts = new List<A2A.Part> { A2A.Part.FromText(_request.Text) },
                    Metadata = new Dictionary<string, JsonElement>
                    {
                        [A2AParts.MetadataKey] = JsonSerializer.SerializeToElement(new
                        {
                            from = new { slug = _request.FromSlug, name = _request.FromName }, references = _request.References,
                        }),
                    },
                };
                var artifact = new System.Text.StringBuilder();
                while (true)
                {
                    var step = await StreamAsync(message, artifact).ConfigureAwait(false);
                    if (step.Outcome is { } outcome)
                    {
                        return outcome;
                    }
                    var choice = await DecideAsync(step.Ask!).ConfigureAwait(false);
                    if (_connection.Closed.IsCompleted && !_cancellationToken.IsCancellationRequested)
                    {
                        return new TaskOutcome(_request.TaskId, TaskState.Failed, _connection.Failure ?? "The agent closed the connection.");
                    }
                    if (choice is null)
                    {
                        return Canceled();
                    }
                    message = new A2A.Message
                    {
                        Role = A2A.Role.User, MessageId = Guid.NewGuid().ToString("N"), TaskId = _taskId, ContextId = _contextId,
                        Parts = new List<A2A.Part>
                        {
                            A2AParts.Data(new { approvalId = step.Ask!.ApprovalId, decision = A2AParts.Decision(choice.Value) }),
                        },
                    };
                }
            }
            catch (WireException e)
            {
                return new TaskOutcome(_request.TaskId, TaskState.Failed, e.Message);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return _cancellationToken.IsCancellationRequested ? Canceled() : new TaskOutcome(_request.TaskId, TaskState.Failed, _connection.Failure ?? "The agent closed the connection.");
            }
            catch (OperationCanceledException)
            {
                return Canceled();
            }
        }

        private TaskOutcome Canceled() => new(_request.TaskId, TaskState.Canceled, "The task was canceled.");

        private sealed record Step(TaskOutcome? Outcome, ApprovalAsk? Ask);

        /// <summary>One streaming request: reads its events until the task ends or needs an approval.</summary>
        private async Task<Step> StreamAsync(A2A.Message message, System.Text.StringBuilder artifact)
        {
            var channel = await _connection.SendAsync(A2A.A2AMethods.SendStreamingMessage, new A2A.SendMessageRequest { Message = message }, _cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                A2A.JsonRpcResponse response;
                try
                {
                    var read = channel.Reader.ReadAsync(CancellationToken.None).AsTask();
                    // A cancelled task answers its cancel request; a stream that goes quiet after that is over.
                    var first = await Task.WhenAny(read, _canceled.Task).ConfigureAwait(false);
                    if (first != read)
                    {
                        return new Step(Canceled(), null);
                    }
                    response = await read.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return new Step(_cancellationToken.IsCancellationRequested ? Canceled()
                        : new TaskOutcome(_request.TaskId, TaskState.Failed, _connection.Failure ?? "The agent closed the connection before the task ended."), null);
                }
                if (response.Error is { } error)
                {
                    return new Step(new TaskOutcome(_request.TaskId, TaskState.Failed, error.Message), null);
                }
                var item = response.Result?.Deserialize(A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.StreamResponse))) as A2A.StreamResponse;
                if (item is null)
                {
                    continue;
                }
                if (Note(item, artifact) is { } step)
                {
                    return step;
                }
            }
        }

        /// <summary>Takes one event in; a step when it ends the stream (an approval is asked, or the task ended).</summary>
        private Step? Note(A2A.StreamResponse item, System.Text.StringBuilder artifact)
        {
            if (item.Task is { } task)
            {
                SetIds(task.Id, task.ContextId);
            }
            if (item.ArtifactUpdate is { } update)
            {
                SetIds(update.TaskId, update.ContextId);
                foreach (var part in update.Artifact.Parts.Where(p => p.Text is not null))
                {
                    artifact.Append(part.Text);
                }
            }
            if (item.Message is { } reply)
            {
                // an agent that answers with a message and no task: that is the answer
                return new Step(new TaskOutcome(_request.TaskId, TaskState.Completed, A2AParts.Text(reply) ?? ""), null);
            }
            // Only status updates count: a stream that follows a task on starts with a snapshot of its last stored state.
            var status = item.StatusUpdate?.Status;
            if (item.StatusUpdate is { } statusUpdate)
            {
                SetIds(statusUpdate.TaskId, statusUpdate.ContextId);
            }
            if (status is null)
            {
                return null;
            }
            var text = A2AParts.Text(status.Message);
            switch (status.State)
            {
                case A2A.TaskState.InputRequired:
                    var data = status.Message?.Parts.Select(p => p.Data).FirstOrDefault(d => d is not null) ?? default;
                    var ask = new ApprovalAsk(A2AParts.String(data, "approvalId"), A2AParts.String(data, "tool"), A2AParts.String(data, "toolClass"),
                        A2AParts.String(data, "summary"), A2AParts.String(data, "argumentsPreview"));
                    _observer.Status(TaskState.InputRequired, text);
                    return new Step(null, ask);
                case A2A.TaskState.Completed:
                    _observer.Status(TaskState.Completed, null);
                    return new Step(new TaskOutcome(_request.TaskId, TaskState.Completed, artifact.ToString()), null);
                case A2A.TaskState.Failed:
                case A2A.TaskState.Rejected:
                case A2A.TaskState.Canceled:
                    var state = status.State == A2A.TaskState.Failed ? TaskState.Failed : status.State == A2A.TaskState.Rejected ? TaskState.Rejected : TaskState.Canceled;
                    _observer.Status(state, text);
                    return new Step(new TaskOutcome(_request.TaskId, state, text ?? (state == TaskState.Canceled ? "The task was canceled." : "")), null);
                case A2A.TaskState.Working:
                    _observer.Status(TaskState.Working, text);
                    return null;
                default:
                    return null;
            }
        }

        private void SetIds(string taskId, string contextId)
        {
            lock (_gate)
            {
                _taskId ??= taskId;
                _contextId ??= contextId;
            }
            if (_cancellationToken.IsCancellationRequested)
            {
                _ = CancelAsync();
            }
        }

        /// <summary>The caller's person decides; a cancel while they are asked ends the wait (null).</summary>
        private async Task<ApprovalChoice?> DecideAsync(ApprovalAsk ask)
        {
            using var done = new CancellationTokenSource();
            var deciding = Task.Run(async () =>
            {
                try
                {
                    return await _observer.ApprovalAsync(ask, done.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return ApprovalChoice.Deny; // nobody could decide
                }
            });
            var first = await Task.WhenAny(deciding, _canceled.Task, _connection.Closed).ConfigureAwait(false);
            if (first != deciding)
            {
                done.Cancel(); // cancelled, or the agent went away while the person was asked
                return null;
            }
            return await deciding.ConfigureAwait(false);
        }

        /// <summary>The A2A cancel method; the task ending canceled is the answer.</summary>
        private async Task CancelAsync()
        {
            string? id;
            lock (_gate)
            {
                id = _taskId;
                if (id is null || _cancelSent)
                {
                    return; // not known yet: the first event of the task sends it
                }
                _cancelSent = true;
            }
            try
            {
                using var limit = new CancellationTokenSource(_owner._cancelGrace);
                var response = await _connection.CallAsync(A2A.A2AMethods.CancelTask, new A2A.CancelTaskRequest { Id = id }, limit.Token).ConfigureAwait(false);
                if (response.Error is null)
                {
                    _canceled.TrySetResult(true);
                }
                // an error (the task already ended): its last event arrives on the stream
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or WireException)
            {
                _canceled.TrySetResult(true); // the agent is gone or silent: nothing more will come
            }
        }
    }
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Fadenstack.Agents.Registry;

namespace Fadenstack.Agents.Delegation;

/// <summary>
/// Pipes for delegation: created for the current OS user only. On .NET 8 with <c>PipeOptions.CurrentUserOnly</c>; on
/// .NET Framework (the netstandard2.0 build) with a <c>PipeSecurity</c> that names the current user's SID alone.
/// </summary>
internal static class DelegationPipes
{
    public const string Machine = ".";

    public static NamedPipeServerStream CreateServer(string name, int maxInstances)
    {
#if NET8_0_OR_GREATER
        return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // A netstandard2.0 build on another system is not a supported host; the pipe is still found only through the owner-only registry.
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        return CreateWindowsServer(name, maxInstances);
#endif
    }

#if !NET8_0_OR_GREATER
    private static NamedPipeServerStream CreateWindowsServer(string name, int maxInstances)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        // .NET Framework has a constructor that takes the PipeSecurity at creation (no moment with the default ACL, which lets
        // Everyone read); the netstandard2.0 reference lacks it, so it is called by reflection, and the ACL is set
        // afterwards where only another runtime has no such constructor.
        var constructor = typeof(NamedPipeServerStream).GetConstructor(new[]
        {
            typeof(string), typeof(PipeDirection), typeof(int), typeof(PipeTransmissionMode), typeof(PipeOptions),
            typeof(int), typeof(int), typeof(PipeSecurity),
        });
        if (constructor is not null)
        {
            return (NamedPipeServerStream)constructor.Invoke(new object[]
            {
                name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security,
            });
        }
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        pipe.SetAccessControl(security);
        return pipe;
    }
#endif

    public static NamedPipeClientStream CreateClient(string name)
    {
#if NET8_0_OR_GREATER
        return new NamedPipeClientStream(Machine, name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            System.Security.Principal.TokenImpersonationLevel.Identification);
#else
        return new NamedPipeClientStream(Machine, name, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
#endif
    }

    /// <summary>A random token (hex) for pipe names and launch secrets.</summary>
    public static string RandomHex(int bytes)
    {
        var data = new byte[bytes];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(data);
        var text = new char[bytes * 2];
        for (var i = 0; i < bytes; i++)
        {
            text[i * 2] = "0123456789abcdef"[data[i] >> 4];
            text[i * 2 + 1] = "0123456789abcdef"[data[i] & 15];
        }
        return new string(text);
    }
}

/// <summary>
/// The embedded agent's end of the local pipe: accepts connections of the current user, checks the launch's secret
/// (the first message must be <c>hello</c> with it, else the connection closes), and gives each <c>task/start</c> to
/// the runner, relaying its status and approvals as the wire's messages.
/// </summary>
internal sealed class PipeDelegationServer : IDelegationServer
{
    private const int MaxInstances = 8;

    private readonly int _maxBytes;
    private readonly TimeSpan _handshakeTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _slug;
    private readonly List<NamedPipeServerStream> _active = new();
    private int _connections;
    private int _disposed;
    private IDelegatedTaskRunner? _runner;
    private Task? _accepting;

    public PipeDelegationServer(AgentCardSummary card, int maxMessageBytes = FrameStream.DefaultMaxBytes, TimeSpan? handshakeTimeout = null)
    {
        _maxBytes = maxMessageBytes;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _slug = card.Slug;
        Endpoint = new AgentDelegationEndpoint { Pipe = "fdg-" + DelegationPipes.RandomHex(12), Secret = DelegationPipes.RandomHex(24), Card = card };
    }

    public AgentDelegationEndpoint Endpoint { get; }

    public void Start(IDelegatedTaskRunner runner)
    {
        if (_runner is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }
        _runner = runner;
        // The first pipe instance exists before Start returns, so a caller that finds the registration can connect.
        var first = DelegationPipes.CreateServer(Endpoint.Pipe, MaxInstances);
        _accepting = Task.Run(() => AcceptAsync(first));
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe.Dispose(); // the caller left before we got to it
                pipe = NextPipe();
                continue;
            }
            var connected = pipe;
            pipe = NextPipe();
            _ = Task.Run(() => HandleAsync(connected));
        }
        pipe.Dispose();
    }

    private NamedPipeServerStream NextPipe()
    {
        while (true)
        {
            try
            {
                return DelegationPipes.CreateServer(Endpoint.Pipe, MaxInstances);
            }
            catch (IOException)
            {
                // all instances busy: wait for one to close
                Task.Delay(50).Wait();
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        var connections = Interlocked.Increment(ref _connections);
        lock (_active)
        {
            _active.Add(pipe);
        }
        using var task = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            if (connections > MaxInstances - 2)
            {
                return; // too many at once: closed without a word
            }
            var frames = new FrameStream(pipe, _maxBytes);
            var hello = await ReadWithin(frames).ConfigureAwait(false);
            if (hello is not { Type: FrameTypes.Hello } || !SecretCompare.Equal(hello.Secret, Endpoint.Secret))
            {
                return; // anything but a hello with the secret closes the connection
            }
            await frames.WriteAsync(new Frame { Type = FrameTypes.Ready, Version = 1, Slug = _slug }, task.Token).ConfigureAwait(false);
            var start = await ReadWithin(frames).ConfigureAwait(false);
            if (start is not { Type: FrameTypes.TaskStart, TaskId.Length: > 0, Message: { } message })
            {
                return;
            }
            var request = new TaskRequest(start.TaskId, message.Text, message.References ?? new List<string>(),
                start.From?.Slug ?? "", start.From?.Name ?? "");
            var channel = new Channel(frames, request.TaskId, task);
            var reading = Task.Run(() => channel.ReadCallerAsync());
            TaskOutcome outcome;
            try
            {
                outcome = await _runner!.RunAsync(request, channel, task.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = new TaskOutcome(request.TaskId, TaskState.Canceled, "The task was canceled.");
            }
            catch (Exception e)
            {
                outcome = new TaskOutcome(request.TaskId, TaskState.Failed, "The agent failed: " + e.GetType().Name + ".");
            }
            await channel.FinishAsync(outcome).ConfigureAwait(false);
            try
            {
                // Closing a pipe with unread data discards it: let the caller read the last message first.
                await Task.Run(() => Drain(pipe)).WaitAsyncWithin(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException
                                          or PlatformNotSupportedException or NotSupportedException)
            {
            }
            task.Cancel();
            await reading.ConfigureAwait(false);
        }
        catch (Exception e) when (e is WireException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the caller broke the protocol or went away: the connection ends
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            lock (_active)
            {
                _active.Remove(pipe);
            }
            pipe.Dispose();
        }
    }

    private static void Drain(NamedPipeServerStream pipe)
    {
#if NET8_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
#endif
        pipe.WaitForPipeDrain();
    }

    private async Task<Frame?> ReadWithin(FrameStream frames)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(_handshakeTimeout);
        try
        {
            return await frames.ReadAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stop.Cancel();
        lock (_active)
        {
            foreach (var pipe in _active)
            {
                pipe.Dispose(); // a task in flight ends for its caller as an agent that went away
            }
        }
        try
        {
            _accepting?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }

    /// <summary>One task's conversation with its caller.</summary>
    private sealed class Channel : IDelegatedTaskChannel
    {
        private readonly FrameStream _frames;
        private readonly string _taskId;
        private readonly CancellationTokenSource _task;
        private readonly Dictionary<string, TaskCompletionSource<ApprovalChoice>> _waiting = new();
        private bool _gone;

        public Channel(FrameStream frames, string taskId, CancellationTokenSource task)
        {
            _frames = frames;
            _taskId = taskId;
            _task = task;
        }

        public Task StatusAsync(TaskState state, string? text) =>
            SendAsync(new Frame { Type = FrameTypes.TaskStatus, TaskId = _taskId, State = state.ToWire(), Text = text });

        public async Task<ApprovalChoice> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<ApprovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting)
            {
                if (_gone)
                {
                    return ApprovalChoice.Deny;
                }
                _waiting[ask.ApprovalId] = answer;
            }
            await SendAsync(new Frame
            {
                Type = FrameTypes.TaskApproval, TaskId = _taskId, State = TaskState.InputRequired.ToWire(), ApprovalId = ask.ApprovalId,
                Tool = ask.Tool, ToolClass = ask.ToolClass, Summary = ask.Summary, ArgumentsPreview = ask.ArgumentsPreview,
            }).ConfigureAwait(false);
            using (cancellationToken.Register(() => answer.TrySetResult(ApprovalChoice.Deny)))
            {
                var choice = await answer.Task.ConfigureAwait(false);
                lock (_waiting)
                {
                    _waiting.Remove(ask.ApprovalId);
                }
                return choice;
            }
        }

        /// <summary>The caller's messages after the start: approvals and a cancel. The end of them is the caller going away.</summary>
        public async Task ReadCallerAsync()
        {
            try
            {
                while (await _frames.ReadAsync(_task.Token).ConfigureAwait(false) is { } frame)
                {
                    if (frame.TaskId != _taskId)
                    {
                        continue;
                    }
                    if (frame.Type == FrameTypes.TaskCancel)
                    {
                        _task.Cancel();
                    }
                    else if (frame.Type == FrameTypes.TaskApprove && frame.ApprovalId is { } id)
                    {
                        TaskCompletionSource<ApprovalChoice>? answer;
                        lock (_waiting)
                        {
                            _waiting.TryGetValue(id, out answer);
                        }
                        answer?.TrySetResult(frame.Decision switch
                        {
                            "once" => ApprovalChoice.Once,
                            "chat" => ApprovalChoice.Chat,
                            _ => ApprovalChoice.Deny,
                        });
                    }
                }
            }
            catch (Exception e) when (e is WireException or IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            Abandon(); // EOF, a broken message or the end of the task
        }

        /// <summary>The caller is gone (or the task ended): pending approvals are denied and the task is cancelled.</summary>
        private void Abandon()
        {
            lock (_waiting)
            {
                _gone = true;
                foreach (var answer in _waiting.Values)
                {
                    answer.TrySetResult(ApprovalChoice.Deny);
                }
            }
            try
            {
                _task.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async Task FinishAsync(TaskOutcome outcome)
        {
            if (outcome.State == TaskState.Completed && !string.IsNullOrEmpty(outcome.Text))
            {
                try
                {
                    await SendAsync(new Frame { Type = FrameTypes.TaskArtifact, TaskId = _taskId, Text = outcome.Text }).ConfigureAwait(false);
                }
                catch (WireException)
                {
                    outcome = new TaskOutcome(outcome.TaskId, TaskState.Failed, "The answer is too large to hand over.");
                }
            }
            await SendAsync(new Frame
            {
                Type = FrameTypes.TaskStatus, TaskId = _taskId, State = outcome.State.ToWire(),
                Text = outcome.State == TaskState.Completed ? null : outcome.Text,
            }).ConfigureAwait(false);
        }

        private async Task SendAsync(Frame frame)
        {
            try
            {
                await _frames.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or WireException)
            {
                if (e is WireException && frame.Type == FrameTypes.TaskArtifact)
                {
                    throw; // an answer too large to send is the task's failure, not a lost caller
                }
                Abandon();
            }
        }
    }
}

internal static class TaskExtensions
{
    /// <summary>Waits for the task for at most <paramref name="limit"/>; throws <see cref="TimeoutException"/> after that.</summary>
    public static async Task WaitAsyncWithin(this Task task, TimeSpan limit)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (finished != task)
        {
            throw new TimeoutException();
        }
        await task.ConfigureAwait(false);
    }
}

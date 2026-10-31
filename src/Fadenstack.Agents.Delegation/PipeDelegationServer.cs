using System.IO.Pipes;
using System.Text.Json;
using Fadenstack.Agents.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Agents.Delegation;

/// <summary>The A2A server of one embedded agent: the library's <see cref="A2A.A2AServer"/>, which also serves the agent's card.</summary>
internal sealed class DelegationA2AServer : A2A.A2AServer
{
    private readonly A2A.AgentCard _card;

    public DelegationA2AServer(A2A.IAgentHandler handler, A2A.AgentCard card)
        : base(handler, new A2A.InMemoryTaskStore(), new A2A.ChannelEventNotifier(), NullLogger<A2A.A2AServer>.Instance,
            new A2A.A2AServerOptions { SupportsStreaming = true, SupportsExtendedAgentCard = true })
    {
        _card = card;
    }

    public override Task<A2A.AgentCard> GetExtendedAgentCardAsync(A2A.GetExtendedAgentCardRequest request, A2A.A2ARequestContext? requestContext,
        CancellationToken cancellationToken = default) => Task.FromResult(_card);

    /// <summary>The A2A Agent Card for an agent's registry summary.</summary>
    public static A2A.AgentCard CardFor(AgentCardSummary summary, string pipe) => new()
    {
        Name = summary.Name.Length > 0 ? summary.Name : summary.Slug,
        Description = summary.Description,
        Version = "1.0",
        SupportedInterfaces = new List<A2A.AgentInterface>
        {
            new() { Url = "pipe://" + pipe, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
        },
        Capabilities = new A2A.AgentCapabilities { Streaming = true, PushNotifications = false, ExtendedAgentCard = true },
        Skills = summary.Skills.Select(s => new A2A.AgentSkill
        {
            Id = Slug(s.Name), Name = s.Name, Description = s.Description, Tags = new List<string>(),
        }).ToList(),
        DefaultInputModes = new List<string> { "text/plain" },
        DefaultOutputModes = new List<string> { "text/plain" },
    };

    private static string Slug(string name)
    {
        var id = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return id.Length == 0 ? "skill" : id;
    }
}

/// <summary>
/// The embedded agent's end of the local pipe: accepts connections of the current user, checks the launch's secret (the first
/// message must be <c>hello</c> with it, else the connection closes), and then speaks A2A 1.0 JSON-RPC, one request or response
/// per line, over an <see cref="A2A.A2AServer"/> whose handler runs each task with the host's runner. The library's JSON-RPC
/// processing is in A2A.AspNetCore (net8+ only), so the small dispatcher here follows its method handling.
/// </summary>
internal sealed class PipeDelegationServer : IDelegationServer
{
    private const int MaxInstances = 8;
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo RequestInfo = A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.JsonRpcRequest));
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo ResponseInfo = A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(A2A.JsonRpcResponse));

    private readonly int _maxBytes;
    private readonly TimeSpan _handshakeTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _slug;
    private readonly List<NamedPipeServerStream> _active = new();
    private int _connections;
    private int _disposed;
    private A2ARunnerHandler? _handler;
    private DelegationA2AServer? _a2a;
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
        if (_handler is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }
        _handler = new A2ARunnerHandler(runner);
        _a2a = new DelegationA2AServer(_handler, DelegationA2AServer.CardFor(Endpoint.Card, Endpoint.Pipe));
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
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var tasks = new List<string>(); // the A2A tasks this connection started
        var requests = new List<Task>();
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
            await frames.WriteAsync(new Frame { Type = FrameTypes.Ready, Version = 1, Slug = _slug }, connection.Token).ConfigureAwait(false);
            while (await frames.ReadLineAsync(connection.Token).ConfigureAwait(false) is { } line)
            {
                requests.Add(Task.Run(() => ServeAsync(frames, line, tasks, connection.Token)));
                requests.RemoveAll(r => r.IsCompleted);
            }
        }
        catch (Exception e) when (e is WireException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the caller broke the protocol or went away: the connection ends
        }
        finally
        {
            // A caller that is gone leaves no one to answer an approval: its tasks stop.
            connection.Cancel();
            lock (tasks)
            {
                foreach (var id in tasks)
                {
                    _handler?.Abandon(id);
                }
            }
            try
            {
                await Task.WhenAll(requests).WaitAsyncWithin(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
            Interlocked.Decrement(ref _connections);
            lock (_active)
            {
                _active.Remove(pipe);
            }
            pipe.Dispose();
        }
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

    /// <summary>One JSON-RPC request: answered by one response line, or by one line per event for a streaming method.</summary>
    private async Task ServeAsync(FrameStream frames, string line, List<string> tasks, CancellationToken cancellationToken)
    {
        A2A.JsonRpcRequest? request = null;
        try
        {
            request = (A2A.JsonRpcRequest?)JsonSerializer.Deserialize(line, RequestInfo);
            if (request is null)
            {
                throw new JsonException();
            }
            if (request.Params is not { } parameters)
            {
                await WriteAsync(frames, A2A.JsonRpcResponse.InvalidParamsResponse(request.Id), cancellationToken).ConfigureAwait(false);
                return;
            }
            switch (request.Method)
            {
                case A2A.A2AMethods.SendStreamingMessage:
                    await foreach (var item in _a2a!.SendStreamingMessageAsync(Parameters<A2A.SendMessageRequest>(parameters), null, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        Remember(tasks, item);
                        await WriteAsync(frames, A2A.JsonRpcResponse.CreateJsonRpcResponse(request.Id, item), cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case A2A.A2AMethods.SendMessage:
                    var sent = await _a2a!.SendMessageAsync(Parameters<A2A.SendMessageRequest>(parameters), null, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(frames, A2A.JsonRpcResponse.CreateJsonRpcResponse(request.Id, sent), cancellationToken).ConfigureAwait(false);
                    break;
                case A2A.A2AMethods.GetTask:
                    var task = await _a2a!.GetTaskAsync(Parameters<A2A.GetTaskRequest>(parameters), null, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(frames, A2A.JsonRpcResponse.CreateJsonRpcResponse(request.Id, task), cancellationToken).ConfigureAwait(false);
                    break;
                case A2A.A2AMethods.CancelTask:
                    var cancelled = await _a2a!.CancelTaskAsync(Parameters<A2A.CancelTaskRequest>(parameters), null, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(frames, A2A.JsonRpcResponse.CreateJsonRpcResponse(request.Id, cancelled), cancellationToken).ConfigureAwait(false);
                    break;
                case A2A.A2AMethods.GetExtendedAgentCard:
                    var card = await _a2a!.GetExtendedAgentCardAsync(Parameters<A2A.GetExtendedAgentCardRequest>(parameters), null, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteAsync(frames, A2A.JsonRpcResponse.CreateJsonRpcResponse(request.Id, card), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(frames, A2A.JsonRpcResponse.MethodNotFoundResponse(request.Id), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // the connection ended
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var id = request?.Id ?? new A2A.JsonRpcId((string?)null);
            var response = e switch
            {
                A2A.A2AException a2a => A2A.JsonRpcResponse.CreateJsonRpcErrorResponse(id, a2a),
                JsonException => A2A.JsonRpcResponse.ParseErrorResponse(id),
                WireException => A2A.JsonRpcResponse.InternalErrorResponse(id, "The answer is too large to hand over."),
                _ => A2A.JsonRpcResponse.InternalErrorResponse(id, "An internal error occurred."), // never the exception's own words
            };
            try
            {
                await WriteAsync(frames, response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or WireException)
            {
            }
        }
    }

    private static T Parameters<T>(JsonElement parameters) where T : class
    {
        try
        {
            return parameters.Deserialize(A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))) as T
                ?? throw new A2A.A2AException("Invalid parameters.", A2A.A2AErrorCode.InvalidParams);
        }
        catch (JsonException ex)
        {
            throw new A2A.A2AException($"Invalid parameters: they could not be read as {typeof(T).Name}.", ex, A2A.A2AErrorCode.InvalidParams);
        }
    }

    private static void Remember(List<string> tasks, A2A.StreamResponse item)
    {
        var id = item.Task?.Id ?? item.StatusUpdate?.TaskId ?? item.ArtifactUpdate?.TaskId;
        if (id is null)
        {
            return;
        }
        lock (tasks)
        {
            if (!tasks.Contains(id))
            {
                tasks.Add(id);
            }
        }
    }

    private static Task WriteAsync(FrameStream frames, A2A.JsonRpcResponse response, CancellationToken cancellationToken) =>
        frames.WriteLineAsync(JsonSerializer.Serialize(response, ResponseInfo), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stop.Cancel();
        _handler?.AbandonAll();
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
        try
        {
            _a2a?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}

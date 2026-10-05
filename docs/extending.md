# Extending

This page covers the SDK's extension points: modules, stages and their fixed order, tool sources, `FadenTransport`, and the client modules an agent can require.

## Modules

A module is a package that plugs into the builder. It implements `IFadenModule`:

```csharp
public interface IFadenModule
{
    string? Name { get; }
    void Configure(AgentBuilder builder);
}
```

`builder.Use(module)` calls `Configure`, where the module adds its stages, tool sources or tools. `Faden.Mcp` is such a module: `AddLocalMcp` adds a `LocalMcpModule`, named `mcp.local`.

A module is active only when it is referenced and added to the builder, never just by being installed.

### Required client modules

An agent's profile on the server can list client modules the agent needs (`required_client_modules`). When the agent starts, every listed name must be the `Name` of a module added with `Use`. Otherwise `StartAsync` throws `FadenException`, naming the missing modules, and no session starts.

A module whose `Name` is null can be added, but it cannot satisfy a requirement.

```csharp
using Faden.Agents;

public sealed class AuditModule : IFadenModule
{
    public string? Name => "example.audit";

    public void Configure(AgentBuilder builder) =>
        builder.AddToolStage(StageOrder.Custom, new ToolLogStage());
}
```

```csharp
builder.Use(new AuditModule());
```

## Stages

Stages wrap the agent's work. There are two kinds:

- `AddChatStage(order, wrap)` wraps the `IChatClient` that carries the agent's requests to the gateway. `wrap` gets the inner client and returns the client that wraps it, usually a Microsoft.Extensions.AI `DelegatingChatClient`.
- `AddToolStage(order, stage)` wraps each tool call. An `IToolStage` sees the call before the tool runs and the result after.

### The order is fixed

Stages run in the order of their `order` value, whatever order they are added in. A module picks its slot, not its position in the builder chain. `StageOrder` names the slots:

| Constant | Slot |
|---|---|
| `StageOrder.Context` | Context added to a request. |
| `StageOrder.Pii` | Personal data masked on the device. |
| `StageOrder.Custom` | Your own stages. |

A lower value sits further from the transport. A request passes the stages from the lowest value to the highest and then goes to the gateway: context first, then masking, then custom stages. A tool call passes them in the same order on the way to the tool, and the result comes back through them in reverse. Give each of your stages its own value when their order matters.

Tool stages run after the approval: the agent decides first whether a call may run, and only calls that run reach the stages.

### A chat stage

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

public sealed class TimingStage : DelegatingChatClient
{
    public TimingStage(IChatClient inner) : base(inner)
    {
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
        Trace.WriteLine($"Model call took {watch.Elapsed}.");
    }
}
```

```csharp
builder.AddChatStage(StageOrder.Custom, inner => new TimingStage(inner));
```

The agent streams every request, so a chat stage overrides `GetStreamingResponseAsync`.

### A tool stage

```csharp
using System.Diagnostics;
using Faden.Agents;

public sealed class ToolLogStage : IToolStage
{
    public async Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
    {
        Trace.WriteLine($"{invocation.Tool.Name} ({invocation.Class}) starts.");
        try
        {
            return await next(invocation, cancellationToken);
        }
        finally
        {
            Trace.WriteLine($"{invocation.Tool.Name} ended.");
        }
    }
}
```

A `ToolInvocation` carries the `Tool`, the model's `Call`, the `Arguments` the tool gets (a stage may change them, for example to put masked values back), the tool's `Class`, and the `SessionId` and `TurnId`. Call `next` to go on to the next stage and finally the tool; what you return is the tool's result.

## Tool sources

An `IToolSource` adds tools once the agent's profile is known, for example tools that depend on the agent's policy:

```csharp
public interface IToolSource
{
    Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken);
}
```

Add one with `builder.AddToolSource(source)`. The `AgentContext` gives the source the `Client`, the agent's `Profile` and `ReportAsync`, which reports an activity event to the server (names and counts, never content). A host tool wins over a source's tool of the same name. A source that implements `IDisposable` is disposed with the agent.

An `AgentTool` wraps an `AIFunction` with its class, its `Source` (`host` or `mcp`), the `Server` label of an MCP tool, and whether it reaches outside the organisation.

## FadenTransport

`client.Transport` makes authenticated requests to the server with the client's tokens, TLS settings and typed errors. Use it to add requests the SDK does not cover yet, without copying the plumbing:

```csharp
using Faden.Http;
using Faden.Models;

var transport = client.Transport;
var features = await transport.GetAsync<ServerFeatures>(transport.Gateway("v1/features"));
```

| Member | Meaning |
|---|---|
| `Gateway(relative)`, `Api(relative)` | The URL of a gateway route (`/v1`) or a backend route (`/api`). |
| `GetAsync<T>`, `PostAsync<T>`, `PutAsync<T>`, `PatchAsync<T>`, `DeleteAsync` | JSON in, JSON out, errors as typed exceptions. |
| `SendJsonAsync<T>(method, url, body)` | The same, for any method. |
| `SendAsync(build, completion, cancellationToken)` | Any request. `build` creates the request; it is called again for the one retry after a 401. |
| `ServerUrl`, `ApiUrl`, `Timeout`, `HttpClient` | The client's settings. |
| `CreateHandler(options)` | A handler with the client's TLS settings, for your own requests to the same server. |

Requests carry the bearer token. When the server answers 401, the transport gets a fresh token and retries once. JSON uses the server's snake_case names; `FadenJson.Options` holds the serializer options.

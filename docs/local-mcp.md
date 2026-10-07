# Local MCP servers

This page covers how an agent uses MCP servers on the device with `Faden.Mcp`: adding them, when they are attached or skipped, and why their tools always ask.

## Add a server

Reference `Faden.Mcp` and add the servers to the builder:

```csharp
using Faden.Mcp;

builder.AddLocalMcp(LocalMcpServer.Stdio("eplan-db", "eplan-mcp.exe"));
```

A `LocalMcpServer` is one of three kinds:

| Factory | Server |
|---|---|
| `LocalMcpServer.Stdio(label, command, arguments, environment, workingDirectory)` | A program the agent starts and talks to over standard input and output. |
| `LocalMcpServer.Http(label, url, httpClient)` | An MCP server reached over HTTP. |
| `LocalMcpServer.FromTransport(label, transport)` | Any other transport of the MCP C# SDK, such as a named pipe or an in-process stream. `transport` is a function that returns the `IClientTransport` when the server is attached. |

The label is what the user and the server see the server as. Choose a short, stable name.

## When servers are attached

Servers are attached when the agent starts (`agent.StartAsync`, which the first `StartSessionAsync` or `OpenSessionAsync` calls). Whether they are attached is the agent's policy on the server, not your configuration:

- If the agent allows local MCP servers, they are attached.
- If the user has developer mode on the server, they are attached.
- Otherwise they are skipped and not even started.

A server that cannot be started, or does not list its tools within the connect timeout, is skipped too. The agent runs without it.

To see what happened, keep the module and read it after the start:

```csharp
var mcp = new LocalMcpModule(new[]
{
    LocalMcpServer.Stdio("eplan-db", "eplan-mcp.exe"),
});
builder.AddLocalMcp(mcp);

using var agent = builder.Build();
await agent.StartAsync();

foreach (var server in mcp.Attached)
{
    Console.WriteLine($"{server.Label}: {string.Join(", ", server.Tools)}");
}
foreach (var server in mcp.Skipped)
{
    Console.WriteLine($"{server.Label} was not attached: {server.Reason}");
}
```

`Skipped` entries carry the `Reason` to show the user and, for a server that failed, the `Error`.

`LocalMcpModule` has these settings:

| Property | Meaning |
|---|---|
| `ConnectTimeout` | How long a server gets to start and list its tools (default 30 seconds). |
| `PrefixToolNames` | Names tools `label_tool`, for servers whose tool names clash. Off by default; without it, the first server's tool of a name wins. |
| `Policy` | The `ILocalMcpPolicy` that decides which servers may be attached. The default follows the agent's profile as described above. Replace it only to make the decision stricter: local configuration must never allow what the agent's profile does not. |

A host tool wins over an MCP tool of the same name. Disposing the agent closes the connections to the servers.

## Why their tools always ask

Every tool of a local MCP server has the class `Unknown`. An MCP server describes its own tools, and the agent does not trust that description to decide what may run without the user.

So in the `Ask` and `Auto` modes, every call to an MCP tool goes to your approval handler, unless the user approved that tool for the session. In the `ReadOnly` and `Off` modes, MCP tools are hidden. [Host tools](tools.md#approvals) describes approvals.

## What the server learns

Each attached server is reported to the server with its label, its program or URL, and the names of its tools. A stdio server's arguments are never reported, because they can hold secrets.

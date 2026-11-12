# Local MCP servers

This page covers how an agent uses MCP servers on the device with `Fadenstack.Mcp`: adding them, when they are attached or skipped, and why their tools always ask.

## Add a server

Add the `Fadenstack.Mcp` package and add the servers to the builder:

```csharp
using Fadenstack.Mcp;

builder.AddLocalMcp(LocalMcpServer.Stdio("notes-db", "notes-mcp.exe"));
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
    LocalMcpServer.Stdio("notes-db", "notes-mcp.exe"),
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

## Tools on request

The model is not given every tool of every server. While at least one server is attached, it gets one tool, `find_tool` (`LocalMcpModule.FindToolName`). Its description is one fixed sentence and a list of the attached servers, so it stays the same from turn to turn until a server comes or goes.

The model calls `find_tool` with a few words about what it needs (`query`). The module ranks the tools of the running servers by their names and descriptions and switches the best matches on for the rest of that conversation:

- A search switches on at most 5 tools (`LocalMcpModule.MaxFound`).
- A conversation has at most 10 local tools on at a time (`LocalMcpModule.MaxToolsInScope`). A new pick pushes out the one used least recently.
- A search that matches nothing answers "No tool on this computer matched that search. Try other words."
- A tool the model calls by name without having found it is switched on, and the call is answered "(name) is now switched on. Call it again with its parameters." instead of running.
- A tool that went away is answered "(name) is no longer available." A tool whose definition changed since the model found it is switched off, and the model is told to search again.
- `find_tool` itself has the class `Read`. It only switches tools on for the conversation; the tools it finds ask as described below.

The list of found tools is fenced as untrusted data, like every result of these servers. What is switched on belongs to one conversation: another chat starts without it.

The module does this through the tool scope that [Extending](extending.md#tools-in-scope-per-conversation) describes.

## Servers while the agent runs

The module keeps one live client for each server for the agent's lifetime. You can change the servers while the agent runs:

| Member | Meaning |
|---|---|
| `AddAsync(server, enabled)` | Adds a server and, when the agent runs and the policy allows, starts it. Throws `ArgumentException` for a label already in use. |
| `RemoveAsync(label)` | Stops and removes a server. |
| `SetEnabledAsync(label, enabled)` | Switches a server off or on. |
| `Servers`, `GetStatus(label)`, `StatusChanged` | What is running, what failed and what the policy refused, as `McpServerStatus` values (`State`, `Detail`, `Tools`, `ToolsChanged`, `Reconnects`). |
| `Changed` | Raised when the tools the module offers changed. |

A server that crashes is started again, at most 3 times (`MaxReconnects`); a server that stayed up for five minutes starts counting from zero again. The module is an `IChangingToolSource` ([Extending](extending.md#tool-sources-that-change)): the agent asks it again at the start of the next turn, so a changed list reaches the model then, not in the middle of a turn.

## Why their tools always ask

Every tool of a local MCP server has the class `Unknown`. An MCP server describes its own tools, and the agent does not trust that description to decide what may run without the user.

So in the `Ask` and `Auto` modes, every call to an MCP tool goes to your approval handler, every time: an unclassified tool is never allowed for the chat or always. In the `ReadOnly` and `Off` modes, MCP tools are hidden. [Host tools](tools.md#approvals) describes approvals.

## What the server learns

Each attached server is reported to the server with its label, its program or URL, and the names of its tools. A stdio server's arguments are never reported, because they can hold secrets.

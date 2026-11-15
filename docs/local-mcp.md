# Local MCP servers

This page covers how an agent uses MCP servers with `Fadenstack.Mcp`:
- adding them, and where a server over HTTP may be reached;
- when they are attached or skipped;
- the class of their tools;
- the person's own list and the Tools view.

The TypeScript SDK has the same module for HTTP servers, `@fadenstack/mcp` ([TypeScript: Local MCP servers](../typescript/local-mcp.md)).

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
| `LocalMcpServer.Http(label, url, httpClient, headers)` | An MCP server reached over HTTP: https anywhere; plain http as described below. The header values are never logged or reported. |
| `LocalMcpServer.FromTransport(label, transport)` | Any other transport of the MCP C# SDK, such as a named pipe or an in-process stream. `transport` is a function that returns the `IClientTransport` when the server is attached. |

The label is what the user and the server see the server as. Choose a short, stable name.

## Where a server over HTTP may be reached

- **https** goes anywhere.
- **Plain http to this computer** (`localhost`, 127.0.0.0/8, `::1`) is fine, headers included.
- **Plain http to the local network** (RFC 1918, link-local, fc00::/7) goes only when all of these hold:
  - the server's switch is on (`LocalMcpServer.AllowPlainHttpInLan`, or `McpServerEntry.PlainHttpInLan` in the person's list);
  - the agent's profile says `local_mcp.plain_http: allow` (absent or anything else is deny, and developer mode does not override it);
  - this computer's policy does not say `plainHttp: forbidden`;
  - the server is sent no header value.
- **Anything else** is refused before anything is sent.

**How a connection is held to the rule** (OWASP ASVS 5.0 V12):
- a name is resolved once, and every address it stands for must pass;
- each request goes to one of those checked addresses, with the name in its Host header, so a name that resolves elsewhere later (DNS rebinding) reaches nothing new;
- plain http goes direct, never through a proxy;
- no redirect is followed, and a request to another origin than the server's is refused, so headers never travel on.

This works the same on .NET Framework. With an app's own `HttpClient`, which the SDK cannot hold to a checked address, plain http goes only to this computer.

**A refusal** sets the server's status to `NotAllowed`, with `McpServerStatus.PlainHttp` set to one of these:
- `NoOptIn`: the switch is off;
- `NotLocal`: an address outside the local network, or one that does not resolve;
- `Secret`: a header value;
- `NotAllowed`: the profile or the policy, shown as "Plain http is not allowed here".

It is not retried. A server that reached the local network stops at the next turn once that is no longer allowed.

**The person's list** keeps a plain-http server on the local network with the switch off, waiting for it. `McpServerValidator` refuses one with a header value (`PlainHttpWithHeaders`) or one outside the local network (`PlainHttpNotLocal`).

### The switch

`McpHost.SetPlainHttpInLanAsync(label, on, warningVersion)` is the person's switch "Allow plain http on the local network". Turning it on needs the version of the warning the person confirmed just before. The app shows the warning; the Tools view does it for you. Turning it off needs no warning.

Every change goes into the device's audit log (`McpHost.AuditLog`, `audit.jsonl` beside the server list). It rotates at 1 MiB, with three older files kept, and nothing in it leaves the device. Each line holds:
- the event `local_mcp.plain_http`;
- the time;
- the server's origin only (no path, query or token);
- on or off;
- the warning's version;
- the person.

In governed mode the change goes to the server as `local_mcp.plain_http {source, state}`. That happens only where the profile's `features` list `client_events.local_mcp_plain_http` (`LocalMcpModule.PlainHttpEventFeature`), because an older core refuses a batch with a type it does not know. Standalone sends nothing.

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
  It runs without asking, standalone and under a Fadenstack server: from 0.7.1 the gateway takes `find_tool` as read unless an
  admin's rule or the agent's contract says otherwise (an older gateway holds it `unknown`, and it asks there). The name is the
  SDK's (`AgentTool.ToolSearchName`): an app's tool cannot take it, so that word reaches no tool with side effects. The tools it
  finds ask on their own calls, every time.
- A server's tool under one of the SDK's own names (`find_tool`, `load_skill`, `read_skill_resource`, `run_skill_script`,
  `web_search`; see [Tools](tools.md)) is left out, and `find_tool` does not find it (logged by the server's label, event
  3007). Of two servers with a tool of the same name, the first one's is kept, and the other's is logged (event 3008).

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

## The class of a tool

An MCP server describes its own tools, and the agent does not take that description on trust (the hint rule):
- **A tool arrives unclassified (`Unknown`), so it asks.** In the `Ask` and `Auto` modes every call goes to your approval handler, every time; an unclassified tool is never allowed for the chat or always. In `ReadOnly` and `Off`, MCP tools are hidden.
- **A `destructiveHint` from any server makes the tool destructive,** and nothing lowers that.
- **A `readOnlyHint` counts only from a server marked trusted** (`LocalMcpServer.Trusted`). Standalone, the person marks it (`McpHost.SetTrusted`); governed, the administrator's marks count.
- **The person can raise a single tool** to `Read` or `Write` (`LocalMcpServer.ToolClasses`, `McpHost.SetToolClass`), never below a destructive hint.

[Host tools](tools.md#approvals) describes approvals.

## The person's own list

`McpHost` keeps the person's servers per user (`users/<id>/mcp/servers.dat`, encrypted; standalone: `users/standalone/mcp`; see [Data on the device](data-on-device.md)). It gives them to the agent through `NewModule()`. Its other members:
- `AddAsync`, `UpdateAsync`, `RemoveAsync` and `SetEnabledAsync`;
- the trust mark and the person's raises;
- `TestAsync`, which starts a server for a moment under the same rules;
- the switch above.

**Import** (`McpImporter`) reads the MCP servers of Claude Desktop, VS Code and Cursor, and a `.mcp.json` the person picks:
- servers arrive off, not trusted, and with the switch off;
- a server on another computer is left out: those files often carry a token in the URL, and such servers mostly need OAuth, so the person types them in.

**The scan** (`McpScanner`) looks for servers in other apps' settings and on this computer's ports.

The machine policy names a server by its source (the program's path or file name, or the address or origin), never by its label ([Machine policy](machine-policy.md)).

## The Tools view

The contract `ILocalToolsCatalog` is in `Fadenstack.Agents` (namespace `Fadenstack.LocalTools`), so `Fadenstack.UI` does not depend on the module. `McpToolsCatalog` implements it over `McpHost`. `ToolsViewModel` (`Fadenstack.UI.Tools`) is drawn by `ToolsView` in the WPF and Avalonia skins.

**What it shows:**
- the policy;
- the person's servers as cards, with status and on/off;
- the details: the trust mark, each tool's class with the person's raise, and under *Advanced* the plain-http switch;
- the add and edit sheet, with secret values in password boxes: a saved value is never shown back, only dots and *Replace*;
- import and scan;
- in governed mode, "On your server": the organisation's tools, read only.

**The switch** shows only where it applies and is allowed. Turning it on opens the warning (version `2026-11-15`) and changes nothing until the person confirms.

The texts are in English, German, French, Spanish and Simplified Chinese. The app gives the folder and file dialogs (`IToolsFileHost`) and opens the page (`ToolsViewModel.OpenAsync`).

## What the server learns

Each attached server is reported to the server (`local_mcp.attached`) with its label, its program or URL, and the names of its tools. The switch's changes go as `local_mcp.plain_http`, as above. A stdio server's arguments are never reported, because they can hold secrets, and a header or environment value is never reported or logged.

OWASP ASVS 5.0: V12 (secure communication), V13 (configuration and secrets), V15 (secure coding).

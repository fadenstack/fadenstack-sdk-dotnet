# Fadenstack.Mcp

Local MCP servers as tools of a [Fadenstack](https://fadenstack.com) agent, under the agent's local MCP policy on
the server. The code is in the `Fadenstack.Mcp` namespace and builds on
[Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents).

```bash
dotnet add package Fadenstack.Mcp
```

```csharp
using Fadenstack.Mcp;

builder.AddLocalMcp(LocalMcpServer.Stdio("notes-db", "notes-mcp.exe"));
```

The servers are attached when the agent starts, and only if the agent allows local MCP servers or the user has
developer mode on the server. Otherwise they are not even started. Their tools ask the user before every call.

The module keeps one live client per server. Servers can be added, removed and switched on or off while the agent runs
(`LocalMcpModule.AddAsync`, `RemoveAsync`, `SetEnabledAsync`), a crashed server is started again (three times at most), and
`Servers` and `StatusChanged` tell a Tools page what is running, what failed and what the policy refused.

- **Tools on request.** The model is not given every tool of every server. It gets one tool, `find_tool`, whose
  description lists the servers; a search switches the best matches on for that conversation (ten at most), and a tool
  called by name is switched on as well. Every call still asks.
- **Roots, questions and progress.** Set `Roots` (and call `NotifyRootsChanged`) to tell servers the folders they may work
  in. Set `Ask` to show a server's question (form mode) to the person. `Progress` reports a running call. URL-mode
  questions are declined, and sampling is never declared: a server cannot use the model.
- **Results are data.** They are fenced with a random tag, cleaned of hidden characters and cut at about 16,000 characters.
  A call is cancelled after `LocalMcpServer.CallTimeout` (2 minutes, 10 at most) or when the turn is stopped.
- `LocalMcpServer.IsLoopback` tells a host whether an address is on this computer.

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`, and needs a Fadenstack server 0.4.2 or
later.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/local-mcp/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

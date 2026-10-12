# Fadenstack.Mcp

Local MCP servers as tools of a [Fadenstack](https://fadenstack.com) agent, under the agent's local MCP policy on
the server. The code is in the `Faden.Mcp` namespace and builds on
[Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents).

```bash
dotnet add package Fadenstack.Mcp
```

```csharp
using Faden.Mcp;

builder.AddLocalMcp(LocalMcpServer.Stdio("eplan-db", "eplan-mcp.exe"));
```

The servers are attached when the agent starts, and only if the agent allows local MCP servers or the user has
developer mode on the server. Otherwise they are not even started. Their tools ask the user before every call.

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`, and needs a Fadenstack server 0.4.2 or
later.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/local-mcp/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

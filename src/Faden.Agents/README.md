# Fadenstack.Agents

Agents that run inside other software (EPLAN, TIA Portal, Office, any desktop application) and are governed by a
[Fadenstack](https://fadenstack.com) server. This package runs the agent loop on the device: the host's tools,
approvals, tool modes and the conversation. The server owns the agent's instructions, model and knowledge, and
handles personal data and audit. The code is in the `Faden.Agents` namespace.

```bash
dotnet add package Fadenstack.Agents
```

```csharp
using System.ComponentModel;
using Faden;
using Faden.Agents;
using Faden.Tools;

using var client = FadenClient.ForAgent(new Uri("https://ai.example.internal"), "my-assistant", out var tokens);
if (!await tokens.IsSignedInAsync())
{
    // First run only: the sign-in is kept for this user on this device (DPAPI on Windows).
    await tokens.SignInAsync(loginToken: args[0], deviceLabel: "Console on " + Environment.MachineName);
}

using var agent = FadenAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("my-assistant")
    .AddTools(new PageTools())
    .Build();

var session = await agent.StartSessionAsync();   // starts in Ask
var result = await session.SendAsync("What is on page 7?");
Console.WriteLine(result.Text);

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open document.")]
    public string ReadPage(int page) => $"Page {page}: contactor K1, terminal strip X1.";
}
```

A tool that changes something asks the user first; without an approval handler the call is declined. The agent
`my-assistant` is registered on the server first.

Related packages: [Fadenstack.Mcp](https://www.nuget.org/packages/Fadenstack.Mcp) for local MCP servers,
[Fadenstack.UI](https://www.nuget.org/packages/Fadenstack.UI) and
[Fadenstack.UI.Wpf](https://www.nuget.org/packages/Fadenstack.UI.Wpf) for a chat panel.

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`, and needs a Fadenstack server 0.4.2 or
later.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

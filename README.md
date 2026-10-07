# Fadenstack .NET SDK

Agents that run inside other software (EPLAN, TIA Portal, Office, any desktop application) and are governed by a
[Fadenstack](https://fadenstack.com) server. The SDK runs the agent loop on the device: host tools, approvals, local
MCP servers and the conversation itself. The server owns the agent's instructions, model and knowledge, and handles
personal data and audit (budgets are planned). Agents need a Fadenstack server from the next release, after 0.4.1.

**Documentation: [developers.fadenstack.com/sdk/dotnet](https://developers.fadenstack.com/sdk/dotnet/)**
(the source is in [`docs/`](docs/)).

| Package | What it is |
|---|---|
| `Faden.Client` | The server connection: the gateway as a Microsoft.Extensions.AI `IChatClient`, agent sign-in, the agent's profile, the REST resources, typed errors |
| `Faden.Agents` | The agent: the builder, the tool loop, tool classes and modes, approvals, sessions on the device, outcomes, modules and stages |
| `Faden.Mcp` | Local MCP servers as agent tools, under the agent's local MCP policy |
| `Faden.UI` | Platform-neutral view models for a chat with an agent |
| `Faden.UI.Wpf` | The WPF skin for `Faden.UI`: a chat panel for WPF applications and add-ins |

`Faden.Client`, `Faden.Agents`, `Faden.Mcp` and `Faden.UI` target `netstandard2.0` (.NET Framework 4.7.2 and later)
and `net8.0`. `Faden.UI.Wpf` targets `net472` and `net8.0-windows`.

## An agent in a host application

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
Console.WriteLine($"Outcome: {result.Outcome}");

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open document.")]
    public string ReadPage(int page) => $"Page {page}: contactor K1, terminal strip X1.";
}
```

A read tool runs without asking. A tool that changes something asks the user first, and without an approval
handler the call is declined. The docs cover [host tools and approvals](docs/tools.md),
[outcomes and errors](docs/outcomes.md), [sessions on the device](docs/sessions.md),
[local MCP servers](docs/local-mcp.md), [the chat panel](docs/chat-panel.md) and [the samples](docs/samples.md).

## Install

The packages are published to NuGet with the first release. Until then, reference the projects from source;
[Install](docs/index.md#install) shows how.

## Build and test

```bash
dotnet test Faden.slnx
```

Build with the .NET 10 SDK. The WPF projects build on any OS (`EnableWindowsTargeting`) and run on Windows only.

To preview the documentation on its own: `pip install zensical`, then `zensical serve` in this folder.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

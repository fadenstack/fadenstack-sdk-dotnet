# Fadenstack .NET SDK

This page introduces the .NET SDK, its packages and target frameworks, and shows how to add it to a project.

!!! note "Server version"
    Agents need a Fadenstack server from the next release, after 0.4.1. With 0.4.1, only
    [the plain client](plain-api.md) works, without tool modes, governance notices and the gateway's events.

## What the SDK is

The SDK builds agents that run inside other software and are governed by a Fadenstack server. The host can be a desktop application such as EPLAN, TIA Portal or Microsoft Office, a Windows service or a console tool.

The work is split between the device and the server. The device runs the agent loop: it sends the conversation, runs the host application's tools, asks the user before a tool changes something, attaches local MCP servers and keeps the conversation. The server owns the agent's instructions, its model and its knowledge, and it handles personal data (PII) and the audit trail. Budgets are <span class="fs-planned">Planned</span>.

The SDK never receives the agent's instructions. Every model call goes through the server's gateway, which applies the organisation's policies on the way to the model and back. [Agents on the server](../../concepts/agents.md) explains how an agent is defined there.

## Packages

| Package | What it contains |
|---|---|
| `Faden.Client` | The server connection: the gateway as a Microsoft.Extensions.AI `IChatClient`, agent sign-in, the agent's profile, the REST resources and typed errors. |
| `Faden.Agents` | The agent: the builder, the tool loop, tool classes and modes, approvals, sessions on the device, outcomes, modules and stages. |
| `Faden.Mcp` | Local MCP servers as agent tools, under the agent's local MCP policy. |
| `Faden.UI` | Platform-neutral view models for a chat with an agent, built on CommunityToolkit.Mvvm. |
| `Faden.UI.Wpf` | The WPF skin for `Faden.UI`: a chat panel for WPF applications and add-ins. |

Each package brings the packages it builds on. `Faden.Agents` includes `Faden.Client`; `Faden.Mcp` and `Faden.UI` include `Faden.Agents`; `Faden.UI.Wpf` includes `Faden.UI`.

## Target frameworks

| Package | Targets | Runs on |
|---|---|---|
| `Faden.Client`, `Faden.Agents`, `Faden.Mcp`, `Faden.UI` | `netstandard2.0`, `net8.0` | .NET Framework 4.7.2 and later, .NET 8 and later |
| `Faden.UI.Wpf` | `net472`, `net8.0-windows` | .NET Framework 4.7.2 and later, .NET 8 and later, on Windows |

The `netstandard2.0` builds serve .NET Framework hosts, such as add-ins loaded by a native application. A .NET 8 or later application gets the `net8.0` builds. If you write a library on top of the SDK, read [Building a library](libraries.md) first.

## Install

The packages are published to NuGet with the first release. Until then, reference the projects from source.

1. Clone the repository next to your solution:

    ```bash
    git clone https://github.com/fadenstack/fadenstack-sdk-dotnet.git
    ```

2. Reference the project of each package you need from your project file:

    ```xml
    <ItemGroup>
      <ProjectReference Include="..\fadenstack-sdk-dotnet\src\Faden.Agents\Faden.Agents.csproj" />
    </ItemGroup>
    ```

    For local MCP servers, add `src\Faden.Mcp\Faden.Mcp.csproj`. For the WPF chat panel, add `src\Faden.UI.Wpf\Faden.UI.Wpf.csproj`.

Build the SDK's projects with the .NET 10 SDK. Your own project can target any of the frameworks in the table above.

## A complete example

A console program that signs in to an agent, offers one host tool and asks one question:

```csharp
using System.ComponentModel;
using Faden;
using Faden.Agents;
using Faden.Tools;

using var client = FadenClient.ForAgent(new Uri("https://ai.example.internal"), "my-assistant", out var tokens);
if (!await tokens.IsSignedInAsync())
{
    // First run only: the sign-in is kept for this user on this device.
    await tokens.SignInAsync(loginToken: args[0], deviceLabel: "Console on " + Environment.MachineName);
}

using var agent = FadenAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("my-assistant")
    .AddTools(new PageTools())
    .Build();

var session = await agent.StartSessionAsync();
var result = await session.SendAsync("What is on page 7?");
Console.WriteLine(result.Text);
Console.WriteLine($"Outcome: {result.Outcome}");

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open document.")]
    public string ReadPage(int page) => $"Page {page}: contactor K1, terminal strip X1.";
}
```

`read_page` is a read tool, so it runs without asking in the mode every session starts in. A tool that changes something asks the user first, and without an approval handler such a call is declined. [Host tools](tools.md) explains both.

## Namespaces

| Namespace | Main types |
|---|---|
| `Faden` | `FadenClient`, `FadenClientOptions`, the exceptions |
| `Faden.Auth` | `AgentTokenProvider`, `StaticTokenProvider`, `ITokenProvider`, `ITokenStore`, `FileTokenStore`, `MemoryTokenStore` |
| `Faden.Chat` | `FadenChatOptions`, `FadenEventContent`, `GovernanceNoticeContent` |
| `Faden.Governance` | `ChatOutcome`, `GovernanceNotice` |
| `Faden.Tools` | `ToolClass`, `ToolMode`, `ToolModes` |
| `Faden.Models`, `Faden.Resources` | The profile, manifest and server records; the REST resources |
| `Faden.Http` | `FadenTransport`, `FadenJson` |
| `Faden.Agents` | `FadenAgent`, `AgentBuilder`, `AgentSession`, the events, `AgentResult`, `AgentToolAttribute`, approvals, stages, `IFadenModule` |
| `Faden.Agents.Sessions` | `ISessionStore`, `FileSessionStore`, `MemorySessionStore`, `SessionRetention`, `TranscriptEntry` |
| `Faden.Mcp` | `LocalMcpServer`, `LocalMcpModule`, `AddLocalMcp` |
| `Faden.UI.ViewModels`, `Faden.UI.Parts`, `Faden.UI.Services` | The chat view models, the answer parts, `ApprovalBroker`, `IUiDispatcher` |
| `Faden.UI.Wpf.Views`, `Faden.UI.Wpf.Services` | `ChatView`, `ChatTheme`, `WpfDispatcher`, `WpfClipboard` |
| `Microsoft.Extensions.DependencyInjection` | `AddFadenAgent` |

## Where next

- [Build an agent](agent.md): the client, sign-in, the builder, sessions and streaming.
- [Host tools](tools.md): tools, tool modes and approvals.
- [Outcomes and errors](outcomes.md): how a turn ends, and what is thrown.
- [Sessions on the device](sessions.md): storage, retention and your own store.
- [Chat panel](chat-panel.md): a ready chat UI for WPF hosts.
- [Your first agent](../../get-started/first-agent.md): registering an agent on the server and signing in.
- [TypeScript SDK](../typescript/index.md): the same agents, built in TypeScript.

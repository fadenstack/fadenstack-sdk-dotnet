# Fadenstack .NET SDK

This page introduces the .NET SDK, its packages and target frameworks, and shows how to add it to a project.

!!! note "Server version"
    Agents need a Fadenstack server 0.4.2 or later. With 0.4.1 and older, only
    [the plain client](plain-api.md) works, without tool modes, governance notices and the gateway's events.

## What the SDK is

The SDK builds agents that run inside other software and are governed by a Fadenstack server. The host can be a desktop application, an add-in for Microsoft Office or another application, a Windows service or a console tool.

The work is split between the device and the server. The device runs the agent loop: it sends the conversation, runs the host application's tools, asks the user before a tool changes something, attaches local MCP servers and keeps the conversation. The server owns the agent's instructions, its model and its knowledge, and it handles personal data (PII) and the audit trail.

The SDK never receives the agent's instructions. Every model call goes through the server's gateway, which applies the organisation's policies on the way to the model and back. [Agents on the server](../../concepts/agents.md) explains how an agent is defined there.

## Packages

| Package | What it contains |
|---|---|
| `Fadenstack.Client` | The server connection: the gateway as a Microsoft.Extensions.AI `IChatClient`, agent sign-in, the agent's profile, the REST resources and typed errors. |
| `Fadenstack.Agents` | The agent: the builder, the tool loop, tool classes and modes, approvals, sessions on the device, outcomes, modules and stages. |
| `Fadenstack.Mcp` | Local MCP servers as agent tools, under the agent's local MCP policy. |
| `Fadenstack.UI` | Platform-neutral view models for a chat with an agent, built on CommunityToolkit.Mvvm. |
| `Fadenstack.UI.Wpf` | The WPF skin for `Fadenstack.UI`: a chat panel for WPF applications and add-ins. |

Each package's namespace is its name; `Fadenstack.Client` holds `Fadenstack` and the namespaces under it (see [Namespaces](#namespaces)). Each package brings the packages it builds on. `Fadenstack.Agents` includes `Fadenstack.Client`; `Fadenstack.Mcp` and `Fadenstack.UI` include `Fadenstack.Agents`; `Fadenstack.UI.Wpf` includes `Fadenstack.UI`.

## Target frameworks

| Package | Targets | Runs on |
|---|---|---|
| `Fadenstack.Client`, `Fadenstack.Agents`, `Fadenstack.Mcp`, `Fadenstack.UI` | `netstandard2.0`, `net8.0` | .NET Framework 4.7.2 and later, .NET 8 and later |
| `Fadenstack.UI.Wpf` | `net472`, `net8.0-windows` | .NET Framework 4.7.2 and later, .NET 8 and later, on Windows |

The `netstandard2.0` builds serve .NET Framework hosts, such as add-ins loaded by a native application. A .NET 8 or later application gets the `net8.0` builds. If you write a library on top of the SDK, read [Building a library](libraries.md) first.

## Install

Add the package you need from nuget.org. It brings the packages it builds on:

```bash
dotnet add package Fadenstack.Agents
```

For local MCP servers, add `Fadenstack.Mcp`. For the WPF chat panel, add `Fadenstack.UI.Wpf`. Your project can target any of the frameworks in the table above.

### From source

To try changes that are not released yet, reference the projects instead of the packages:

1. Clone the repository next to your solution:

    ```bash
    git clone https://github.com/fadenstack/fadenstack-sdk-dotnet.git
    ```

2. Reference the project of each package you need from your project file:

    ```xml
    <ItemGroup>
      <ProjectReference Include="..\fadenstack-sdk-dotnet\src\Fadenstack.Agents\Fadenstack.Agents.csproj" />
    </ItemGroup>
    ```

    For local MCP servers, add `src\Fadenstack.Mcp\Fadenstack.Mcp.csproj`. For the WPF chat panel, add `src\Fadenstack.UI.Wpf\Fadenstack.UI.Wpf.csproj`.

Build the SDK's projects with the .NET 10 SDK.

## A complete example

A console program that signs in to an agent, offers one host tool and asks one question:

```csharp
using System.ComponentModel;
using Fadenstack;
using Fadenstack.Agents;
using Fadenstack.Tools;

using var client = FadenstackClient.ForAgent(new Uri("https://ai.example.internal"), "my-assistant", out var tokens);
if (!await tokens.IsSignedInAsync())
{
    // First run only: the sign-in is kept for this user on this device.
    await tokens.SignInAsync(loginToken: args[0], deviceLabel: "Console on " + Environment.MachineName);
}

using var agent = FadenstackAgent.CreateBuilder()
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
    public string ReadPage(int page) => $"Page {page}: section 1.1, table T1.";
}
```

`read_page` is a read tool, so it runs without asking in the mode every session starts in, once the agent's tool contract on the server lists it as `read`. Until an administrator has taken it in, it is unclassified and asks every time. A tool that changes something asks the user first, and without an approval handler such a call is declined. [Host tools](tools.md) explains both.

## Namespaces

| Namespace | Main types |
|---|---|
| `Fadenstack` | `FadenstackClient`, `FadenstackClientOptions`, the exceptions |
| `Fadenstack.Auth` | `AgentTokenProvider`, `StaticTokenProvider`, `ITokenProvider`, `ITokenStore`, `FileTokenStore`, `MemoryTokenStore` |
| `Fadenstack.Chat` | `FadenstackChatOptions`, `FadenstackEventContent`, `GovernanceNoticeContent` |
| `Fadenstack.Governance` | `ChatOutcome`, `GovernanceNotice` |
| `Fadenstack.Tools` | `ToolClass`, `ToolMode`, `ToolModes` |
| `Fadenstack.Models`, `Fadenstack.Resources` | The profile, manifest and server records; the REST resources |
| `Fadenstack.Http` | `FadenstackTransport`, `FadenstackJson` |
| `Fadenstack.Agents` | `FadenstackAgent`, `AgentBuilder`, `AgentSession`, the events, `AgentResult`, `AgentToolAttribute`, approvals, stages, `IFadenstackModule` |
| `Fadenstack.Agents.Sessions` | `ISessionStore`, `FileSessionStore`, `MemorySessionStore`, `SessionRetention`, `TranscriptEntry` |
| `Fadenstack.Mcp` | `LocalMcpServer`, `LocalMcpModule`, `AddLocalMcp` |
| `Fadenstack.UI.ViewModels`, `Fadenstack.UI.Parts`, `Fadenstack.UI.Services` | The chat view models, the answer parts, `ApprovalBroker`, `IUiDispatcher` |
| `Fadenstack.UI.Wpf.Views`, `Fadenstack.UI.Wpf.Services` | `ChatView`, `ChatTheme`, `WpfDispatcher`, `WpfClipboard` |
| `Microsoft.Extensions.DependencyInjection` | `AddFadenstackAgent` |

## Where next

- [Build an agent](agent.md): the client, sign-in, the builder, sessions and streaming.
- [Host tools](tools.md): tools, tool modes and approvals.
- [Outcomes and errors](outcomes.md): how a turn ends, and what is thrown.
- [Sessions on the device](sessions.md): storage, retention and your own store.
- [Chat panel](chat-panel.md): a ready chat UI for WPF hosts.
- [Your first agent](../../get-started/first-agent.md): registering an agent on the server and signing in.
- [TypeScript SDK](../typescript/index.md): the same agents, built in TypeScript.

# Fadenstack .NET SDK

This page introduces the .NET SDK, its packages and target frameworks, and shows how to add it to a project.

!!! note "Server version"
    SDK 0.3.0 is built and tested against Fadenstack server 0.7.1. Agents need a server 0.4.2 or later; where a
    server lacks a feature, the SDK says so and goes on without it: pairing throws
    `FadenstackPairingUnsupportedException`, `ListToolsAsync` returns `null` and `agent.Grants.Available` is false. With
    0.4.1 and older, only [the plain client](plain-api.md) works, without tool modes, governance notices and the
    gateway's events.

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
| `Fadenstack.Secrets` | Encryption at rest on macOS and Linux: the data key lives in the Keychain or the Secret Service. One call at start-up, `FadenstackSecrets.UseOsKeyStore()`, before any store is created; Windows keeps DPAPI. Without a key store the agent keeps session transcripts in memory only; approvals and tokens stay in owner-only files. |
| `Fadenstack.Provenance` | Content provenance: what tools place where people read it is marked as AI-generated where the host can carry a mark, signed by the server and recorded ([Content provenance](provenance.md)). |
| `Fadenstack.UI` | Platform-neutral view models for a chat with an agent, built on CommunityToolkit.Mvvm, with its texts in English, German, French, Spanish and Simplified Chinese ([Languages](chat-panel.md#languages)). |
| `Fadenstack.UI.Wpf` | The WPF skin for `Fadenstack.UI`: a chat panel for WPF applications and add-ins. |
| `Fadenstack.UI.Avalonia` | The Avalonia 12 skin for `Fadenstack.UI`: the same chat panel for cross-platform desktop applications ([The Avalonia skin](chat-panel.md#the-avalonia-skin)). |

Each package's namespace is its name; `Fadenstack.Client` holds `Fadenstack` and the namespaces under it (see [Namespaces](#namespaces)). Each package brings the packages it builds on. `Fadenstack.Agents` includes `Fadenstack.Client`; `Fadenstack.Mcp` and `Fadenstack.UI` include `Fadenstack.Agents`; `Fadenstack.Provenance` includes `Fadenstack.UI` (for the words its marks show people); `Fadenstack.UI.Wpf` and `Fadenstack.UI.Avalonia` include `Fadenstack.UI`; `Fadenstack.Secrets` includes `Fadenstack.Client`.

## Target frameworks

| Package | Targets | Runs on |
|---|---|---|
| `Fadenstack.Client`, `Fadenstack.Agents`, `Fadenstack.Mcp`, `Fadenstack.Provenance`, `Fadenstack.UI` | `netstandard2.0`, `net8.0` | .NET Framework 4.7.2 and later, .NET 8 and later |
| `Fadenstack.Secrets` | `net8.0` | .NET 8 and later, on macOS and Linux (Windows hosts use DPAPI without it) |
| `Fadenstack.UI.Wpf` | `net472`, `net8.0-windows` | .NET Framework 4.7.2 and later, .NET 8 and later, on Windows |
| `Fadenstack.UI.Avalonia` | `net8.0` | .NET 8 and later, wherever Avalonia 12 runs |

The `netstandard2.0` builds serve .NET Framework hosts, such as add-ins loaded by a native application. A .NET 8 or later application gets the `net8.0` builds. If you write a library on top of the SDK, read [Building a library](libraries.md) first.

## Install

Add the package you need from nuget.org. It brings the packages it builds on:

```bash
dotnet add package Fadenstack.Agents
```

For local MCP servers, add `Fadenstack.Mcp`. For the WPF chat panel, add `Fadenstack.UI.Wpf`; for the Avalonia one, `Fadenstack.UI.Avalonia`. On macOS and Linux, add `Fadenstack.Secrets` to encrypt what the SDK keeps ([Sessions on the device](sessions.md#where-they-are-stored)). Your project can target any of the frameworks in the table above.

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

    For local MCP servers, add `src\Fadenstack.Mcp\Fadenstack.Mcp.csproj`. For the WPF chat panel, add `src\Fadenstack.UI.Wpf\Fadenstack.UI.Wpf.csproj`; for the Avalonia one, `src\Fadenstack.UI.Avalonia\Fadenstack.UI.Avalonia.csproj`.

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
| `Fadenstack.Auth` | `AgentTokenProvider`, `AgentPairing`, `StaticTokenProvider`, `ITokenProvider`, `ITokenStore`, `FileTokenStore`, `MemoryTokenStore`, `ISecretProtector` |
| `Fadenstack.Storage`, `Fadenstack.Logging` | `FadenstackPaths` (where the SDK keeps things on a device); `FadenstackFileLoggerProvider`, `FadenstackFileLoggerOptions` ([Logging](agent.md#logging)) |
| `Fadenstack.Chat` | `FadenstackChatOptions`, `FadenstackEventContent`, `GovernanceNoticeContent` |
| `Fadenstack.Governance` | `ChatOutcome`, `GovernanceNotice` |
| `Fadenstack.Tools` | `ToolClass`, `ToolMode`, `ToolModes`, `EditKind` |
| `Fadenstack.Models`, `Fadenstack.Resources` | The profile, manifest and server records; the REST resources |
| `Fadenstack.Http` | `FadenstackTransport`, `FadenstackJson` |
| `Fadenstack.Agents` | `FadenstackAgent`, `AgentBuilder`, `AgentSession`, the events, `AgentResult`, `AgentToolAttribute`, approvals, stages, `IFadenstackModule` |
| `Fadenstack.Agents.Sessions` | `ISessionStore`, `FileSessionStore`, `MemorySessionStore`, `SessionRetention`, `TranscriptEntry` |
| `Fadenstack.Agents.Registry` | `DeviceAgentRegistry`, `AgentRegistration`, `AgentPresence`, `AgentStatus`, `IProcessSource`, `SystemProcessSource` |
| `Fadenstack.Secrets` | `FadenstackSecrets` |
| `Fadenstack.Mcp` | `LocalMcpServer`, `LocalMcpModule`, `AddLocalMcp` |
| `Fadenstack.Provenance` | `ProvenanceModule`, `IProvenance`, `MarkableContent`, `SinkInfo`, `IContentMarker`, the built-in markers, `ProvenanceReader` |
| `Fadenstack.UI.ViewModels`, `Fadenstack.UI.Parts`, `Fadenstack.UI.Services` | The chat view models, the answer parts, `ApprovalBroker`, `IUiDispatcher` |
| `Fadenstack.UI.Wpf.Views`, `Fadenstack.UI.Wpf.Services` | `ChatView`, `ChatTheme`, `WpfDispatcher`, `WpfClipboard` |
| `Fadenstack.UI.Avalonia.Views`, `.Services`, `.Themes` | `ChatView`, `AvaloniaUiDispatcher`, `AvaloniaClipboard`, `FadenstackTheme`, `FadenstackControls` |
| `Microsoft.Extensions.DependencyInjection` | `AddFadenstackAgent` |

## What's new in 0.3.0

- **Signing in:** [sign in by pairing](agent.md#sign-in-by-pairing), for one agent or several with one approval, and what to do when the server is too old.
- **Approvals:** [server-held grants and Bypass](server-grants.md), the second sign-in, `GrantLifecycle`; [host commands](host-commands.md) and the server's recheck of host calls.
- **Thinking:** [how long the agent thinks](agent.md#how-long-it-thinks-the-persons-choice), the pill in the chat and the server's cap.
- **Data on the device:** [per-user stores, encryption on every system, retention from the server](sessions.md), and [export and deletion of a person's data](sessions.md#export-and-delete-my-data).
- **Tools:** [the organisation's tools list](agent.md#the-organisations-tools), [tool sources that change](extending.md#tool-sources-that-change) and [`find_tool` for local MCP servers](local-mcp.md#tools-on-request).
- **Logging:** [content-free logs, traces and metrics, and a file sink](agent.md#logging).
- **Chat panel:** [the Avalonia skin](chat-panel.md#the-avalonia-skin), nested Markdown with a preview and Mermaid blocks, card details, [a gate on running turns](chat-panel.md#a-gate-on-running-turns), and the [notice for answers without an AI mark](chat-panel.md#answers-without-an-ai-mark).
- **Around the SDK:** [Testing](testing.md) and the [parity table](parity.md) with the TypeScript SDK.

### Breaking change: the device agent registry moved

`DeviceAgentRegistry` and the types around it moved from the `Fadenstack.Workspaces` namespace to `Fadenstack.Agents.Registry`, in the `Fadenstack.Agents` package. There are no forwarding types: code that used them does not compile until its `using` lines change.

```csharp
// before
using Fadenstack.Workspaces;

// after
using Fadenstack.Agents.Registry;
```

The moved types are `DeviceAgentRegistry`, `AgentRegistration`, `AgentPresence` (with `AgentStatus`, `AgentDelegationEndpoint`, `AgentCardSummary` and `AgentCardSkill`), `ProcessSnapshot`, `IProcessSource`, `ProcessSourceExtensions` and `SystemProcessSource`. Keep `using Fadenstack.Workspaces;` only where you use what stayed there, such as `WorkspaceDirectory` or `DocumentLeases`. A project that referenced `Fadenstack.Workspaces` only for the registry can reference `Fadenstack.Agents` instead; `Fadenstack.Workspaces` is a prototype that is not published as a package.

## Where next

- [Build an agent](agent.md): the client, sign-in, the builder, sessions and streaming.
- [Host tools](tools.md): tools, tool modes and approvals.
- [Host commands and policy](host-commands.md): host tools the person starts, and the server's fresh check of every host call.
- [Outcomes and errors](outcomes.md): how a turn ends, and what is thrown.
- [Sessions on the device](sessions.md): storage, retention and your own store.
- [Content provenance](provenance.md): marking what tools place as AI-generated.
- [Chat panel](chat-panel.md): a ready chat UI for WPF and Avalonia hosts.
- [Server-held grants and Bypass](server-grants.md): what the server holds so the agent need not ask again.
- [Testing](testing.md): how to test an app against the SDK.
- [Parity with the TypeScript SDK](parity.md): each feature, and its name in both SDKs.
- [Your first agent](../../get-started/first-agent.md): registering an agent on the server and signing in.
- [TypeScript SDK](../typescript/index.md): the same agents, built in TypeScript.

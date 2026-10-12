# Fadenstack.UI

Platform-neutral view models for a chat with a [Fadenstack](https://fadenstack.com) agent: the conversation,
streamed Markdown, tool calls, approval cards, notices and the session history. They have no platform references
and run headless in unit tests. A skin only renders them:
[Fadenstack.UI.Wpf](https://www.nuget.org/packages/Fadenstack.UI.Wpf) is the WPF one. The code is in the
`Faden.UI` namespace and builds on [Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents).

```bash
dotnet add package Fadenstack.UI
```

```csharp
using Faden.Agents;
using Faden.UI.Services;
using Faden.UI.ViewModels;

var approvals = new ApprovalBroker();          // approval cards in the chat decide
var agent = FadenAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("my-assistant")
    .UseApprovals(approvals)
    .Build();

var chat = new AgentChatViewModel(agent, approvals, dispatcher);   // your skin's IUiDispatcher
await chat.InitializeAsync();
```

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/chat-panel/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

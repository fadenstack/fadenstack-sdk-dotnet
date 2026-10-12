# Fadenstack.UI.Wpf

A chat panel for [Fadenstack](https://fadenstack.com) agents in WPF applications and add-ins, including Office VSTO
add-ins and Windows Forms hosts. It renders the view models of
[Fadenstack.UI](https://www.nuget.org/packages/Fadenstack.UI) and follows the system's light or dark theme. The
code is in the `Fadenstack.UI.Wpf` namespace.

```bash
dotnet add package Fadenstack.UI.Wpf
```

```csharp
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;

var chat = new AgentChatViewModel(agent, approvals, new WpfDispatcher(),
    new AgentChatOptions { Clipboard = new WpfClipboard() });
panel.Content = new ChatView { DataContext = chat, Theme = ChatTheme.System };
await chat.InitializeAsync();
```

It targets `net472` and `net8.0-windows` and runs on Windows.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/chat-panel/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

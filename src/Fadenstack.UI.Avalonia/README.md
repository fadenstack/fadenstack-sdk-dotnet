# Fadenstack.UI.Avalonia

A chat panel for [Fadenstack](https://fadenstack.com) agents in cross-platform desktop applications built on
[Avalonia](https://avaloniaui.net) 12. It renders the view models of
[Fadenstack.UI](https://www.nuget.org/packages/Fadenstack.UI), follows the system's light or dark theme, and is built
for sessions of thousands of turns: virtualized turns, a scroll position that sticks to the newest answer, older turns
loaded before the reader reaches them, and far-away turns that let go of their parsed Markdown and decoded pictures.
The code is in the `Fadenstack.UI.Avalonia` namespace.

```bash
dotnet add package Fadenstack.UI.Avalonia
```

```csharp
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.ViewModels;

var chat = new AgentChatViewModel(agent, approvals, new AvaloniaUiDispatcher(),
    new AgentChatOptions { Clipboard = new AvaloniaClipboard(() => TopLevel.GetTopLevel(panel)) });
panel.Content = new ChatView { DataContext = chat };
await chat.InitializeAsync();
```

The application uses `FluentTheme`; the chat's styles (`Chat.axaml`, `Parts.axaml`) sit on its control templates and
apply inside `ChatView` only. Light, dark and high contrast (`FadenstackTheme.HighContrastVariant`, inheriting from Dark) are theme variants (`Colors.axaml`); `FadenstackTheme.Apply(target, new ThemeChoices { ... })` sets accent, density, text size and high contrast at runtime.

It targets `net8.0` and runs wherever Avalonia 12 does.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/chat-panel/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

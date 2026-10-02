# Fadenstack .NET SDK

Agents that run inside other software (EPLAN, TIA Portal, Office, any desktop application) and are
governed by a [Fadenstack](https://fadenstack.github.io) server. The SDK runs the agent loop on the
device: host tools, approvals, local MCP servers and the conversation itself. The server owns the
agent's instructions, model and knowledge, and handles PII, budgets and audit.

| Package | What it is |
|---|---|
| `Faden.Client` | The server connection: the gateway as a Microsoft.Extensions.AI `IChatClient`, agent sign-in, the agent resource (profile, approvals, events), the REST resources, typed errors |
| `Faden.Agents` | The agent: builder, the tool loop, tool classes and modes, approvals, local sessions, outcomes, modules and stages |
| `Faden.Mcp` | Local MCP servers as agent tools, under the agent's local MCP policy |
| `Faden.UI` | Platform-neutral view models for a chat with an agent (netstandard2.0; CommunityToolkit.Mvvm) |
| `Faden.UI.Wpf` | The WPF skin: a Copilot-style chat panel (net472; net8.0-windows) |

The libraries target **netstandard2.0** (.NET Framework 4.6.2+ hosts) and **net8.0**. Names match the
Python SDK: `Faden.X` / `PascalCase` + `Async` ↔ `faden.x` / `snake_case`.

## An agent in a host application

```csharp
using Faden;
using Faden.Agents;
using Faden.Tools;

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open schematic.")]
    public string ReadPage(int page) => ...;

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page) => ...;
}

// One agent per client. The sign-in is kept for this user on this device (DPAPI on Windows).
using var client = FadenClient.ForAgent(new Uri("https://faden.plant.local"), "eplan-assistant", out var tokens);
if (!await tokens.IsSignedInAsync())
    await tokens.SignInAsync(loginToken, deviceLabel: "EPLAN on PC-7");

using var agent = FadenAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("eplan-assistant")
    .AddTools(new PageTools())
    .UseApprovals(async (request, ct) => await ShowApprovalDialog(request))   // otherwise: declined
    .Build();

var session = await agent.StartSessionAsync();          // starts in Ask
await foreach (var e in session.StreamAsync("What is on page 7?"))
{
    switch (e)
    {
        case TextDelta t: Append(t.Text); break;
        case ToolCallStarted t: ShowTool(t.Name, t.Source); break;
        case NoticeRaised n: ShowNotice(n.Notice.Message); break;
        case TurnCompleted c: Done(c.Result.Outcome); break;
    }
}
```

`session.SendAsync(text)` waits for the whole turn and returns the `AgentResult`.

## Tool classes and tool modes

Every tool has a class: `Read`, `Write`, `Destructive`, or `Unknown` (nobody said). The user picks a
mode per session (`session.ToolMode`):

| Mode | Read | Write | Destructive | Unknown |
|---|---|---|---|---|
| Off | hidden | hidden | hidden | hidden |
| ReadOnly | runs | hidden | hidden | hidden |
| **Ask** (every session starts here) | runs | asks | asks | asks |
| Auto | runs | runs | asks | asks |

- *hidden* tools are not offered to the model, and refused if it calls them anyway. *asks* goes to your
  `IApprovalHandler`. `ApprovalDecision.ApproveForSession()` stops asking for that tool in that session.
- The agent's host tool contract on the server can make a class stricter, never looser. Tools
  outside a non-empty contract are not offered.
- An EE ceiling narrows what the user may pick (`session.EffectiveToolMode`). The builder's
  `UseDefaultToolMode` can only be stricter than Ask.
- The session sends its mode to the gateway (`X-Faden-Tool-Mode`), which applies the same table to its
  own tools.

## Refusals are outcomes

A turn ends with an `AgentResult` whose `Outcome` is `Completed`, `PromptBlocked`, `ResponseBlocked`,
`ToolDenied`, `RateLimited` or `BudgetExceeded`, with the `GovernanceNotice`s to show. A refused prompt
or answer is never sent to the model again, and the session goes on. Only real failures throw
(`FadenApiException` and its subclasses, `FadenStreamException`, `FadenSignInRequiredException`).

## Sessions stay on the device

Each session is an append-only transcript under the user's data folder (`%LOCALAPPDATA%\Fadenstack\sessions\<agent>\`).
Each line is encrypted with DPAPI on Windows; on other systems the files are readable by their owner only.
By default the newest 50 sessions are kept, none older than 90 days. `agent.OpenSessionAsync(id)` continues
a session in its last mode; `UseSessionStore(...)` plugs in your own store. The server keeps usage and
events (tool names, durations, outcomes, approval decisions), never content.

## Local MCP servers

```csharp
using Faden.Mcp;

builder.AddLocalMcp(LocalMcpServer.Stdio("eplan-db", "eplan-mcp.exe"));
```

They are attached at start only if the agent allows local MCP (`allow`) or the user has developer mode.
Otherwise they are not even started (`LocalMcpModule.Skipped` says why). Their tools are `Unknown`, since an
MCP server's own annotations are not trusted, so they always ask.

## On the standard .NET host

```csharp
builder.Services.AddFadenAgent((agent, services) => agent
    .UseServer(serverUrl, services.GetRequiredService<AgentTokenProvider>())
    .UseAgent("tia-assistant")
    .AddTools(services.GetRequiredService<TiaTools>()));
```

## Extending

- `IFadenModule` plugs a package into the builder (`Faden.Mcp` is one; `Faden.Enterprise` modules
  too). An agent profile's `required_client_modules` must be present, or `StartAsync` refuses.
- Stages run in a fixed order, whatever order they are added in (`StageOrder`: context → PII → custom):
  `AddChatStage` wraps the `IChatClient`, and `AddToolStage` wraps each tool call.
- `FadenTransport` makes authenticated requests with the client's tokens, TLS settings and typed errors.

## A chat panel (`Faden.UI`, `Faden.UI.Wpf`)

The UI is split in two, and the split is strict:

- **`Faden.UI`** holds all of the behaviour, as view models with no platform references: the chat
  (`AgentChatViewModel`), turns, answer parts, the tool-mode picker, session history, and the
  `IUiDispatcher` / `IClipboard` services a skin implements. It is unit-tested headless.
- **Skins only render.** `Faden.UI.Wpf` is XAML templates, styles, light and dark themes, and a few
  view-only behaviours (keep the list scrolled to the bottom, render Markdown runs, focus the composer).
  Its only code-behind picks the theme. A later Avalonia or WebView2 skin binds to the same view models.

```csharp
var approvals = new ApprovalBroker();                     // approval cards in the chat decide
var agent = FadenAgent.CreateBuilder()
    .UseClient(client).UseAgent("eplan-assistant")
    .AddTools(new PageTools())
    .UseApprovals(approvals)
    .Build();
var chat = new AgentChatViewModel(agent, approvals, new WpfDispatcher(),
    new AgentChatOptions { Clipboard = new WpfClipboard() });
panel.Content = new ChatView { DataContext = chat, Theme = ChatTheme.System };
await chat.InitializeAsync();
```

What it shows, as in GitHub Copilot's chat:

- The answer **streams as Markdown**: headings, lists, code blocks with a copy button, tables, quotes,
  links. Text and tool calls **interleave** in the order they happened.
- Each tool call is **one line** ("Running read_page" with a spinner, then "Ran read_page", its class
  and duration). It expands to input and output.
- **Approvals are cards** in the answer, with Allow, Allow for this chat, and Deny. Destructive and
  unclassified tools are marked.
- Refusals and limits are **notices**. A refused prompt is marked "Not sent", with *Edit and resend*.
  Citations ("Used 2 references"), PII notices and reasoning ("Thought for 4 s") are collapsible.
- **The tool mode** is picked in the header. Modes above an EE ceiling are greyed out.
- Other controls: **Stop** (Esc), **new chat** (Ctrl+N), **session history**, and copy / retry per
  answer. An empty chat shows the agent's greeting and suggested prompts (profile `ui.greeting`,
  `ui.suggestions`).
- Unknown gateway events show as generic notices. Modules add their own through
  `IAgentEventRenderer` and the primitives (`CardPart`, `TablePart`, `ProgressPart`, `DiffPart`, `NoticePart`).

Built for long sessions:

- The agent runs off the UI thread. Its events reach the UI in **one batch per frame** (33 ms), with
  consecutive text merged: 400 streamed tokens cost under 40 dispatches, whatever the model's speed.
- A streamed answer re-parses **only its last block**. Blocks before it are immutable, so the skin
  re-renders one item per update however long the answer grows.
- The conversation is a **virtualized, recycled** list of turns, scrolled by pixel. Tool output and
  reasoning are formatted only when expanded.
- Opening a session reads and builds **off the UI thread** and shows the latest 30 turns. *Show
  earlier messages* pages in more. A 2 000-turn encrypted session opens in about 250 ms.
- Measured end to end against a gateway: the longest single block of the UI thread was 16 ms.

In add-ins without a WPF `Application` (Office VSTO, WinForms hosts), host `ChatView` in an
`ElementHost`. Create `WpfDispatcher` on the UI thread.

## Plain API use (no agent)

`new FadenClient(new FadenClientOptions { ServerUrl = ..., Credentials = new StaticTokenProvider(token), Model = "..." })`
gives `client.Chat` (an `IChatClient` on `/v1/chat/completions`) and the server resources: `Sessions`,
`Projects`, `Tools` (including the session's tool mode), `Memory`, `Attachments` and `Services`.

## Samples

`samples/Faden.Samples.Console` is a console agent with host tools, console approvals, `/mode` and
`/sessions`:

```bash
dotnet run --project samples/Faden.Samples.Console -- --server https://faden.example --agent eplan-assistant --login-token <jwt>
```

`samples/Faden.Samples.LiveCheck` drives the SDK against a running server and says, check by check, what
works: sign-in, refresh and refresh-token replay, the profile, streaming, host tools in every tool mode,
approvals, sessions on the device, the agent's contract and the plain route refused, local MCP, tool rules
and gateway approvals, a revoked grant. The server-side steps between its phases (publish, grants, rules)
are the caller's:

```bash
FADEN_USER_EMAIL=... FADEN_USER_PASSWORD=... dotnet run --project samples/Faden.Samples.LiveCheck -- \
    --server https://faden.example --ca ca.crt --agent sdk-livecheck --checks signin,profile,chat,host-read
```

`samples/Faden.Samples.Wpf` is the chat panel in a window (Windows). With `--demo`, or without a
server, it runs against a built-in demo gateway: streamed Markdown, tool calls and approvals, with no
setup.

```bash
dotnet run --project samples/Faden.Samples.Wpf -- --demo
```

## Building a library on the SDK

Target **netstandard2.0 and net8.0**, as the SDK does. The SDK's types use `init` setters (records,
`MessageAttachment`, options). A netstandard2.0-only library that sets them, loaded by a .NET 8+
application, gets the SDK's net8.0 build and fails with `MissingMethodException` on those setters.
Built for both targets, each application loads matching builds. .NET Framework hosts use the
netstandard2.0 builds throughout.

## Build and test

```bash
dotnet test Faden.slnx
```

The WPF projects build on any OS (`EnableWindowsTargeting`). They run on Windows only.

## License

Apache-2.0

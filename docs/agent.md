# Build an agent

This page covers the client and the agent's sign-in, the builder, sessions, the events a turn streams, and cancellation.

## The client

A `FadenstackClient` is the connection to one server, for one agent. `FadenstackClient.ForAgent` creates it together with the token provider that signs the agent in:

```csharp
using Fadenstack;
using Fadenstack.Auth;

using var client = FadenstackClient.ForAgent(
    new Uri("https://ai.example.internal"), "docs-assistant", out AgentTokenProvider tokens);
```

| Parameter | Meaning |
|---|---|
| `serverUrl` | The Fadenstack server. It must use `https`, unless you set `AllowInsecureHttp`. |
| `agent` | The agent's slug or id. A client works for one agent: its chat goes to that agent's route. |
| `tokens` (out) | The `AgentTokenProvider` that signs in and supplies the client's access tokens. |
| `store` | Where the sign-in is kept. Defaults to `FileTokenStore`. |
| `configure` | Sets further `FadenstackClientOptions` before the client is created. |

The options you are most likely to set for an agent:

| Option | Meaning |
|---|---|
| `ApiUrl` | The backend that handles sign-in, when it is not at the server URL. |
| `TrustedCaCertificate` | A CA certificate (PEM or DER) to trust for the server's certificate, in addition to the operating system's store. It applies to this client's connections only, sign-in included. |
| `AllowInsecureHttp` | Allows `http://`. Use it only for a development server. `ForAgent` passes it on to the token provider. |
| `Timeout` | How long one request may take (default five minutes). Streamed requests, such as an agent's model calls, are not limited by it. |
| `HttpClient` | Your own `HttpClient`, for proxies, extra handlers or tests. |
| `GatewayEvents` | Whether the gateway sends its events (tool rounds, approvals, notices). On by default. |

```csharp
using var client = FadenstackClient.ForAgent(serverUrl, "docs-assistant", out var tokens,
    configure: options => options.TrustedCaCertificate = File.ReadAllBytes("company-ca.crt"));
```

The client never changes process-wide settings, so it is safe inside another application's process. On .NET Framework it enables TLS 1.2, and TLS 1.3 from .NET Framework 4.8, on its own connections only.

## Sign in

The agent signs in once per user and device. Check for a kept sign-in before asking the user:

```csharp
if (!await tokens.IsSignedInAsync())
{
    await tokens.SignInAsync(loginToken, deviceLabel: "Viewer on PC-7");
}
```

`SignInAsync(loginToken, deviceLabel)` exchanges the user's login token for a sign-in to this agent on this device and returns the `TokenSet`. The device label names this sign-in in the server's list of sign-ins; it defaults to the machine name. If the user has no grant for the agent, the server refuses with `FadenstackPermissionException` (403). [Your first agent](../../get-started/first-agent.md) shows how an agent is registered and granted, and where a login token comes from.

| Member | Meaning |
|---|---|
| `IsSignedInAsync()` | Whether a sign-in is kept and its refresh token has not expired. It does not ask the server. |
| `SignInAsync(loginToken, deviceLabel)` | Signs in and keeps the sign-in in the store. |
| `SignOutAsync()` | Forgets the sign-in on this device. It does not revoke it on the server; revoking is done there. |
| `DeviceId` | This device's sign-in id, once signed in. |

A sign-in sends the user's login token, so `AgentTokenProvider` refuses an `http://` API URL unless it is this machine (`localhost`, `127.0.0.1` or `::1`) or you pass `allowInsecureHttp: true` for a development server. Its constructor throws `ArgumentException` otherwise.

After the sign-in, the provider supplies access tokens by itself. An access token is short-lived and is renewed with a refresh token shortly before it expires; the server replaces the refresh token on every use. When the server refuses a renewal (the sign-in was revoked, a refresh token was used twice, or the user lost access), the provider forgets the sign-in and throws `FadenstackSignInRequiredException`. Ask the user to sign in again.

### Where the sign-in is kept

`FileTokenStore`, the default, keeps one file per server and agent in the user's local data folder (`%LOCALAPPDATA%\Fadenstack\tokens\` on Windows). On Windows the file is encrypted with DPAPI for the current user. On other systems it is not encrypted, and only its owner may read it. A file that cannot be read, such as another user's or a damaged one, counts as no sign-in.

To keep it elsewhere, pass a store:

- `MemoryTokenStore` keeps the sign-in for the lifetime of the process.
- `new FileTokenStore(directory, protector)` uses another folder or another `ISecretProtector`.
- Your own `ITokenStore` implements `LoadAsync`, `SaveAsync` and `DeleteAsync`.

```csharp
using var client = FadenstackClient.ForAgent(serverUrl, "docs-assistant", out var tokens, store: new MemoryTokenStore());
```

## The builder

`FadenstackAgent.CreateBuilder()` puts an agent together. `Build()` returns the `FadenstackAgent`.

```csharp
using Fadenstack.Agents;

using var agent = FadenstackAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("docs-assistant")
    .AddTools(new PageTools())
    .UseApprovals((request, cancellationToken) => AskTheUserAsync(request, cancellationToken))
    .Build();
```

| Method | What it does |
|---|---|
| `UseClient(client)` | Uses a client you created. It must be for the same agent. You dispose it. |
| `UseServer(serverUrl, credentials, configure)` | Lets the agent create its own client with these credentials (usually an `AgentTokenProvider`). The agent disposes it. |
| `UseAgent(slug)` | The agent's slug. Optional with `UseClient`, which brings the client's agent. |
| `AddTools(target)`, `AddTools<T>()`, `AddTool(...)` | Host tools. See [Host tools](tools.md). |
| `UseApprovals(handler)` | Who decides on calls that need approval. Without it, every such call is declined. |
| `UseApprovalStore(store)` | Where "Allow always" is kept. See [Host tools](tools.md#allow-always). |
| `UsePermissionMode(mode)` | `PermissionMode.Ask` (the default), or `PermissionMode.Bypass` for development and trusted automation. See [Host tools](tools.md#bypass-mode). |
| `UseSessionStore(store)` | Where sessions live. See [Sessions on the device](sessions.md). |
| `UseDefaultToolMode(mode)` | The mode new sessions start in. It can only be stricter than `Ask`. |
| `UseMaxRounds(rounds)` | Model calls per turn before the loop stops (default 16). |
| `UseHistoryBudget(tokens)` | About how many tokens of history a request carries (default 24 000). |
| `UseReasoning(effort)` | How much the model reasons before it answers, on every request. |
| `UseEventReporting(enabled)` | Whether the agent reports its activity and the app's manifest to the server (default on). |
| `DescribeApp(app)` | Describes the app the agent runs in. |
| `Use(module)`, `AddChatStage(...)`, `AddToolStage(...)`, `AddToolSource(...)` | Extensions. See [Extending](extending.md). |

`Build()` throws `InvalidOperationException` when no agent is set, when neither `UseClient` nor `UseServer` was called, when the client is for another agent, or when two tools have the same name.

### Describe your app

When the agent starts, it tells the server what your app brings: its host tools with the classes you gave them, the app's name and version, the SDK's version, and the context and greeting you describe. The server keeps this for its administrators to review; nothing in it changes what the agent may do. When the agent's host tool contract is not empty, a tool the contract does not list is held back until an administrator takes it in.

```csharp
using Fadenstack.Models;

builder.DescribeApp(new AppDescription
{
    Name = "Viewer add-in",
    Version = "2.1.0",
    Ui = new ManifestUi
    {
        Greeting = "Ask me about the open project.",
        Suggestions = new[] { "What is on page 7?" },
    },
});
```

The report never fails the start. `UseEventReporting(false)` turns it off, together with the activity reports.

## Start the agent

`agent.StartAsync()` loads the agent's profile from the server, checks that the client modules the agent requires are present, and settles the tools. `StartSessionAsync` and `OpenSessionAsync` call it for you; call it yourself to fail early or to read the profile. `agent.Profile.User.Language` is the language the user picked for all their clients and the console, or `null`; `agent.SetUserLanguageAsync(tag)` keeps a new one (`null` forgets it). The chat panel does both: see [Languages](chat-panel.md#languages).

| Member | Meaning |
|---|---|
| `Profile` | The agent's `AgentProfile`: name, version, model, host tool contract, tool modes, UI settings. Never its instructions. |
| `Tools` | The tools the agent offers, with the classes they run under. |
| `DefaultToolMode` | The mode new sessions start in. |
| `PermissionMode` | `Ask`, or `Bypass` when the app set it. |
| `ResetApprovalsAsync(tool)` | Forgets what the user allowed always for this agent on this device: every tool, or one. |
| `Slug`, `Client` | The agent's slug and its client. |

`StartAsync` throws `FadenstackSignInRequiredException` when there is no sign-in, `FadenstackPermissionException` when the user may not use the agent, and `FadenstackException` when the agent requires a client module the builder does not have.

## Sessions

A session is one conversation, kept on the device.

```csharp
var session = await agent.StartSessionAsync();       // starts in Ask
```

| Member | Meaning |
|---|---|
| `agent.StartSessionAsync(toolMode)` | A new session, in the agent's default mode unless you pass the mode the user picked. |
| `agent.OpenSessionAsync(id)` | Continues a session stored on this device. |
| `agent.ListSessionsAsync()`, `agent.DeleteSessionAsync(id)` | The sessions on this device. |
| `session.Id` | The session's id. |
| `session.ToolMode`, `session.EffectiveToolMode` | The user's pick, and the mode turns run in. See [Host tools](tools.md#tool-modes). |
| `session.Transcript`, `session.History` | What is kept, and what the next request would carry. |
| `session.ApprovedForSession`, `session.ResetApprovals(tool)` | The tools the user allowed for this chat, and forgetting them. Kept in memory only. |
| `session.CloseAsync()` | Reports the session's counts (never content) and sends what is left to report. |

A session runs one turn at a time. Starting a turn while another runs in the same session throws `InvalidOperationException`.

[Sessions on the device](sessions.md) covers storage and retention.

## Stream a turn

`StreamAsync` sends a message and streams what happens, in order:

```csharp
await foreach (var e in session.StreamAsync("What is on page 7?"))
{
    switch (e)
    {
        case TextDelta t: Append(t.Text); break;
        case ToolCallStarted t: ShowTool(t.Name, t.Source); break;
        case ToolCallCompleted t: ShowToolDone(t.Name, t.Outcome, t.Duration); break;
        case NoticeRaised n: ShowNotice(n.Notice.Message); break;
        case TurnCompleted c: Done(c.Result); break;
    }
}
```

| Event | When | Fields |
|---|---|---|
| `TurnStarted` | First. | `ToolMode`: the mode this turn runs in. |
| `TextDelta` | Answer text arrives. | `Text` |
| `ReasoningDelta` | The model's reasoning arrives, from models that send it. | `Text` |
| `ToolCallStarted` | A tool starts. | `CallId`, `Name`, `Class`, `Source` (`host`, `mcp` or `gateway`), `Arguments` (empty for gateway tools) |
| `ToolCallCompleted` | A tool ended. | `CallId`, `Name`, `Source`, `Outcome` (`ok`, `error` or `denied`), `Result` (null for gateway tools), `Duration` (the tool's own time, without the wait for approval) |
| `ApprovalRequested` | A call waits for the user. Your approval handler is called right after. A call that an earlier approval lets through raises no approval events. | `Request` |
| `ApprovalDecided` | The decision was made. `Decision` says what was taken: `ForSession` or `Always` only where the request offered it. | `Request`, `Decision` |
| `NoticeRaised` | A refusal or a limit to show on the turn. | `Notice` |
| `GatewayEvent` | The gateway sent an event. | `Event` |
| `TurnCompleted` | Last, when the turn ended with an outcome. | `Result`: the `AgentResult` |

Every event carries the turn's `TurnId`.

`GatewayEvent.Event` is a `FadenstackEventContent`: the event as the gateway sent it (`Event`), its type (`Type`), the name of a custom event (`Name`), its value (`Value`) and `Kind`, which is the name for a custom event and the type otherwise. Citations (`faden.citations`) and personal-data notices (`faden.pii`) arrive this way. Every gateway event arrives as a `GatewayEvent`, including those the SDK also turns into the events above, so render the kinds you know and ignore the rest.

A turn that is cancelled or fails has no `TurnCompleted`.

## Wait for the whole turn

`SendAsync` runs the same turn and returns the `AgentResult` when it has ended:

```csharp
var result = await session.SendAsync("What is on page 7?");
Console.WriteLine(result.Text);
```

[Outcomes and errors](outcomes.md) describes the result.

## Attach context

A message can carry context the user attached, such as a selection, a range or a document's text. Pass it as `MessageAttachment` values:

```csharp
var result = await session.SendAsync("What does this range sum to?", new[]
{
    new MessageAttachment
    {
        Title = "Sheet1!A1:A3",
        Content = "| A |\n|---|\n| 40 |\n| 2 |",
        Source = "excel",
        Kind = "range",
    },
});
```

| Property | Meaning |
|---|---|
| `Title` | What the user sees, for example `Selection` or `Slide 3`. |
| `Content` | The text the model gets. |
| `MediaType` | The content's type, `text/markdown` by default. |
| `Source` | Where it comes from, for example `excel`. |
| `Kind` | What it is, for example `selection`, `range` or `document`. |
| `Reference` | An id your tools can read again, for example to fetch the rest of a truncated range. |

The model gets each attachment before the question, in a block of its own. Attachments are kept with the message in the session, so later turns still see what the question was about.

## Cancellation

Pass a `CancellationToken` to `StreamAsync` or `SendAsync`. Cancelling it stops the turn: the request to the gateway is cancelled, a running tool gets the cancellation through its `CancellationToken` parameter, a pending approval's token is cancelled, and `OperationCanceledException` is thrown.

Leaving an `await foreach` over `StreamAsync` early, for example with `break`, also cancels the turn. No exception is thrown in that case.

A cancelled turn is recorded as cancelled in the session and is never sent to the model again. The session goes on with the next message.

## Reasoning, rounds and history

- `UseReasoning(ReasoningEffort.None)` asks a reasoning model to answer at once, which suits quick lookups. `Low`, `Medium`, `High` and `ExtraHigh` ask for more. Without it, the model uses its own setting. Models without a setting ignore it.
- `UseMaxRounds(rounds)` limits the model calls in one turn. When the limit is reached, the turn ends with a `round_limit` notice.
- `UseHistoryBudget(tokens)` sets about how many tokens of earlier turns a request carries. The oldest turns are left out first; the current turn is always sent.

`ReasoningEffort` is the Microsoft.Extensions.AI type.

## What is reported to the server

With event reporting on (the default), the agent reports what it did as names, counts, durations and outcomes: tool runs, approval decisions with what let each call through (`once`, `chat`, `always` or `bypass`), attached local MCP servers and session counts. It never reports arguments, results or messages. Reports are sent after each turn; a report that fails is dropped and never fails the turn. `agent.FlushAsync()` sends what is pending.

[Sessions](../../concepts/sessions.md) describes what the server keeps.

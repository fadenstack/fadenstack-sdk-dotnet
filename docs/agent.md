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
| `SignOutAsync()` | Revokes the sign-in at the server (best effort), then forgets it on this device. |
| `DeviceId` | This device's sign-in id, once signed in. |

A sign-in sends the user's login token, so `AgentTokenProvider` refuses an `http://` API URL unless it is this machine (`localhost`, `127.0.0.1` or `::1`) or you pass `allowInsecureHttp: true` for a development server. Its constructor throws `ArgumentException` otherwise.

After the sign-in, the provider supplies access tokens by itself. An access token is short-lived and is renewed with a refresh token shortly before it expires; the server replaces the refresh token on every use. When the server refuses a renewal (the sign-in was revoked, a refresh token was used twice, or the user lost access), the provider forgets the sign-in and throws `FadenstackSignInRequiredException`. Ask the user to sign in again.

## Sign in by pairing

A desktop app should not handle a password. Pairing (the OAuth device grant, RFC 8628) lets the person approve the app in the web console, in their browser, where they sign in however the server lets them (password, SSO, MFA). The app shows a short code and receives the same agent tokens a sign-in issues.

```csharp
var store = new FileTokenStore();
var pairing = new AgentPairing(serverUrl, new HttpClient(), store);

var start = await pairing.StartAsync("docs-assistant", "Desktop agent on " + Environment.MachineName,
    PairingClient.ForThisDevice("Desktop assistant", "1.2.3"));
Console.WriteLine($"Approve {start.UserCode} at {start.VerificationUriComplete}");   // open the link in the browser

var result = await pairing.WaitAsync(start, new Progress<PairingProgress>(p => Console.WriteLine($"{p.State}, {p.TimeLeft} left")));
Console.WriteLine($"Signed in as {result.SignedInUser?.DisplayName}");

var tokens = new AgentTokenProvider(serverUrl, "docs-assistant", store);   // same server, agent and store: signed in
```

### One pairing for several agents

An app suite (for example the Office add-ins) can sign in all its agents with one approval. Name the further agents in `StartAsync`; the person sees every agent in the console before approving, and the device then collects one token set per agent, each redeemable once. At most 8 agents in all, none repeated (`ArgumentException` before any request).

```csharp
string[] suite = ["word-assistant", "excel-assistant", "outlook-assistant"];
var start = await pairing.StartAsync(suite[0], "Office on " + Environment.MachineName,
    PairingClient.ForThisDevice("Office add-ins", "1.0"), suite.Skip(1));
Console.WriteLine($"Approve {start.UserCode} at {start.VerificationUriComplete}");

await pairing.WaitAsync(start);                  // waits for the approval; the first agent's tokens
foreach (var agent in suite.Skip(1))
{
    await pairing.RedeemAsync(start, agent);     // one call each, no waiting; tokens saved under that agent
}
var excel = new AgentTokenProvider(serverUrl, "excel-assistant", store);   // signed in
```

`WaitAsync(start, agent)` waits for the approval and returns that agent's tokens, so the agents can also be collected in any order. `RedeemAsync(start, agent)` is for an agent after the approval is known. If the person may not use an agent, or its tokens were already collected, the server answers `invalid_grant` and the call throws `FadenstackPairingAgentException` (its `Agent` names it); nothing is saved. `PairingStart.Agents` is the list the server stored; `PairingStart.AgentsLeft` and `PairingResult.AgentsLeft` name the agents whose tokens are still to collect.

A server that does not know `agents` (an older 0.7.1 candidate) ignores the list: `PairingStart.Agents` is empty, only the first agent can be signed in, and the others stay in `AgentsLeft`. Asking for one of them throws `FadenstackPairingAgentException` without a request; start another pairing for them. A pairing for one agent sends exactly what it always did.

`WaitAsync` polls the server's interval, five seconds longer after each `slow_down`, and retries network errors after 5, 10, 20 and then 30 seconds while the pairing is valid. It ends with `FadenstackPairingDeniedException` (denied in the console), `FadenstackPairingExpiredException` (the code expired) or, from `StartAsync`, `FadenstackPairingUnsupportedException` (the server is older than the feature). All derive from `FadenstackException`. The pairing code the app polls with stays in the `PairingStart` object in memory: it is never stored, logged or printed. The user code may be shown on screen, not logged. To trust a server's own CA, build the `HttpClient` on `FadenstackTransport.CreateHandler(options)` with `TrustedCaCertificate` set.

The tokens and who signed in (`SignedInUser`: id, e-mail, display name) go into the `ITokenStore` under the same key `AgentTokenProvider` uses. Token sets stored before the user was kept still load, with no user.

| Member | Meaning |
|---|---|
| `AgentTokenProvider.SignedInUser` | Who is signed in, once the provider has loaded the sign-in; `GetSignedInUserAsync()` reads it from the store. |
| `AgentTokenProvider.SignOutAsync()` | Revokes the sign-in at the server (`POST /api/agent-tokens/revoke`, best effort: a network failure is ignored), then deletes the tokens on this device. Afterwards sign-in is required. |

### The sign-in page

The server names the page where the person approves the app (`PairingStart.VerificationUri`, and `VerificationUriComplete` with the code in the address). That page must be on the server's own address: the same scheme, host and port as the server URL you gave `AgentPairing`, and with no user name or password in the address. A relative address is the server's own. If the server names a page anywhere else, `StartAsync` throws `FadenstackException` before anything is shown, because a page on another site would be a phishing page. The TypeScript SDK refuses the same. Open the address in the system browser, and show `UserCode` beside it.

### A server that is too old

A server from before pairing has no route to start it. It answers `POST /api/agent-tokens/pairings` with 404, or with 405: core 0.7.0 does, because the same path belongs to another method there. `StartAsync` throws `FadenstackPairingUnsupportedException` for both, and `WaitAsync` and `RedeemAsync` throw it when their routes answer the same way. Nothing was started and nothing is kept.

The exception's message is written for the person ("This server does not support signing in from the desktop app yet. Ask your IT to update Fadenstack."). Show it, and offer the sign-in with a login token ([Sign in](#sign-in)) where your app has a way to get one. Do not try pairing again in a loop: the server will not change by itself.

### Where the sign-in is kept

`FileTokenStore`, the default, keeps one file per server and agent in the user's local data folder (`%LOCALAPPDATA%\Fadenstack\tokens\` on Windows). On Windows the file is encrypted with DPAPI for the current user. On macOS and Linux it is encrypted when the app called `FadenstackSecrets.UseOsKeyStore()` (see [Sessions on the device](sessions.md#where-they-are-stored)), and otherwise it is not encrypted and only its owner may read it. A file that cannot be read, such as another user's or a damaged one, counts as no sign-in.

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
| `App` | The app as you described it with `DescribeApp`, or `null`. The chat panel's About shows it. |

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
| `session.Approvals` | Who is asked about this session's calls instead of the agent's approval handler; null (the default) means the agent's. A task that another agent hands over sets it. |
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

**Call ids.** `CallId` ties a `ToolCallStarted` to its `ToolCallCompleted`, to an approval and to what the gateway says about the call. A model may send a call with no id, or two calls of one round with the same id. The SDK then gives such calls ids of its own, `fs-<turn id>-<round>-<index>`: the round counts the turn's model calls from 0, and the index is the call's place in that round. Calls with ids of their own keep them. The new ids are what the events, the transcript and the results sent back to the model carry. When the gateway refuses a call and can only name the tool, because the call has no id of its own or its id is shared, the refusal covers every call of that tool in the round, so a refusal is never lost.

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
| `Content` | The text the model gets; for a picture, its `data:` URL. |
| `MediaType` | The content's type, `text/markdown` by default; `image/...` for a picture. |
| `Source` | Where it comes from, for example `excel`. |
| `Kind` | What it is, for example `selection`, `range`, `document` or `picture`. |
| `Reference` | An id your tools can read again, for example to fetch the rest of a truncated range. |

The model gets each attachment before the question, in a block of its own. Attachments are kept with the message in the session, so later turns still see what the question was about.

### Pictures

An attachment whose `MediaType` is `image/...` is a picture: its `Content` is a `data:image/...;base64,` URL, and the model gets it as an image before the text. Send pictures only when `agent.Profile.Vision` is true: a model that does not see pictures cannot use them. The history carries the pictures of the newest messages only, `ContextBuilder.MaxHistoryPictures` (4) in all, because each costs the model about as much as a page of text; older ones are named in their place. The newest message keeps all of its own.

## Pick skills

When the server runs the skills module (`agent.Profile.Skills`), the user can pick skills for a message. `agent.ListSkillsAsync(query)` lists the ones they can pick (none on a server without skills), and `SendOptions.Skills` sends them with the message:

```csharp
var skills = await agent.ListSkillsAsync("reply");
var result = await session.SendAsync("Answer this mail.", new SendOptions
{
    Skills = new[] { new PickedSkill(skills[0].Slug, skills[0].Name) },
});
```

Every model call of the turn gets the skills, and the session keeps them with the message. The next message has none unless they are picked again.

## The organisation's tools

The server lists the tools of the organisation's MCP servers that this user and this agent can have, with what the agent's tool rules make of each. An app can show them, for example on a Tools page:

```csharp
var tools = await agent.Client.Agent!.ListToolsAsync();
if (tools is null)
{
    // The server is too old to list them.
}
else
{
    foreach (var tool in tools)
    {
        Console.WriteLine($"{tool.Server}: {tool.Name} (allowed {tool.Allowed}, switched on {tool.Enabled})");
    }
}
```

`ListToolsAsync` calls `GET /v1/agents/{agent}/tools` and returns `AgentToolInfo` values:

| Property | Meaning |
|---|---|
| `Name` | The name the model calls it by, such as `mcp.github.search`. |
| `Server` | The MCP server that brings it. |
| `Description` | What the server says it does. |
| `Allowed` | Whether the agent's tool rules let its requests be offered and run the tool. |
| `Enabled` | Whether the tool is switched on. An administrator may have switched it off. |

It returns names and descriptions only, never how a server is reached. The list is empty when the user has no such tools, and `null` when the server has no route for it (404). An unknown agent (`agent_not_found`) is an error, as is a user who may not use the agent (`FadenstackPermissionException`). The MCP servers on the device are another thing: see [Local MCP servers](local-mcp.md).

## Logging

The agent logs through the `ILoggerFactory` you give the builder, and so do its client, its local MCP servers and the provenance module. Without one, nothing is logged:

```csharp
builder.UseLogging(loggerFactory);
```

A client without an agent has `FadenstackClientOptions.LoggerFactory`, and `AgentPairing` takes an `ILogger`.

**The logs are free of content.** The SDK's own entries hold names, ids, counts, durations, outcomes and the type of an error. They never hold a prompt, an answer, the model's reasoning, an attachment, a tool's arguments or results, a token, a secret or who the user is. A turn's entries sit in a scope with the conversation and turn ids. What your app logs through the same factory is yours to keep free of content.

Each package has its own range of event ids and its own logger category:

| Package | Category | Event ids | Examples |
|---|---|---|---|
| `Fadenstack.Client` | `Fadenstack.Client` | 1000-1099 | Requests (method, route template, status, duration), sign-in renewals, pairing progress, a skipped event |
| `Fadenstack.Agents` | `Fadenstack.Agents` | 2000-2099 | 2000 profile loaded, 2010 and 2011 a turn started and ended, 2020 a tool call, 2021 an approval, 2040 transcripts kept in memory only |
| `Fadenstack.Mcp` | `Fadenstack.Mcp` | 3000-3099 | A local server attached, refused, stopped or restarted |
| `Fadenstack.Provenance` | `Fadenstack.Provenance` | 4000-4099 | A placement that was not recorded or reported |
| `Fadenstack.Secrets` | | 5000-5099 | 5000 no usable OS key store |

**Traces and metrics** need no factory. Subscribe to the names in `FadenstackTelemetry`, for example with OpenTelemetry:

| Name | What it is |
|---|---|
| `FadenstackTelemetry.ClientSource` | Spans of the client's HTTP requests. |
| `FadenstackTelemetry.AgentsSource` | Spans of turns, model requests and tool calls. |
| `FadenstackTelemetry.AgentsMeter` | `fadenstack.agent.turns`, `fadenstack.agent.tool_calls`, `fadenstack.agent.tool.duration` and `fadenstack.agent.approval.wait`. |

Tags carry names, ids, counts and outcomes, never content.

### A log file

A desktop app or an add-in often wants its logs in a file. `FadenstackFileLoggerProvider` writes rolling files in the operating system user's own log folder:

```csharp
using Fadenstack.Logging;

using var files = new FadenstackFileLoggerProvider("my-addin");
using var factory = LoggerFactory.Create(logging => logging.AddProvider(files));   // Microsoft.Extensions.Logging

builder.UseLogging(factory);
```

The files are `<app>-<yyyyMMdd>.log`, one line per entry: the time in UTC, the level, the category, the event id and the message, and for an exception its type, message and stack trace. The folder is `FadenstackPaths.LogRoot`: `%LOCALAPPDATA%\Fadenstack\logs` on Windows, `~/Library/Logs/Fadenstack` on macOS, and `$XDG_STATE_HOME/Fadenstack/logs` on Linux (`~/.local/state/Fadenstack/logs` when the variable is not set). A folder you choose in `FadenstackFileLoggerOptions.Folder` must be that folder or below it, or the constructor throws `ArgumentException`: logs stay in the user's own folder, never in a machine-wide one.

| `FadenstackFileLoggerOptions` | Meaning |
|---|---|
| `Folder` | Where the files go; the log root by default. |
| `MaxFileBytes` | A file is closed and numbered (`.1`, `.2`) before it passes this size. Default 5 MB. |
| `MaxAge` | Files older than this are deleted. Default 14 days. |
| `MaxTotalBytes` | The oldest files are deleted while the app's files together are larger. Default 50 MB. |
| `QueueCapacity` | Entries waiting for the writer; more are dropped. Default 4096. |

Logging never holds up or fails the caller. Entries go to a queue and one background writer. When the queue is full, an entry is dropped and counted (`Dropped`). Disposing the provider writes what is queued. The provider touches only files of its own app name, because add-ins and a desktop agent share the folder. It writes every level its factory passes on, so set a minimum level on the factory.

## Cancellation

Pass a `CancellationToken` to `StreamAsync` or `SendAsync`. Cancelling it stops the turn: the request to the gateway is cancelled, a running tool gets the cancellation through its `CancellationToken` parameter, a pending approval's token is cancelled, and `OperationCanceledException` is thrown.

Leaving an `await foreach` over `StreamAsync` early, for example with `break`, also cancels the turn. No exception is thrown in that case.

A cancelled turn is recorded as cancelled in the session and is never sent to the model again. The session goes on with the next message. So is a turn the app closed on before it ended: once a newer turn starts, it is left out like a cancelled one, so no tool call goes to the model without its result.

## Reasoning, rounds and history

- `UseReasoning(ReasoningEffort.None)` asks a reasoning model to answer at once, which suits quick lookups. `Low`, `Medium`, `High` and `ExtraHigh` ask for more. Without it, the model uses its own setting. Models without a setting ignore it.
- `UseMaxRounds(rounds)` limits the model calls in one turn. When the limit is reached, the turn ends with a `round_limit` notice.
- `UseHistoryBudget(tokens)` sets about how many tokens of earlier turns a request carries. The oldest turns are left out first; the current turn is always sent.

`ReasoningEffort` is the Microsoft.Extensions.AI type.

### How long it thinks: the person's choice

The chat UIs show a "thinking" pill beside the tool mode, where the person picks how long the agent thinks: Auto, Off (`none`), Low, Medium or High, as in other agents. The choice is kept per person and per agent on the device (`users/<user>/preferences/reasoning.json` in the SDK's data folder); a new chat starts from it and a change applies from the next message.

The server can only set a maximum, in the agent version's `ui`:

```json
{ "reasoning": { "max": "low" } }
```

- `max` is optional. It takes the gateway's names, from least to most: `none`, `minimal`, `low`, `medium`, `high`, `xhigh` and `max`. Anything else, or a value that is not a string, is ignored, as if the server had set none.
- The SDK can send `none`, `low`, `medium`, `high` and `xhigh`. `minimal` has no level of its own in `ReasoningEffort`, so it caps at `none`, the level below it that the SDK can send; the server still caps every request itself. `max` is the top of the scale: it sets no cap, and the agent behaves as if the server had set no maximum.
- The pill hides the levels above the maximum; Off is always offered. A stored choice above a later, lower maximum shows and sends the maximum.
- With a maximum, Auto means the maximum (it is sent, so with `xhigh` Auto sends `xhigh`). Without one, or with `max`, Auto sends no field.
- The order is: the person's choice, then `UseReasoning`, then Auto, each lowered to the maximum.
- In code: `agent.ReasoningChoices` (`Levels`, `Max`), `agent.Reasoning`, `agent.ReasoningChoice`, `agent.SetReasoningAsync(level)` and `session.Reasoning`. The turn's start entry in the session records `reasoning_effort`; the client events carry nothing about it.
- A host that passes both its own session store and its own approval store has taken over the device's storage: the person's choice then lasts for the run only.

## What is reported to the server

With event reporting on (the default), the agent reports what it did as names, counts, durations and outcomes: tool runs, approval decisions with what let each call through (`once`, `chat`, `always` or `bypass`), attached local MCP servers and session counts. It never reports arguments, results or messages. Reports are sent after each turn; a report that fails is dropped and never fails the turn. `agent.FlushAsync()` sends what is pending.

[Sessions](../../concepts/sessions.md) describes what the server keeps.

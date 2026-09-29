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

## Plain API use (no agent)

`new FadenClient(new FadenClientOptions { ServerUrl = ..., Credentials = new StaticTokenProvider(token), Model = "..." })`
gives `client.Chat` (an `IChatClient` on `/v1/chat/completions`) and the server resources: `Sessions`,
`Projects`, `Tools` (including the session's tool mode), `Memory`, `Attachments` and `Services`.

## Sample

`samples/Faden.Samples.Console` is a console agent with host tools, console approvals, `/mode` and
`/sessions`:

```bash
dotnet run --project samples/Faden.Samples.Console -- --server https://faden.example --agent eplan-assistant --login-token <jwt>
```

## Build and test

```bash
dotnet test Faden.slnx
```

## License

Apache-2.0

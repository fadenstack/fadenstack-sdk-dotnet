# Host tools

This page covers how you declare host tools, how the agent calls them, and how the session's tool mode and the user's approvals decide whether a call runs.

## Declare tools

A host tool is a method of your application that the model may call. Mark it with `[AgentTool]`, say what it does with `[Description]`, and add the object to the builder:

```csharp
using System.ComponentModel;
using Fadenstack.Agents;
using Fadenstack.Tools;

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open document.")]
    public string ReadPage([Description("The page number, from 1.")] int page) => Document.Read(page);

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
    public string AddComment(int page, string text) => Document.Comment(page, text);

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page) => Document.Delete(page);
}
```

```csharp
builder.AddTools(new PageTools());
```

The rules:

- `AddTools(target)` takes the public methods of `target` that carry `[AgentTool]`, instance and static. Pass a `Type` to take its static methods only. `AddTools<T>()` creates a `T` first. A target without any marked method throws `ArgumentException`.
- The tool's name is the method's name in snake_case, without an `Async` suffix: `ReadPageAsync` becomes `read_page`. Set `Name` on the attribute to choose another.
- The description comes from `[Description]` on the method. The model reads it to decide when to call the tool, so say what the tool does and what it returns.
- `[AgentTool(ToolClass.Read)]` gives the tool its class. Without a class, the tool is `Unknown`.
- `OpenWorld = true` marks a tool that reaches outside the organisation, such as a web search. It is recorded, and its approval request says so (`ApprovalRequest.OpenWorld`): the chat panel's card says that what it is given leaves the organisation. Below `Auto`, a call to such a tool asks ([Open-world tools](#open-world-tools)).
- Tool names must be unique. `Build()` throws when two tools share a name.

`AddTool(function, toolClass)` adds a Microsoft.Extensions.AI `AIFunction` made elsewhere, for example with `AIFunctionFactory.Create`. Without a class it is `Unknown`.

Five names are the SDK's own (`AgentTool.ReservedNames`). A Fadenstack gateway gives each the class of the SDK's tool, so an app's tool cannot take one:

| Name | The SDK's tool |
|---|---|
| `find_tool` | The tool search of the local MCP module (`AgentTool.ToolSearchName`): read |
| `load_skill`, `read_skill_resource` | Skills on the device (coming): read |
| `run_skill_script` | Kept, and never offered: skill scripts stay off |
| `web_search` | Web search (coming): read, open-world |

- `AddTool`, `AddTools` and a module's `Configure` refuse a tool of one of these names (`ArgumentException`).
- A tool source of the app's that brings one has it left out (logged, event 2016). Only the SDK's own sources bring them; a local MCP server's tool of such a name is left out too (event 3007).
- A tool source's tool whose name is taken is left out as before: the app's own tool wins, then the first source. From 0.3.0 it is logged with what won (event 2017; event 3008 between two local MCP servers).

A tool can say what a call acts on, for the approval card: `AddTool(new AgentTool(function, ToolClass.Destructive) { Describe = (args, ct) => ... })`, returning text such as `button "Send" on mail.example.com`. The agent calls it only when it asks the user, and the card shows it above the arguments (`ApprovalRequest.Target`), since `ref: e12` tells the user little. Null, empty, an exception or no answer within 3 seconds leaves the card with the arguments.

## Parameters and return values

Tools are Microsoft.Extensions.AI functions, so the usual rules of `AIFunctionFactory` apply:

- The parameters become the JSON schema the model sees. `[Description]` on a parameter describes it, and a default value makes it optional.
- A `CancellationToken` parameter is not shown to the model. It receives the turn's token, which is cancelled when the turn is stopped.
- Asynchronous methods (`Task<T>`, `ValueTask<T>`) are awaited.
- A returned string goes to the model as it is. Any other value goes as JSON (an empty list or object too).
- For nothing (a `void` method, `null`, an empty or a blank string) the model gets `ContextBuilder.NoToolResult`, "The tool returned no result.": an empty tool message makes some models stop or guess. A picture without text goes with it as its text. The transcript and the tool's card keep what the tool returned.

```csharp
[AgentTool(ToolClass.Read), Description("Finds devices whose name contains the text.")]
public async Task<IReadOnlyList<string>> FindDevicesAsync(string text, CancellationToken cancellationToken)
{
    return await Project.FindDevicesAsync(text, cancellationToken);
}
```

When a tool throws, the model gets `Error:` followed by the exception's message, the call's outcome is `error`, and the turn goes on. Keep anything the model should not see out of exception messages.

When the model sends arguments that are not valid JSON, the tool is not run and the model gets an error.

### Services a call gets

A parameter whose type is marked `[ToolService]` is not shown to the model either. The call gets it from its tool services: a module adds a factory with `builder.AddToolService<T>(invocation => ...)`, the factory runs the first time the call or one of its stages asks for the service, and both get the same instance. `Fadenstack.Provenance` gives tools `IProvenance` this way. A parameter with a default value gets the default when no module added the service; without one, the call fails with a message that names the missing service. A tool can also take an `IServiceProvider` and ask it for the services itself.

## Content for people

A tool that puts content a model wrote where people read it -- text into a document, a mail, a web page, a picture onto a slide -- says so with `[Emits]`: the argument that carries the content, its media type, and the kind of edit.

```csharp
[AgentTool(ToolClass.Write), Emits("text", "text/markdown", EditKind.Generated), Description("Inserts a reply at the cursor.")]
public Task<string> InsertReplyAsync(string text, IProvenance provenance, CancellationToken ct) => ...;
```

| Edit kind | Meaning | Marked by default |
|---|---|---|
| `Generated` | New content | Yes |
| `Rewritten` | Changes style, structure or meaning | Yes |
| `Summarised` | A summary of existing content | Yes |
| `Translated` | A translation | No |
| `Corrected` | Spelling, grammar, minor stylistic polish | No |
| `Formatted` | Layout, styles, format conversion | No |
| `Extracted` | Existing content selected or structured, not summarised | No |

`EditKindFrom` names a method of the same class, `EditKind Name(IReadOnlyDictionary<string, object?> arguments)`, that decides the kind per call. Declared `EditKind?`, it returns null for a call whose argument is not content for people to read -- a form tool that picks an option in a drop-down or ticks a check box; such a call is not marked and not reported:

```csharp
[AgentTool(ToolClass.Write), Emits("value", "text/plain", EditKindFrom = nameof(KindOf)), Description("Fills a form field.")]
public Task<string> FillFieldAsync(string value, string kind, IProvenance provenance, CancellationToken ct) => ...;

private static EditKind? KindOf(IReadOnlyDictionary<string, object?> arguments) =>
    arguments.TryGetValue("kind", out var kind) && kind?.ToString() == "text" ? EditKind.Generated : null;
```

An `[Emits]` that names no parameter of the method throws `ArgumentException` when the tools are added. The declarations are in `AgentTool.Outputs` and in the app's manifest; `ToolOutput.EditKindFor(arguments)` gives one call's kind (null: not content). They are metadata only: [Content provenance](provenance.md) marks what the tool places.

A host that names its tools itself builds each from its method: `AgentTool.FromMethod(method, target, name, toolClass, description)` binds the parameters as `AgentTool.From` does (tool services from the call) and reads the method's `[Emits]` and `EditKindFrom` into `Outputs`; `AgentTool.OutputsOf(method, target)` reads only the declarations, for an `AgentTool` you build from your own `AIFunction`.

## Tool classes

Every tool has a class that says what it does:

| Class | Meaning |
|---|---|
| `Read` | No side effects. |
| `Write` | Changes something that can be changed back. |
| `Destructive` | Deletes, sends, pays, or cannot be undone. |
| `Unknown` | Nobody said what it does. It always asks, and `ReadOnly` hides it. |

The class you give is what you say the tool does. The agent's host tool contract on the server can make a class stricter, never looser, and the agent runs each tool under the stricter of the two. A tool you gave no class runs under the contract's class. When the contract is not empty, host tools it does not list are not offered to the model at all. After the agent has started, `agent.Tools` lists the tools it offers with the classes they run under.

The server has the last word on what a tool may do. So when the agent's contract is empty, every host tool runs as `Unknown`, whatever class you gave it, and asks every time. A contract entry without a `risk` makes that tool `Unknown` too. Your classes take effect once an administrator has taken the tools into the contract with their risks.

[Agents on the server](../../concepts/agents.md) describes the host tool contract.

## Tool modes

Each session has a tool mode, which the user picks: `Off`, `ReadOnly`, `Ask` or `Auto`. Every session starts in `Ask`. For each tool class, the mode decides whether a call runs, asks the user first, or is hidden from the model. [Tools and approvals](../../concepts/tools.md) has the table.

```csharp
var session = await agent.StartSessionAsync();   // Ask
session.ToolMode = ToolMode.Auto;                // from the next turn
```

- `session.ToolMode` is the user's pick. You can change it at any time; a turn runs in the mode it started with, which `TurnStarted.ToolMode` reports.
- `agent.StartSessionAsync(toolMode)` starts a session in a mode the user picked.
- `UseDefaultToolMode(mode)` sets the mode new sessions start in. It can only be stricter than `Ask`: `Off` and `ReadOnly` take effect, a wider mode is ignored.
- `session.EffectiveToolMode` is the mode turns run in. When the server limits the modes a user can pick (a ceiling, from an administrator's rules), `EffectiveToolMode` holds the narrowed mode.
- A session continued with `OpenSessionAsync` keeps the mode it was last used in.

A hidden tool is not offered to the model. If the model calls it anyway, the call is refused, the turn gets a `tool_denied` notice, and the outcome is `ToolDenied`.

Each request carries the session's mode to the gateway, which applies the same rules to the server's own tools.

`ToolModes.Decide(mode, toolClass, openWorld)` returns what a mode does with a class (`Run`, `Ask` or `Hidden`), if you want to show it in your UI. `ToolModes.ToWire` and `ToolModes.ParseMode` convert modes to and from their wire names (`off`, `read_only`, `ask`, `auto`).

## Approvals

When the mode says a call asks, or an administrator's rule does, the agent asks your approval handler. Pass one to the builder, either as an `IApprovalHandler` or as a function:

```csharp
builder.UseApprovals(async (request, cancellationToken) =>
{
    var choices = "y" + (request.CanAllowForSession ? "/c" : "") + (request.CanAllowAlways ? "/a" : "") + "/N";
    Console.Write($"Run {request.ToolName} ({request.ToolClass})? {request.Reason} [{choices}] ");
    var answer = await Task.Run(Console.ReadLine, cancellationToken);
    return answer?.Trim() switch
    {
        "y" => ApprovalDecision.Approve(),
        "c" => ApprovalDecision.ApproveForSession(),
        "a" => ApprovalDecision.ApproveAlways(),
        _ => ApprovalDecision.Deny("The user said no."),
    };
});
```

The request tells you what to show:

| Property | Meaning |
|---|---|
| `ToolName` | The tool. |
| `ToolClass` | The class it runs under. |
| `Source` | `host`, `mcp` or `gateway`. |
| `Reason` | Why it asks: the tool's class and the mode, or the administrator's message for a rule. |
| `Arguments` | The call's arguments. Empty for gateway tools, whose arguments stay on the server. |
| `CallId` | The call's id. |
| `SessionId`, `TurnId` | The session and the turn the call belongs to. A screen that shows several chats at once uses them to hand the request to the chat that owns it. Empty when a caller does not say. |
| `Server`, `ReadOnlyHint`, `DestructiveHint` | For a tool from a local MCP server: its label, and what the server says about the tool (it only reads, it may delete or overwrite). Null where it said nothing. The hints are shown on the card; no tool mode acts on them. |
| `CanAllowForSession` | Whether you may offer "Allow for this chat". |
| `CanAllowAlways` | Whether you may offer "Allow always". |
| `GrantDurations` | The times you may offer for "Allow for…" (`CanAllowForTime`): a grant the server holds, for a `Read` or `Write` tool, up to the agent's longest grant. Empty where it is not offered. |
| `RequiredByRule` | An administrator's "Ask first" rule covers the call. `Reason` is the administrator's message. |
| `OpenWorld` | The tool reaches outside the organisation: what it is given goes to another site. |
| `Target` | What the call acts on, as the tool's `Describe` says it, one line of at most 300 characters; null when the tool does not say. Show it. |

The decision is one of five:

| Decision | Effect |
|---|---|
| `ApprovalDecision.Approve()` | Runs this call. |
| `ApprovalDecision.ApproveForSession()` | Runs this call, and the tool runs without asking for the rest of this session. This is kept in memory only: when the session is opened again with `OpenSessionAsync`, the tool asks again. |
| `ApprovalDecision.ApproveAlways()` | Runs this call, and the tool runs without asking on this device, in this and later sessions, until the approvals are reset. |
| `ApprovalDecision.ApproveFor(duration)` | Runs this call, and asks the server for a grant: the tool runs without asking on this device for that long (see [Grants](#grants-for-a-time-and-working-alongside)). Only for a duration in `GrantDurations`. If the server does not confirm the grant, this call alone runs and the turn gets a `grant_failed` notice. |
| `ApprovalDecision.Deny(reason)` | Does not run the call. The model is told the user declined, with your reason if you gave one. |

"For this chat", "always" and "for a time" are taken only where the request offers them (`CanAllowForSession`, `CanAllowAlways`, `GrantDurations`). Elsewhere they approve this call only, so a handler cannot widen what the request allows. They are never offered for a `Destructive` or `Unknown` tool, nor for a call an administrator's rule covers: those ask every time. The agent's author may also allow fewer of them: the profile's `approvals.standing` lists which (`chat`, `time`, `grant`, `always`), and the agent offers only those.

Without a handler, every call that needs approval is declined. A declined call makes the turn's outcome `ToolDenied`; the model may still complete its answer.

The `cancellationToken` passed to your handler is cancelled when the turn is stopped. For host and MCP tools the SDK sets no time limit: the call waits until the user decides.

The stream reports both sides: `ApprovalRequested` just before your handler is called, and `ApprovalDecided` after it returns. A call that an earlier approval lets through raises neither. In a UI that shows approvals inside the conversation, use the `ApprovalBroker` of the [chat panel](chat-panel.md).

### What decides a call

The agent's contract on the server may set each tool's approval (`AgentTool.Approval`), on top of the tool mode: `always` asks every time, like an administrator's rule (1 below); `ask` makes the call need approval even where the mode would run it; `never` runs a `Read` or `Write` tool the mode would ask about, unless a rule asks. None of them shows a tool the mode hides.

For a call that needs approval, the first of these that applies decides:

1. An administrator's "Ask first" rule covers the call: the user is asked, and may allow this call only.
2. The conversation runs in Bypass, the gateway says the conversation's Bypass grant covers the call, and the agent's copy holds that grant: the call runs without asking, destructive and unclassified ones too (see [Bypass](#bypass-mode)).
3. The tool is `Destructive` or `Unknown`: the user is asked, and may allow this call only.
4. The gateway says a grant covers the call, and the agent's copy holds that grant, running, for this tool or class: the call runs without asking (see [Grants](#grants-for-a-time-and-working-alongside)).
5. The user allowed the tool always, with the class it has now: the call runs without asking.
6. The user allowed the tool for this chat: the call runs without asking.
7. The user is asked, and may allow this call, allow for this chat, allow always, allow for a time, or deny.

A tool the mode hides is refused before any of this, also in bypass mode.

For a tool the gateway runs, the gateway waits while your app asks, and the agent answers it with what the approval covers: `once`, `chat` or `always`. The gateway takes `once` only for a `Destructive` or `Unknown` tool, or one an administrator's rule asks about, which is what the agent sends for those. Bypass mode never answers the gateway: only the server can skip its own question.

### Rules from the server

An administrator can set an "Ask first" rule on the server for a tool. The gateway then marks the call before your app runs it, and the agent asks the user even where the mode would run the call without asking, even when the user allowed the tool for the chat or always, and even in bypass mode. `RequiredByRule` is set, and `Reason` holds the administrator's message. A call the server refuses outright is not run and not asked about: the turn gets a `tool_denied` notice.

The gateway may also hold a stricter class for a tool than your app has. The agent then uses the stricter one.

### Open-world tools

A tool that reaches outside the organisation (`OpenWorld`, such as a web search) is open-world. What it is given leaves
the organisation, and text the model read (a page, a mail, a file) can try to put private data into it (OWASP Top 10
for LLM Applications 2025, LLM02 and LLM06). So:

- In `ReadOnly` and `Ask`, a call to an open-world tool asks, a read one too. `Auto` and `Bypass` run it as its class
  says.
- The person may allow it for the rest of the conversation (*Allow for this chat*). Below `Auto`, *Allow always* and a
  time grant are not offered for it, and an "always" saved before does not answer it: the next conversation asks again.
  A server grant that covers the call still covers it.
- The mark is the stricter of the tool's own (`AgentTool.OpenWorld`, a local MCP tool's `openWorldHint: true`) and the
  gateway's: a Fadenstack gateway from 0.7.1 says `openWorld` when an administrator's class rule marks a tool, and for
  the SDK's `web_search`.
- An agent's contract that sets `approval: never` for a read or write tool still runs it unasked, as for any tool the
  mode would ask about.

### Allow always

"Allow always" is kept per device, per agent, per tool and per class, in an approval store. When the tool's class changes, for example when an administrator makes it destructive, the stored approval no longer matches and the user is asked again.

By default the approvals are kept in files in the signed-in user's own folder (`users\<user>\approvals` in the data folder, see [Sessions on the device](sessions.md#where-they-are-stored)), one per agent, protected like the sessions. To keep them elsewhere, implement `IApprovalStore` (`ListAsync`, `AddAsync`, `RemoveAsync`) and pass it to `UseApprovalStore`. `MemoryApprovalStore` keeps them in memory only, which suits tests. Each `StoredApproval` holds the tool's name, its class and when it was allowed.

To forget approvals:

```csharp
await agent.ResetApprovalsAsync();             // everything the user allowed always for this agent
await agent.ResetApprovalsAsync("add_comment"); // one tool
session.ResetApprovals();                       // what the user allowed for this chat
```

`session.ApprovedForSession` lists the tools the user allowed for the chat.

### Bypass mode

Bypass is a level above Auto (`ToolMode.Bypass`), for one conversation and a time the person picks. The server holds it as a grant of kind `bypass`. The person switches it on, never the model, a tool or another agent: `session.StartBypassAsync()` asks them to confirm, they sign in again in the browser, and the server answers with the grant. `session.StopBypassAsync()` ends it. It is offered only where the organisation allows it and no ceiling is lower: `agent.BypassOffered`. An administrator's "Ask first" rule and a tool marked `always` still ask.

For development and trusted automation, the app can switch Bypass on in its own code instead:

```csharp
builder.UsePermissionMode(PermissionMode.Bypass);
```

`agent.PermissionMode` tells which mode the agent runs in; the default is `PermissionMode.Ask`. [Server-held grants and Bypass](server-grants.md#bypass) describes the whole flow: the confirmation, the second sign-in, what is reported, and what happens when Bypass ends.

### What is recorded

Every call that needed approval leaves an `approval.decision` entry in the transcript, also when an earlier approval or bypass mode let it through:

| Field | Meaning |
|---|---|
| `call_id`, `name`, `source` | The call. |
| `approved`, `timed_out` | The outcome. |
| `scope` | What let the call through: `once`, `chat`, `always`, `bypass` or `grant`. Missing when it was declined. |
| `grant_id`, `grant_minutes` | With `grant`: the grant, and the time the user picked when they made it with this call. |
| `asked` | Whether the user was asked. |
| `rule` | Whether an administrator's rule required approval. |
| `for_session` | True when the user picked "Allow for this chat" for this call, for older readers. |
| `wait_ms` | How long the user took; 0 when nobody was asked. |

With event reporting on, the server gets an `approval.decided` event for the same calls: the tool's name, the decision, how long the user took (not sent when nobody was asked), the `scope`, and with `grant` the `grant_id`.

### Approvals for gateway tools

The server's own tools run on the gateway, and a tool rule or the tool mode can make one of them wait for the user. The agent then decides as above, with `Source` set to `gateway` and the class the gateway sends, asks the same handler where it has to, and sends the answer to the gateway, also when an earlier approval lets the call through. The gateway sets the time limit: when the handler does not answer in time, the approval expires, the call is not run, and `ApprovalDecided` reports it declined.

### Grants: for a time, and working alongside

A grant lets the agent run, for a while, what would ask. Unlike "Allow always", the server holds it: the gateway checks every call against it, counts it, and ends it when the time is up or when the user or an administrator revokes it. There are two kinds: a grant **for a time** for one tool, chosen on the approval card with `ApprovalDecision.ApproveFor(duration)`, and a grant for **a stretch of work** in named places, which the app offers with `session.OfferWorkAsync(places)`. `agent.Grants.Available` says whether the server holds grants. [Server-held grants and Bypass](server-grants.md#grants-for-a-time-and-a-stretch-of-work) describes both, how a place is matched, and `agent.Grants`.

### Keeping a grant from running unattended

A grant that outlives the person's attention is a risk: the app is closed, the screen is locked, and the agent still runs under it. `GrantLifecycle` keeps the rules for that, so every app gets the same ones; the app feeds it what only the app knows (`AppStartedAsync`, `AppEndingAsync`, `SetScreenLockAsync`). See [The grant lifecycle](server-grants.md#the-grant-lifecycle).

## Where tools run

| Source | Runs | Page |
|---|---|---|
| `host` | In your application, on the device. | This page |
| `mcp` | In a local MCP server on the device. | [Local MCP servers](local-mcp.md) |
| `gateway` | On the server. You see the calls as `ToolCallStarted` and `ToolCallCompleted` events. | [Tools and approvals](../../concepts/tools.md) |

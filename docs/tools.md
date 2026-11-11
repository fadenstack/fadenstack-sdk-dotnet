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
- `OpenWorld = true` marks a tool that reaches outside the organisation, such as a web search. It is recorded, and its approval request says so (`ApprovalRequest.OpenWorld`): the chat panel's card says that what it is given leaves the organisation. No tool mode acts on it yet.
- Tool names must be unique. `Build()` throws when two tools share a name.

`AddTool(function, toolClass)` adds a Microsoft.Extensions.AI `AIFunction` made elsewhere, for example with `AIFunctionFactory.Create`. Without a class it is `Unknown`.

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

`ToolModes.Decide(mode, toolClass)` returns what a mode does with a class (`Run`, `Ask` or `Hidden`), if you want to show it in your UI. `ToolModes.ToWire` and `ToolModes.ParseMode` convert modes to and from their wire names (`off`, `read_only`, `ask`, `auto`).

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

### Allow always

"Allow always" is kept per device, per agent, per tool and per class, in an approval store. When the tool's class changes, for example when an administrator makes it destructive, the stored approval no longer matches and the user is asked again.

By default the approvals are kept in encrypted files under the user's data folder (`%LOCALAPPDATA%\Fadenstack\approvals\`), one per agent, protected like the sessions. To keep them elsewhere, implement `IApprovalStore` (`ListAsync`, `AddAsync`, `RemoveAsync`) and pass it to `UseApprovalStore`. `MemoryApprovalStore` keeps them in memory only, which suits tests. Each `StoredApproval` holds the tool's name, its class and when it was allowed.

To forget approvals:

```csharp
await agent.ResetApprovalsAsync();             // everything the user allowed always for this agent
await agent.ResetApprovalsAsync("add_comment"); // one tool
session.ResetApprovals();                       // what the user allowed for this chat
```

`session.ApprovedForSession` lists the tools the user allowed for the chat.

### Bypass mode

Bypass is a fifth level above Auto (`ToolMode.Bypass`), for one conversation and a time the person picks. The server holds it as a grant of kind `bypass`: the gateway checks every call against it and records each call it lets through. It is offered only where the organisation allows it, the agent's version goes up to it, and no ceiling is lower: `agent.BypassOffered`. An administrator's "Ask first" rule and a tool marked `always` still ask, the tool mode still hides what it hides, and no conversation starts in Bypass. A conversation opened again comes back in Bypass only while its Bypass grant still runs (with its marker); otherwise in the mode it had before.

The person switches it on, never the model, a tool or another agent:

```csharp
var grant = await session.StartBypassAsync();   // the confirmation first; null when declined or not offered
await session.StopBypassAsync();                // ends it; the chat goes back to the mode it had before
```

`StartBypassAsync` shows the confirmation through the app's grant handler (`GrantOffer.Bypass`): what no longer asks, what still asks, what the server records of each call (`GrantOffer.Evidence`: `decisions`, `calls` or `results`), the times up to the longest the server allows (30 minutes first, or the longest offered when that is shorter), and an optional reason (`GrantAnswer.Reason`, at most 500 characters, cut at a character's end by `GrantAnswer.Cut`), which administrators see. After the person's Allow, the person signs in again in the browser (the second sign-in, sdk/41): the agent starts it at the server, the app's step-up handler (`IStepUpHandler`, which the chat panel's `ApprovalBroker` is) opens the page in the system browser (`SystemBrowser.Open`; set `ApprovalBroker.OpenBrowser` to open it otherwise) and shows that the app waits, and once the person approved there the agent sends the proof with the grant request. One sign-in serves every Bypass switched on with that agent on that device for 5 minutes; the proof stays in memory only. A denial, no answer within 10 minutes, or a browser that cannot be opened throws `BypassSignInException` (its `Stage` says which), and nothing is switched on. Bypass holds only once the server answered with the grant: a refusal or no connection is thrown, and the chat stays as it was. A Bypass grant is never extended: more time is a new Bypass. Starting another chat or opening another leaves it running in its own conversation. Picking another mode, `StopBypassAsync`, or the end of the time ends it; so do an administrator, the organisation turning Bypass off, and a narrower limit. `agent.Grants.WhyEndedAsync(id)` says why one ended (`person`, `expired`, `admin`, `kill_switch`, `limit`). After each turn in Bypass the agent reports the calls it ran without asking, and the server flags any difference with its own records. In the first request after a Bypass ended the model is told that tools ask again and that only the person can switch it on; no tool can. At the app's end, `agent.Grants.PauseBypassAsync()` pauses running Bypass grants (their time runs on), and at the next start `agent.Grants.ResumeBypassAsync()` resumes those paused for less than 30 minutes -- without a card or a sign-in; the server checks that Bypass is still allowed -- and ends the others, returning why. The chat panel does all of this (see [the chat panel](chat-panel.md#what-the-panel-shows)).

For development and trusted automation, the app can switch Bypass on in its own code instead:

```csharp
builder.UsePermissionMode(PermissionMode.Bypass);
```

Before each turn of a session in Ask or above, the agent then makes the conversation's Bypass grant itself, without a card, for the longest time the server allows, where the server offers it; elsewhere every call that would ask still asks. It needs the second sign-in too: give the agent a step-up handler (`builder.UseStepUpHandler(handler)`, or an approval handler that also implements `IStepUpHandler`) that opens the page for a person. Without one, or when nobody approves, there is no Bypass: the session runs in Auto, and the agent writes why to `System.Diagnostics.Trace`. Unattended automation uses an administrator's rules, not Bypass. It is a setting in the app's code, never a choice for the user. The agent writes a warning to `System.Diagnostics.Trace` once when it starts in bypass mode where the server does not offer it, and the transcript and the server's record say what let each call through. A task another agent hands over never runs in Bypass, whatever the app or its host set. `agent.PermissionMode` tells which mode the agent runs in; the default is `PermissionMode.Ask`.

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

A grant lets the agent run, for a while, what would ask. Unlike "Allow always", the server holds it: the gateway checks
every call against it, counts it, and ends it when the time is up, when the user or an administrator revokes it, or
when the agent gets a new version. The device keeps a copy, `agent.Grants`, as the second line: a call runs under a
grant only when the gateway says the grant covers it and the copy holds that grant, running. A copy that does not know
the grant reads the server's list once; if it still lacks it, the user is asked. Grants need a server that holds them:
`agent.Grants.Available` says so (the profile's feature `approval_scopes.grant`).

There are two kinds:

- **For a time:** one tool, chosen on the approval card with `ApprovalDecision.ApproveFor(duration)`.
- **A stretch of work:** `Read` and `Write` calls in named places, in this chat. The app offers it with the places it
  knows, and the user may take places out and picks the time:

```csharp
var grant = await session.OfferWorkAsync(new[]
{
    GrantPlace.Document(@"C:\Docs\report.docx"),
    GrantPlace.Site("https://purchase.example.de"),
}, reason: "To fill in the order form.");
```

The card is drawn by your grant handler (`IGrantHandler`); the chat panel's `ApprovalBroker` is one. It gets a
`GrantOffer` (places, classes, durations, the time to start at, your reason) and returns a `GrantAnswer` (the places
kept, the time, and the card's text, whose SHA-256 goes to the server as evidence). `OfferWorkAsync` returns the grant
the server confirmed, or null when the user said "Not now" or the agent does not offer it. A call covers a place only
when its tool names it: in the agent's contract on the server, `target_argument` points at the argument (a JSON
pointer such as `/path`) and `target_type` says what it is (`document`, `folder`, `site`, `app` or `desk`).

The gateway matches a place by its text. A path on the device may lead elsewhere through a symbolic link, a junction
or a mount point, so before a covered call runs, the device resolves the call's path and the grant's paths as the
operating system does (on Windows `GetFinalPathNameByHandleW`, on macOS and Linux `realpath`). The call runs under the
grant only when the real path lies in a granted place; otherwise the user is asked, and `ApprovalRequest.LeadsOutside`
holds where it really leads, which the chat panel's card shows. A path that cannot be resolved also asks. A hard
link, and a link changed between this check and the tool's own opening of the path, are not caught.

A grant never covers a `Destructive` or `Unknown` call, a tool the agent's contract marks `always`, or a call an
administrator's rule asks about: those still ask.

To extend a running grant (more time, another place), show the card again with only the difference:

```csharp
var longer = await session.OfferExtensionAsync(grant, TimeSpan.FromMinutes(30), new[] { GrantPlace.Document(@"C:\Docs\budget.xlsx") });
```

The server keeps a grant within the agent's longest grant (two hours unless the agent's author set less), counted from
when it was made: `Narrowed` contains `minutes` when it cut the time, and past that it refuses (`grant_at_longest`), so
a new grant needs the full card.

`agent.Grants` also holds what you need for a marker and for Settings:

| Member | Use |
|---|---|
| `Live` | The grants that may still cover a call, the one ending first first. |
| `Remaining(grant)` | Its time left, by the server's clock (`ServerNow`); count down this, not the device's clock. |
| `RevokeAsync(id)`, `RevokeAllAsync()` | End a grant, or all of them: the marker's Stop. A call already running finishes. |
| `PauseAsync(id)`, `ResumeAsync(id)` | While the user is away (the screen locked); the time runs on. |
| `ExtendAsync(id, more, places)` | Extend without a card, where your own UI already asked (the marker's "Another 30 min"). |
| `Changed` | Raised when the copy changed: made, extended, ended, or a new list from the server at the start of a stream. |

`agent.ListApprovalsAsync()` lists what the user allowed always, for the same Settings list.

## Where tools run

| Source | Runs | Page |
|---|---|---|
| `host` | In your application, on the device. | This page |
| `mcp` | In a local MCP server on the device. | [Local MCP servers](local-mcp.md) |
| `gateway` | On the server. You see the calls as `ToolCallStarted` and `ToolCallCompleted` events. | [Tools and approvals](../../concepts/tools.md) |

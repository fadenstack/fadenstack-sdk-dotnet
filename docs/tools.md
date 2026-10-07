# Host tools

This page covers how you declare host tools, how the agent calls them, and how the session's tool mode and the user's approvals decide whether a call runs.

## Declare tools

A host tool is a method of your application that the model may call. Mark it with `[AgentTool]`, say what it does with `[Description]`, and add the object to the builder:

```csharp
using System.ComponentModel;
using Faden.Agents;
using Faden.Tools;

public sealed class PageTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open schematic.")]
    public string ReadPage([Description("The page number, from 1.")] int page) => Schematic.Read(page);

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
    public string AddComment(int page, string text) => Schematic.Comment(page, text);

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page) => Schematic.Delete(page);
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
- `OpenWorld = true` marks a tool that reaches outside the organisation. It is recorded and shown; no tool mode acts on it yet.
- Tool names must be unique. `Build()` throws when two tools share a name.

`AddTool(function, toolClass)` adds a Microsoft.Extensions.AI `AIFunction` made elsewhere, for example with `AIFunctionFactory.Create`. Without a class it is `Unknown`.

## Parameters and return values

Tools are Microsoft.Extensions.AI functions, so the usual rules of `AIFunctionFactory` apply:

- The parameters become the JSON schema the model sees. `[Description]` on a parameter describes it, and a default value makes it optional.
- A `CancellationToken` parameter is not shown to the model. It receives the turn's token, which is cancelled when the turn is stopped.
- Asynchronous methods (`Task<T>`, `ValueTask<T>`) are awaited.
- A returned string goes to the model as it is. Any other value goes as JSON.

```csharp
[AgentTool(ToolClass.Read), Description("Finds devices whose name contains the text.")]
public async Task<IReadOnlyList<string>> FindDevicesAsync(string text, CancellationToken cancellationToken)
{
    return await Project.FindDevicesAsync(text, cancellationToken);
}
```

When a tool throws, the model gets `Error:` followed by the exception's message, the call's outcome is `error`, and the turn goes on. Keep anything the model should not see out of exception messages.

When the model sends arguments that are not valid JSON, the tool is not run and the model gets an error.

## Tool classes

Every tool has a class that says what it does:

| Class | Meaning |
|---|---|
| `Read` | No side effects. |
| `Write` | Changes something that can be changed back. |
| `Destructive` | Deletes, sends, pays, or cannot be undone. |
| `Unknown` | Nobody said what it does. It always asks, and `ReadOnly` hides it. |

The class you give is what you say the tool does. The agent's host tool contract on the server can make a class stricter, never looser, and the agent runs each tool under the stricter of the two. A tool you gave no class runs under the contract's class. When the contract is not empty, host tools it does not list are not offered to the model at all. After the agent has started, `agent.Tools` lists the tools it offers with the classes they run under.

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
- `session.EffectiveToolMode` is the mode turns run in. With the Enterprise edition (planned), an administrator may in future narrow the modes a user can pick, and `EffectiveToolMode` then holds the narrowed mode.
- A session continued with `OpenSessionAsync` keeps the mode it was last used in.

A hidden tool is not offered to the model. If the model calls it anyway, the call is refused, the turn gets a `tool_denied` notice, and the outcome is `ToolDenied`.

Each request carries the session's mode to the gateway, which applies the same rules to the server's own tools.

`ToolModes.Decide(mode, toolClass)` returns what a mode does with a class (`Run`, `Ask` or `Hidden`), if you want to show it in your UI. `ToolModes.ToWire` and `ToolModes.ParseMode` convert modes to and from their wire names (`off`, `read_only`, `ask`, `auto`).

## Approvals

When the mode says a call asks, the agent asks your approval handler. Pass one to the builder, either as an `IApprovalHandler` or as a function:

```csharp
builder.UseApprovals(async (request, cancellationToken) =>
{
    Console.Write($"Run {request.ToolName} ({request.ToolClass})? {request.Reason} [y/s/N] ");
    var answer = await Task.Run(Console.ReadLine, cancellationToken);
    return answer?.Trim() switch
    {
        "y" => ApprovalDecision.Approve(),
        "s" => ApprovalDecision.ApproveForSession(),
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
| `Reason` | Why it asks: the tool mode, or the administrator's message for a gateway tool rule. |
| `Arguments` | The call's arguments. Empty for gateway tools, whose arguments stay on the server. |
| `CallId` | The call's id. |

The decision is one of three:

| Decision | Effect |
|---|---|
| `ApprovalDecision.Approve()` | Runs this call. |
| `ApprovalDecision.ApproveForSession()` | Runs this call, and the tool runs without asking for the rest of the session. This is kept in the session, so it still holds after `OpenSessionAsync`. |
| `ApprovalDecision.Deny(reason)` | Does not run the call. The model is told the user declined, with your reason if you gave one. |

Without a handler, every call that needs approval is declined. A declined call makes the turn's outcome `ToolDenied`; the model may still complete its answer.

The `cancellationToken` passed to your handler is cancelled when the turn is stopped. For host and MCP tools the SDK sets no time limit: the call waits until the user decides.

The stream reports both sides: `ApprovalRequested` just before your handler is called, and `ApprovalDecided` after it returns. In a UI that shows approvals inside the conversation, use the `ApprovalBroker` of the [chat panel](chat-panel.md).

### Approvals for gateway tools

The server's own tools run on the gateway, and a tool rule on the server can make one of them wait for the user. The agent then asks the same handler, with `Source` set to `gateway`, and sends the answer to the gateway. The gateway sets the time limit: when the handler does not answer in time, the approval expires, the call is not run, and `ApprovalDecided` reports it declined.

## Where tools run

| Source | Runs | Page |
|---|---|---|
| `host` | In your application, on the device. | This page |
| `mcp` | In a local MCP server on the device. | [Local MCP servers](local-mcp.md) |
| `gateway` | On the server. You see the calls as `ToolCallStarted` and `ToolCallCompleted` events. | [Tools and approvals](../../concepts/tools.md) |

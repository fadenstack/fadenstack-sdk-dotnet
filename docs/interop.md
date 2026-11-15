# Interop

This page covers how the agent plugs into the app and its tooling: hooks that watch (and can tighten) model calls and tool calls, and opt-in OpenTelemetry.

## Hooks

Hooks are the app's own code that runs before and after each model call and each tool call. Use them to audit, to keep your own log, to annotate a call, or to make a tool call stricter than the policy made it. They are documented and stable; they build on `AddChatStage` and `AddToolStage`.

Derive from `AgentHooks` (every method does nothing) and override what you need, then add the hooks to the builder:

```csharp
using Fadenstack.Agents;
using Fadenstack.Tools;

public sealed class AuditHooks : AgentHooks
{
    public override Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext call, CancellationToken cancellationToken)
    {
        // Our own tool "export_report" is always shown to the person, even where the policy would run it.
        if (call.Source == "host" && call.ToolName == "export_report")
        {
            return Task.FromResult(ToolHookVerdict.Ask("Exports leave the building.").WithNote("audit", "export asked"));
        }
        return Task.FromResult(ToolHookVerdict.Continue);
    }

    public override Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{result.ToolName} {result.Outcome} in {result.Duration.TotalMilliseconds:0} ms");
        return Task.CompletedTask;
    }
}

var agent = FadenstackAgent.CreateBuilder()
    /* ... */
    .AddHooks(new AuditHooks(), order: 0)
    .Build();
```

### The four points

| Point | Method | What the hook sees |
|---|---|---|
| Before a model call | `BeforeModelCallAsync(ModelCallContext)` | `AgentName`, `SessionId`, `TurnId`, `Round`, `Model`, `MessageCount`, `ToolCount`. |
| After a model call | `AfterModelCallAsync(ModelCallResult)` | The same ids, `Outcome` (`Ok`, `Error`, `Denied`, `Cancelled`), `Duration`, `InputTokens` and `OutputTokens` when the model said, `ToolCallCount`. |
| Before a tool call | `BeforeToolCallAsync(ToolCallContext)` | `ToolName`, `Source` (`host`, `mcp`, `gateway`, `skills`, ...), `Server`, `Class`, `CallId`, `Arguments` (a copy), and `Decision`: what the policy decided (`Runs`, `Asks`, `Refused`). It returns a `ToolHookVerdict`. |
| After a tool call | `AfterToolCallAsync(ToolCallResult)` | The same, plus `Outcome` (`Ok`, `Error`, `Denied`, `Cancelled`), `Duration` (without the time the person took to answer a card) and `ResultText` for the app's own (`host`) tools only. |

A model hook sees no prompt and no answer text. If your app needs them, say so once with `builder.UseHookContent()`: `ModelCallContext.Messages` and `ModelCallResult.Text` are then filled. They are `null` otherwise.

A tool call that the policy refused (the tool mode hides it, the gateway or the server's check denies it) is still shown to the hooks with `Decision == Refused`, so they can audit it, and their answer is dropped. A tool the model made up, a call with invalid arguments and a person's own host command are not hooked.

### The rule that keeps the floor

`BeforeToolCallAsync` runs **after** the policy decided (OWASP ASVS 5.0 V8: authorization is enforced in a trusted layer, and app code cannot widen it). Its answer, a `ToolHookVerdict`, can only be:

- `ToolHookVerdict.Continue`: go on as the policy decided.
- `ToolHookVerdict.Ask(reason)`: ask the person. A call the policy would run unasked then shows the approval card, once only: no earlier "allow always", no grant answers it. A call the policy already asks about is asked as before.
- `ToolHookVerdict.Deny(reason)`: refuse. The reason is cut to 500 characters and reaches the model as a declined call (`'name' was declined by the app (reason).`). The turn ends as `ToolDenied`.

There is no answer that approves a call the policy would ask about or refuse. The types make that impossible: `ToolHookVerdict` has no public constructor and no "approve", everything a hook is given (`ToolCallContext`, `ToolCallResult`) has get-only properties, so a hook cannot lower or change a tool's class or the policy's decision. A destructive or unclassified call still shows its card after any hook; a hook that denies stops the call, and a hook that throws counts as "go on". The tests in `HookTests` pin each of these.

### Notes

A verdict can carry short notes: `ToolHookVerdict.Continue.WithNote("ticket", "T-42")`. They are kept in the `notes` field of the call's `tool.execution_complete` entry in the transcript, on the device, and never sent to the server. A call keeps at most 8 notes (all hooks together); a key is cut to 64 characters and a value to 200.

### Order

Before-hooks run in ascending `order`, then in the order they were added. After-hooks run in the reverse order. The first hook that denies ends the line: later before-hooks are not asked (they still get their after-call).

### A hook that throws

A hook that throws never breaks the run. The SDK logs warning 2060 with the hook's type name and the exception's type, never the exception's message or any content (ASVS 5.0 V16), and treats the hook as having said "go on as the policy decided".

### TypeScript

The TypeScript SDK has the same hooks with the same semantics: `builder.addHooks(hooks, order)` with an `AgentHooks` object of optional functions (`beforeModelCall`, `afterModelCall`, `beforeToolCall`, `afterToolCall`), and `toolHook.continue()`, `toolHook.ask(reason)` and `toolHook.deny(reason)` for the answer. Its context types are named `ModelCallHookContext`, `ToolCallHookContext` and so on.

## OpenTelemetry

OpenTelemetry is **opt-in** and off by default: `builder.UseOpenTelemetry()`.

```csharp
var agent = FadenstackAgent.CreateBuilder()
    /* ... */
    .UseOpenTelemetry()                       // or: o => o.EnableSensitiveData = true
    .Build();

// The app wires the exporter; the SDK bundles none.
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(FadenstackTelemetry.AgentsSource)                       // "Fadenstack.Agents": invoke_agent, execute_tool
    .AddSource(AgentOpenTelemetryOptions.DefaultModelSourceName)       // "Fadenstack.Agents.Models": model calls
    .AddOtlpExporter());
```

What it adds:

- **Model calls** go through Microsoft.Extensions.AI's `OpenTelemetryChatClient` (in the stable `Microsoft.Extensions.AI` package), under the source name `Fadenstack.Agents.Models` (change it with `o.ModelSourceName`). It sits just above the transport, so it sees what leaves the process after masking and marking.
- **Runs and tool calls** are the spans the SDK always has when a listener is attached to `Fadenstack.Agents`: `invoke_agent <agent>` (a turn) and `execute_tool <tool>` (each tool call), with the GenAI attributes `gen_ai.operation.name`, `gen_ai.agent.name`, `gen_ai.conversation.id`, `gen_ai.tool.name` and `gen_ai.tool.call.id`. `UseOpenTelemetry` adds `gen_ai.tool.type` and `fadenstack.tool.policy_decision` (`runs`, `asks`, `refused`) to the tool span; it does so through a hook (it is itself a hook, added first).
- **No prompt or answer content by default.** Spans carry names, ids, counts and outcomes. `EnableSensitiveData = true` adds the messages and the answer to the model spans (Microsoft.Extensions.AI), the arguments (`gen_ai.tool.call.arguments`) to the tool spans, and the result (`gen_ai.tool.call.result`) for the app's own (`host`) tools. Arguments and results are cut at 4000 characters. Prompts and answers are the person's data: switch this on for development, not for what leaves the machine.
- **No exporter is bundled.** In standalone, nothing goes anywhere unless the app wires an exporter. The SDK takes no dependency on the OpenTelemetry SDK.

The GenAI semantic conventions are in **Development** status. Span and attribute names may change between versions of the conventions, and so between versions of this SDK.

The SDK's counters and histograms (`FadenstackTelemetry.AgentsMeter`) are separate and unchanged. Works on .NET 8 and later, .NET Framework 4.8 and .NET Standard 2.0.

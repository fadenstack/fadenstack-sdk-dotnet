# Interop

How a Fadenstack agent talks to the rest of the ecosystem: AG-UI-shaped events for AG-UI clients, hooks that watch (and can tighten) model calls and tool calls, and opt-in OpenTelemetry.

## AG-UI events

[AG-UI](https://docs.ag-ui.com) is the open protocol agent front ends speak. The SDK's own view models keep using the native `AgentEvent`s; `AgUiEvents.From` is an adapter over that stream, so a third-party AG-UI client (CopilotKit, assistant-ui with its AG-UI adapter) can render a Fadenstack agent. The event types are our own small records in `Fadenstack.Agents.AgUi` and `AgUiJson` writes AG-UI's JSON: the SDK takes no dependency on an AG-UI package.

```csharp
using Fadenstack.Agents.AgUi;

var session = await agent.StartSessionAsync();
await foreach (var e in AgUiEvents.From(session.StreamAsync("Delete page 8"), new AgUiOptions { ThreadId = session.Id }))
{
    await response.WriteAsync("data: " + AgUiJson.Serialize(e) + "\n\n"); // one AG-UI event per SSE message
}
```

`AgUiJson.Serialize(e)` is the event's JSON: compact, `type` first (`RUN_STARTED`, `TEXT_MESSAGE_CONTENT`, ...), camelCase keys, `metadata` last. A run that stops on an approval looks like this:

```json
{"type":"RUN_STARTED","timestamp":1700000000000,"threadId":"thread-1","runId":"turn-1","metadata":{"toolMode":"ask"}}
{"type":"TOOL_CALL_START","timestamp":1700000000001,"toolCallId":"call-2","toolCallName":"delete_page","metadata":{"toolClass":"destructive","source":"host"}}
{"type":"TOOL_CALL_ARGS","timestamp":1700000000002,"toolCallId":"call-2","delta":"{\"page\":8}"}
{"type":"TOOL_CALL_END","timestamp":1700000000003,"toolCallId":"call-2"}
{"type":"RUN_FINISHED","timestamp":1700000000004,"threadId":"thread-1","runId":"turn-1","outcome":{"type":"interrupt","interrupts":[{"id":"call-2","reason":"approval_required","message":"Deleting cannot be undone.","toolCallId":"call-2","metadata":{"toolName":"delete_page","toolClass":"destructive","source":"host"}}]}}
```

### The mapping

| Fadenstack event | AG-UI event |
|---|---|
| a turn starts | `RUN_STARTED` (`threadId`, `runId`; `metadata.toolMode`) |
| a turn completes | `RUN_FINISHED`, outcome `success`; `result.outcome` says how the turn ended (`completed`, `prompt_blocked`, `response_blocked`, `tool_denied`, `rate_limited`); `usage` has the token counts when the model reported them |
| the stream fails (a stopped turn) | `RUN_ERROR` (`message`); a cancellation is thrown on instead |
| a text delta | `TEXT_MESSAGE_START` (once), `TEXT_MESSAGE_CONTENT` per delta, `TEXT_MESSAGE_END` when anything else comes |
| a reasoning delta | `REASONING_START`, `REASONING_MESSAGE_START`, `REASONING_MESSAGE_CONTENT` per delta, `REASONING_MESSAGE_END`, `REASONING_END` |
| a tool call starts | `TOOL_CALL_START` (`metadata.toolClass`, `metadata.source`), `TOOL_CALL_ARGS` (the arguments as one JSON text), `TOOL_CALL_END` |
| a tool call ends | `TOOL_CALL_RESULT` (`content` is the result text; `metadata.outcome` is `ok`, `error`, `denied` or `cancelled`, `metadata.durationMs`) |
| a call waits for the user (the approval card) | `RUN_FINISHED` with outcome `interrupt`: one interrupt, `id` and `toolCallId` are the call's id, `reason` is `approval_required`, `message` is the reason the card shows |
| the user answers | the next event opens a new run on the same thread: `RUN_STARTED` with `runId` `{turnId}.2` and `metadata.resumes` (`interruptId`, `approved`); a denied call ends with a `TOOL_CALL_RESULT` whose `metadata.outcome` is `denied` |
| a governance notice (a refusal, a limit) | `CUSTOM` `faden.notice` |
| citations, a PII notice, a left-out attachment, the provenance mark (gateway words) | `CUSTOM` `faden.citations`, `faden.pii`, `faden.attachment_left_out`, `faden.provenance`, with the gateway's value |

Details that matter to a client:

- **Interrupt.** AG-UI 1.0 models a pause as a `RUN_FINISHED` event with an `interrupt` outcome, and the answer as a new run. The adapter follows it, so an off-the-shelf client shows the approval as an interrupt, and the events after the answer belong to the run `{turnId}.2`. The native stream itself keeps going in one turn; only the AG-UI view splits it.
- **Session state.** The SDK has no session state to send, so `STATE_SNAPSHOT` and `STATE_DELTA` are not produced.
- **Not sent.** The gateway round (the history the SDK keeps for the next request), the pictures a tool returns, and any header or secret. Tool arguments and results go as the native events carry them.
- **Ids.** `messageId` is `{turnId}-m1`, `{turnId}-m2`, ... for text and reasoning, and `{turnId}-result-{callId}` for a tool result. `threadId` is the one you pass, else the first turn's id. `timestamp` is milliseconds since the Unix epoch.
- **Same JSON from both SDKs.** The .NET and TypeScript adapters write the same bytes for the same events; golden files in both repositories' tests (text, tool call, approval interrupt, error, notice) hold them.

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

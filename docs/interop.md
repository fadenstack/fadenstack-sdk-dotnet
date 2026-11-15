# Interop

How a Fadenstack agent talks to the rest of the ecosystem.

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

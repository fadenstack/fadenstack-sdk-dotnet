# Host commands and central policy

This page covers host tools the person starts themselves (a button, a view of a module), and the server's fresh policy check around every host tool call, whoever started it. Both go through `POST /v1/agents/{agent}/tools/check`.

The TypeScript SDK has the same two features on the same wire, so an app behaves the same in either.

## A host tool the person starts

A host command is a host tool called without a model: the person pressed "Undo", opened a record, restored a version. It still goes through the agent's policy, the approval cards and the transcript, so it is as governed as a call the model asks for.

```csharp
var result = await session.InvokeToolAsync(
    "restore_record",
    new Dictionary<string, object?> { ["action_id"] = "a-42" },
    new HostCommandOptions { Origin = HostToolOrigin.UserHistory, Label = "Undo file change" });

if (result.Outcome == "ok")
{
    ShowDone(result.Result);
}
```

`InvokeToolAsync` waits for the command to end. `StreamToolAsync` streams the same events a turn has (`TurnStarted`, `ToolCallStarted`, `ApprovalRequested`, `ToolCallCompleted`, `TurnCompleted`). Leaving the stream early, or cancelling the token, also cancels a pending approval.

What happens, in order:

1. The SDK checks the name (letters, digits, `_` and `-`, at most 64) and the label, and copies the arguments as strict JSON. What the person reviews is what runs, whatever the caller does with its own dictionary afterwards.
2. A command is available for the agent's host tools only. A tool the session's mode hides (Off, or Read-only for a write or destructive tool) is refused without a request.
3. The arguments are checked against the tool's schema: required ones present, none unknown where the schema forbids it, the stated types and enums.
4. The server checks the command (phase `prepare`). It answers `deny`, `ask` or `run`, the class and level it applies, and whether an administrator's rule asks.
5. Where the server or the mode says `ask`, the approval handler is asked, always for this call alone. The card offers no "for this chat", no "always" and no time grant, and a standing approval or a grant never covers it.
6. Immediately before the tool runs, the server checks it again (phase `dispatch`). The answer must carry the same profile, arguments and decision stamps, the same principal, and not be a denial. Anything else stops the command.
7. The tool runs, and the result is the command's outcome: `ok`, `error` or `denied`. A stopped command throws `OperationCanceledException`.

No model request is made at any point. A server too old to have the route answers 404, and the command ends as an error: it never runs unchecked.

!!! note "Stamps are not permits"
    The stamps are comparison values. They show that nothing changed between the two checks; they are not signed permission, a credential, or proof that the tool's effect happened. Your tool still checks its own access, scope and conflicts. Do not retry a mutation automatically after an uncertain outcome.

### The label

The label is the text the chat shows for the command. It is one line, not blank, at most 200 characters, with no control characters; otherwise `InvokeToolAsync` throws an `ArgumentException` before anything is checked or run. It is trimmed and kept in the transcript. It is never an argument, a grant or a message to the model.

Without a label the chat shows the tool's name.

### The transcript and the model

A command writes a `user.command` entry (`EntryTypes.UserCommand`) with the tool's name, its origin (`user_command` or `user_history`) and the label, never the arguments. The tool's start and end entries and the approval decision follow, as for a model's call.

The turn of a command is left out of the model's history (`session.History`). Its result would otherwise reach the model without a prompt and leave tool messages without a call. Modules cannot write `user.command` entries: it is one of the SDK's own types.

### In the chat panel

`AgentChatViewModel.InvokeToolAsync` does the same in the chat panel: the command is shown as a turn titled with its label, the approval card appears as for any call, and the composer's draft and attachments are left alone.

```csharp
var result = await chat.InvokeToolAsync(
    "restore_record",
    new Dictionary<string, object?> { ["action_id"] = "a-42" },
    new ChatCommandOptions { Origin = HostToolOrigin.UserHistory, Label = "Undo file change" });
```

- `AgentChatOptions.BeforeCommand` runs first, on the UI thread, with the command's name, origin and label (not its arguments): check that the document is in a state the tool may act on. When it throws, nothing is started, `Error` says why, and `InvokeToolAsync` returns null.
- `AgentChatOptions.AfterCommand` runs once afterwards, however the command ended.
- The chat is busy while the command runs; Stop cancels it. A second command, or a message sent meanwhile, does nothing.
- The arguments are copied before `BeforeCommand` is awaited.
- A command turn can be neither edited nor tried again as text (`TurnViewModel.IsCommand`), also after the session is opened again.

## Fresh policy for the model's host calls

A server that lists `host_tools.dispatch_check` in its features (the profile's and `GET /v1/features`) checks every host tool call the model makes, too. The agent uses the check automatically once it has seen the feature. An older server keeps the previous behaviour.

For each host call the model asks for, the SDK:

1. Reads the profile again and checks that the agent's identity and published contract are unchanged.
2. Checks the call with origin `model_tool` and the model's call id (phase `prepare`), against the pinned schema. The server's class and level can only make the call stricter than the tool declared; its denial ends the call.
3. Asks the person through the normal approval handler where the server, the mode or an administrator's rule says so. An administrator's rule asks for this call alone.
4. Right before the tool runs, reads the profile again, compares the approval limits, and checks the call again (phase `dispatch`) with the three stamps of the first answer. A denial, a different stamp or principal, changed approval limits or a changed level stops the call.

The call's result for the model is then an error, and the tool did not run. Network errors and malformed answers never let a call fall back to the old path.

The arguments that reach the tool must be the ones the person reviewed. If a stage (for example local PII unmasking) changed them, the call is stopped: the SDK does not send locally unmasked values to the check endpoint, and there is no reviewed disclosure contract for that combination yet.

### Requiring the check

```csharp
var agent = FadenstackAgent.CreateBuilder()
    .UseServer(serverUrl, credentials)
    .UseAgent("records")
    .AddTools(nativeTools)
    .RequireHostToolPolicyChecks()
    .UseApprovals(approvals)
    .Build();
```

With `RequireHostToolPolicyChecks()` the agent refuses to start against a server that does not advertise `host_tools.dispatch_check` (`StartAsync` and the first session throw a `FadenstackException`: "does not advertise support"), and never uses cached policy. `agent.HostToolPolicyChecks` says whether the check is in use.

Without it, the check is used as soon as the server has offered it. From then on, **losing it fails closed**: if a later profile no longer lists the feature, host calls fail with that error until a new agent is built. They do not go back to the old behaviour.

## What the SDK sends

`AgentResource.CheckHostToolAsync` is the wire call, for hosts that build their own flow.

| Field | Meaning |
|---|---|
| `Name`, `Arguments` | The tool and its JSON arguments. |
| `ToolMode` | The session's level. The server checks Bypass as Auto. |
| `Origin` | `UserCommand`, `UserHistory` or `ModelTool`. |
| `CallId` | Required for `ModelTool`. A model call without one is refused by the SDK before anything is sent. |
| `Phase` | `Prepare` before the person is asked, `Dispatch` right before the tool runs. |
| `SchemaSha256` | SHA-256 of the tool's parameter schema as the manifest reports it (canonical JSON: sorted keys, no spaces). |
| `Prepared` | For `Dispatch`: the `Prepare` answer, whose three stamps are sent back. |

The answer is a `HostToolDecision`. A decision the SDK does not know is `Deny`; a class it does not know is `Unknown`; a level it does not know is `Off`; a rule flag that is not a boolean counts as a rule. An answer without valid stamps or a principal throws a `FadenstackException`.

## What it is not

- It is not a way to call gateway tools or MCP tools: the check is for host tools only.
- It is not an atomic lock with the administrator's console. Normal server cache boundaries still apply between the second check and the tool's run.
- It does not replace the native checks of your own tool (access, scope, conflicts).

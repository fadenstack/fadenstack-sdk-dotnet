# Approvals

This page is for app developers: what asks the person, what no level skips, and what survives a restart. [Host tools](tools.md#approvals) has the details of the handler and the decision order, and [Server-held grants and Bypass](server-grants.md) has the grants.

It follows OWASP ASVS 5.0 chapter V8 (authorization is decided in a trusted layer, not by the model) and OWASP Top 10 for LLM Applications 2025, LLM06 (excessive agency: a person approves high-impact actions).

## The levels

The person picks a level for each conversation (`session.ToolMode`). The level decides, per tool class, whether a call runs, asks or is hidden. [Host tools](tools.md#tool-modes) has the rest.

| Level | Read | Write | Destructive and unclassified | What it is |
|---|---|---|---|---|
| Read only (`ToolMode.ReadOnly`) | runs | hidden | hidden | The model can look, not change. |
| Ask (`ToolMode.Ask`) | runs | asks | asks | The default; every session starts here. |
| Auto (`ToolMode.Auto`) | runs | runs | asks | Changes run; the risky ones still ask. |
| Bypass (`ToolMode.Bypass`) | runs | runs | runs where the gateway covers the call | The server's grant for one conversation, after a second sign-in. |

Below Auto, a call to an open-world tool asks too. A chat set to Bypass without its server grant runs in Auto (`session.EffectiveToolMode` says which).

## Standing approvals

A standing approval spares a question the level would ask. The request offers only the ones that apply (`ApprovalRequest.CanAllowForSession`, `CanAllowAlways`, `GrantDurations`).

| Kind | Covers | Held |
|---|---|---|
| Once (`ApprovalDecision.Approve()`) | This call. | Nowhere. |
| For this chat (`ApproveForSession()`) | One tool, this conversation. | The device's memory; a chat opened again asks again. |
| Always (`ApproveAlways()`) | One tool, on this device. | `FileApprovalStore`, sealed: an HMAC-SHA256 under the device's key, over the agent, the tool, its class, its source and its server. An approval that fails the seal answers nothing and the person is asked again. A new MCP server bringing a tool of the same name inherits no "always". |
| For a time (`ApproveFor(duration)`) | One tool, a chosen time. | A server grant. |
| For a stretch of work (`session.OfferWorkAsync(places)`) | Named places and classes, for a time. | A server grant. |

A server grant is read from the server for each covered call and once more as the call goes out, so a grant that ended meanwhile runs nothing. A time or work grant never covers a call on the never-skip list below, nor a destructive or unclassified one.

## The never-skip list

These ask, or are refused, at every level, Bypass included. Bypass skips questions, never rules. The list is in code as `ApprovalFloor` (`ApprovalFloor.NeverSkipped`, in this order) and is tested at Ask, Auto and Bypass.

| Id | What | Where it is decided |
|---|---|---|
| `refused` | Deny rules, and hidden or refused tools. | The call does not run and nobody is asked; the turn gets a `tool_denied` notice. |
| `ask_first_rule` | An administrator's "Ask first" rule. | The gateway marks the call `rule: true`; `ApprovalRequest.RequiredByRule` is set. The person's "for this chat" or "always" does not answer it. |
| `author_always` | The author's `approval: always` in the tool contract. | Asks every time, like a rule. (`never` and `ask` are not on the list: `ask` can be answered by the person's "for this chat" or "always".) |
| `physical` | A tool that acts in the physical world. | See the note below. |
| `open_world_tainted` | An open-world tool in a turn that read untrusted content. | See the note below. |
| `mcp_server_question` | A question an MCP server asks the person itself (elicitation). | It goes to the app's question handler (`LocalMcpModule.Ask`); the SDK never answers it. Without a handler the module does not declare elicitation, so a server cannot ask. |
| `gateway_held` | A call the gateway holds for the person (a gateway tool's approval). | The person is asked and the SDK sends their answer. Bypass never answers the gateway: only the server may skip its own question, so a Bypass grant does not cover a call the gateway holds, even if the gateway also names the grant for it (time and work grants are not affected). |

**Not marked yet.** Neither the SDK nor the gateway has a marker for `physical` and `open_world_tainted` (`ApprovalFloor.NotMarkedYet`). Until they do, a tool of that kind needs `approval: always` in its contract or an administrator's "Ask first" rule to be held to the floor.

**Destructive and unclassified calls** (`destructive_or_unclassified`, `ApprovalFloor.DestructiveOrUnclassified`; not on the list) ask at every level except Bypass. Bypass covers them only where the tenant allows it, after the second sign-in ([Bypass](server-grants.md#bypass)), and only the calls the gateway says the conversation's grant covers; the rest still ask.

## The arguments of a call the gateway holds

From server 0.7.1 the event of a call the gateway holds for the person carries the call's `arguments` text (cut at 8000 characters), `argumentsTruncated` when it was cut, and `argumentsSha256`. The SDK fills `ApprovalRequest.Arguments` from it, so the card shows them as it does for the app's own calls: when the text is a complete JSON object, its members; when it was cut or is not a JSON object, the text as one read-only value under the note "The arguments are shortened." (`ApprovalRequest.ArgumentsShortened`, `ApprovalPart.ArgumentsNote`; five languages). The card never shows more than its usual preview. An older gateway sends none, and the card shows no arguments, as before. The record of tool calls takes the hash from the same event. TypeScript: `ApprovalRequest.arguments`, `argumentsShortened`, `ApprovalPart.argumentsNote`.

## After a restart

Neither SDK stores a pending approval card. When the app closes or reloads (an Office pane reloads, Chrome stops an extension's worker), the turn ends and the person asks again. What survives a restart is "Allow always", sealed as above. A Bypass grant is the server's; `agent.Grants.PauseBypassAsync()` and `ResumeBypassAsync()` carry it across a restart ([Bypass](server-grants.md#bypass)).

## The person's reason

A person who declines can say why ("Say why"). Pass it as `ApprovalDecision.Deny(reason)`. The SDK cleans it with `ApprovalDecision.CleanReason` (control characters and invisible marks out, trimmed) and cuts it to 500 characters (`ApprovalDecision.MaxReasonChars`). The cleaned text reaches the model's next turn as part of the tool result and is kept in the transcript's `approval.decision` entry as `reason`. It is the person's own text, so it is not fenced like untrusted data.

## Delegation

When your app lets another agent run a task, its approval questions come back as `DelegationApprovalRequest` ([Delegating to agents](delegation.md)). A call the other agent asks about every time (`DelegationApprovalRequest.AsksEveryTime`: a rule, the author's `approval: always`, a destructive or unclassified tool, or an agent too old to say) is never answered from "for this task". Hide that choice on such a card.

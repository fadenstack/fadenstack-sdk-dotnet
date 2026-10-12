# Outcomes and errors

This page covers how a turn ends: the `AgentResult`, its outcome and notices, and the exceptions the SDK throws when something really fails.

## Refusals are outcomes

When the server refuses a prompt, stops an answer, denies a tool or applies a limit, the turn still ends normally. The refusal is part of the result, with a notice to show the user, and the session goes on. Only real failures, such as an unreachable server or a lost sign-in, throw.

[Outcomes](../../concepts/outcomes.md) describes the model behind this on the server.

## The result of a turn

`SendAsync` returns an `AgentResult`, and `StreamAsync` ends with a `TurnCompleted` event that carries it.

| Property | Meaning |
|---|---|
| `Outcome` | How the turn ended: a `ChatOutcome` value. |
| `Text` | The answer's text. For a stopped answer, the text streamed before it was stopped. |
| `Notices` | The `GovernanceNotice` values raised during the turn, in order. |
| `Notice` | The first notice, or null when there was none. |
| `Usage` | Token counts for the turn (`UsageDetails` from Microsoft.Extensions.AI), when the server reported them. |
| `TurnId` | The turn's id. |

```csharp
using Fadenstack.Governance;

var result = await session.SendAsync("Delete page 2.");
switch (result.Outcome)
{
    case ChatOutcome.Completed:
        ShowAnswer(result.Text);
        break;
    case ChatOutcome.PromptBlocked:
        ShowNotice(result.Notice?.Message);
        LetTheUserRephrase();
        break;
    default:
        ShowAnswer(result.Text);
        foreach (var notice in result.Notices)
        {
            ShowNotice(notice.Message);
        }
        break;
}
```

## Outcome values

`ChatOutcome` is in the `Fadenstack.Governance` namespace.

| Value | Meaning |
|---|---|
| `Completed` | The turn ended normally. |
| `PromptBlocked` | An input check refused the prompt. Nothing of it reached a model, and it is never sent again. The user can rephrase and send a new message. |
| `ResponseBlocked` | An output check stopped the answer. Tool calls in it are never run. Only the question stays in the session's history. |
| `ToolDenied` | At least one tool call was refused: by the tool mode, by the user, because an approval expired, or by the server. The answer may still be complete. |
| `RateLimited` | A limit of the server applied (429). The notice carries the server's message, and its `RetryAfter` says when to try again, if the server said. |
| `ApprovalRequired` | A call waits for the user's decision. An agent turn does not end with it, because the agent waits for the decision inside the turn. |

A refusal of the prompt or the answer decides the outcome before a denied tool does.

Reaching the limit of model calls per turn (`UseMaxRounds`) is not an outcome of its own: the turn ends with a `round_limit` notice and the outcome it had.

## Governance notices

A `GovernanceNotice` is what the user is told about a refusal or a limit, and what an auditor finds it by. The stream raises each one as a `NoticeRaised` event when it happens; the result lists them all.

| Property | Meaning |
|---|---|
| `Outcome` | `prompt_blocked`, `response_blocked`, `tool_denied`, `rate_limited` or `round_limit`, or another value the server sends. |
| `Message` | The text to show. For a policy, it is the administrator's message. |
| `Stage` | Where it happened: `input`, `output` or `tool`. |
| `Category` | The policy's category, when there is one. |
| `DecisionId` | The id of the server's decision. Show it where users report problems; administrators find the decision by it. |
| `RequestId` | The id of the request, when there is one. |
| `RetryAfter` | For a limit (429): when to try again, if the server said. |

Show `Message` as it is. Do not replace it with your own text: the administrator wrote it for the user.

## Exceptions

Failures throw. All SDK exceptions derive from `FadenstackException`, in the `Fadenstack` namespace.

| Exception | When |
|---|---|
| `FadenstackException` | The server could not be reached, or did not answer within the client's `Timeout`. Also thrown by `StartAsync` when the agent requires a client module the builder does not have. |
| `FadenstackApiException` | The server answered with an error. `Status` is the HTTP status; `Code`, `Type` and `Param` are the error's fields, for example the code `agent_not_granted`. |
| `FadenstackAuthenticationException` | 401: the token was refused, after the SDK retried once with a fresh token. |
| `FadenstackPermissionException` | 403: the user may not do this, for example because there is no grant for the agent or the agent is retired. |
| `FadenstackRateLimitedException` | 429 from a call outside an agent turn, such as the plain chat client or a resource. `RetryAfter` says when to try again, and `Code` which limit applied. Inside a turn, a 429 becomes the `RateLimited` outcome instead. |
| `FadenstackStreamException` | An answer that had started broke off, and the server said so. `Code` is the server's error code. |
| `FadenstackSignInRequiredException` | The agent's sign-in is gone: never made, expired, or revoked. Sign in again. |

`FadenstackApiException` is the base of the four HTTP-specific exceptions after it.

The SDK also throws standard .NET exceptions:

| Exception | When |
|---|---|
| `OperationCanceledException` | You cancelled the turn or the request. |
| `InvalidOperationException` | A turn is already running in the session, the builder is incomplete, or `agent.Profile` is read before the agent started. |
| `KeyNotFoundException` | `OpenSessionAsync` was given an id that is not stored on this device. |
| `ArgumentException` | For example, an `http://` server URL without `AllowInsecureHttp`, or `AddTools` on an object without tools. |

A turn that fails with an exception is recorded as failed in the session and is never sent to the model again. The session goes on with the next message.

```csharp
try
{
    var result = await session.SendAsync(text, cancellationToken);
    Show(result);
}
catch (FadenstackSignInRequiredException)
{
    ShowSignIn();
}
catch (FadenstackPermissionException e)
{
    ShowError($"You may not use this agent: {e.Message}");
}
catch (FadenstackException e)
{
    ShowError(e.Message);
}
```

Catch the more specific exceptions first: they all derive from `FadenstackException`.

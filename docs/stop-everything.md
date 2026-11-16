# Stop everything and the off-switch

Two ways an agent ends at once: the person stops it (the emergency stop), or the organisation turns it off. Both need
the server of core 0.7.1 or later for the server's part; without it the SDK still does what it can on the device.

## Stop everything

`agent.StopAllAsync()` (TypeScript: `agent.stopAll()`) is the emergency stop (release plan 8.11):

1. Every running turn in every open chat of the agent stops the way the chat's own Stop button does. The turn ends as
   stopped (its stream throws `OperationCanceledException`, the transcript says `cancelled`), a tool call that waits for
   the person's answer is answered **no** (the transcript and the server's record say so, with the reason "Stopped."; a call
   the gateway holds is told no), and no further tool call of the turn runs. One chat alone: `ChatSession.Stop()`
   (TypeScript: `chat.stop()`), which returns whether a turn ran.
2. Every grant the agent holds for the person ends: the server's grants (for a time, for a stretch of work, plan, Bypass)
   and each open chat's "for this chat" approvals.
3. What the person allowed **always** stays. It is their saved preference, not authority that runs; `ResetApprovalsAsync`
   forgets it.

The part on the device always happens, also offline. The server is told after, one grant at a time; a grant it could not
be told about is tried once more and, if that fails, counted: it ended on the device at once (no call runs under it from
now on, whatever the server later says) and ends at its time on the server. The stop never waits long for a slow server.

`StopAllAsync` returns a `StopAllResult` with `TurnsStopped`, `GrantsEnded` (the server's grants and the chats' approvals,
including those the server was not told about), `ChatApprovalsEnded` and `GrantsNotTold`; `NothingRan` is true when the
stop found nothing to stop. For an app with several agents, `FadenstackAgent.StopAllAsync(agents)` (TypeScript:
`stopAll(agents)`) stops them all on the device first and then tells the servers; the result is the sum.

### The control for the tray or the title bar

`StopEverythingViewModel` (`Fadenstack.UI.Settings`; TypeScript `StopEverythingViewModel`) takes the agents as a function,
because they come and go, and has `StopCommand`, `IsBusy` and `ResultText`: "Stopped 2 chats and ended 3 grants.", "Nothing
was running." or "Stopped. The server could not be told about 1 grant; it ends at its time." There is **no confirmation**:
it is the emergency stop. Each skin has `StopEverythingButton`: an icon button named "Stop everything" with a tooltip, and
the result line, in en, de, fr, es and zh-Hans. Host it where the person finds it quickly.

### A refused sign-in ends the turn

When the token provider's refresh is refused during a turn (the sign-in ended, the device was signed out, the grant is
gone), the turn ends at once with `FadenstackSignInRequiredException` (TypeScript: `FadenstackSignInRequiredError`), and no
tool call of that turn runs after the refusal; before this, a tool's error result let the next call of the turn run. The
chat panel then shows the sign-in again (ASVS 5.0 V7, session ending).

### Access tokens are short

Core cuts the agent access token to 300 seconds at most (from rc21). The SDKs refresh from `expires_in`, 30 seconds ahead,
and assume no lifetime. A device signed out stops within one token's life; an agent turned off, a person deactivated or a
sign-in ended stop at the next request.

## The off-switch

An administrator can **turn an agent off** in the console (release plan 8.3). From then on every agent route but the
profile, the events and the manifest answers 403 `agent_disabled` ("Your organisation turned this agent off."), for
everyone, and the server has ended the agent's grants (`revoked_by: agent_turned_off`).

- **Client:** `ErrorCodes.AgentDisabled`, `FadenstackApiException.IsAgentDisabled` (TypeScript: `errorCodes.agentDisabled`,
  `isAgentDisabled(error)`); `AgentProfile.Enabled` (`enabled`; true when the key is missing, as from an older server);
  `ChatOutcome.AgentDisabled` (`"agent_disabled"`).
- **Agent:** `agent.TurnedOff` (`turnedOff`) with the event `TurnedOffChanged` (`onTurnedOffChange(listener)`). It turns true
  when the profile at start says `enabled: false`, or any agent route answers `agent_disabled`: chat, `tools/check`,
  approvals, grants, models, tools, skills, user, provenance. At once the running turns stop and the grants end here (as
  in "Stop everything", without asking the server), and:
  - a new turn is refused **without a request**, with the outcome `AgentDisabled` and the sentence above as its notice;
  - **nothing retries**: no further request goes to any agent route (they fail on the device with the same error), no
    back-off, no reconnect, no background refresh of the profile or the grants;
  - the client events and the manifest are still sent, and `DELETE` of a grant is not needed (the server ended them);
  - someone the agent does not let in still gets `agent_not_granted` or `agent_retired`, as before.
- **Try again:** `agent.CheckAgainAsync()` (`checkAgain()`) reads the profile, the one route the server answers for an agent
  that is off, and clears `TurnedOff` when it says `enabled: true`. The SDK reads it again only at start and on this call.
- **The chat panel** shows the sentence with a "Check again" button, disables the composer, and ends the turn that hit the
  switch with the same sentence (`AgentChatViewModel.IsAgentOff`, `AgentOffText`, `CheckAgainCommand`, `IsInputEnabled`). The
  grants view shows `AgentOffGrantsText`: the grants have ended. All four skins, five languages (the console's own words in
  each).

## Testing

The SDK's own test gateway (the fake server its tests use) has `TurnOff()` and `TurnOn()` (TypeScript: `fakeGateway.turnOff()`, `turnOn()` in `@fadenstack/testing`): every agent route but the
profile, the events and the manifest answers the 403, the profile says `enabled: false`, and the grants end with
`revoked_by: agent_turned_off`.

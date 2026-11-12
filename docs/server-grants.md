# Server-held grants and Bypass

The server can hold what a person let the agent do for a while without asking: a grant for **a time** for one tool, a grant for **a stretch of work** in some places, and **Bypass**, which switches approvals off in one conversation. The SDK offers all three, on the same wire as the [TypeScript SDK](../typescript/server-grants.md).

Grants need a server that holds them: it says so in the agent's profile (the feature `approval_scopes.grant`), and `agent.Grants.Available` reports it. A time or work grant never covers a call an administrator's rule asks about, a tool the agent's contract marks `always`, or a `Destructive` or `Unknown` call. Bypass covers the destructive and unclassified calls the gateway says it covers, in its own conversation, and nothing that asks every time.

This page covers the grants, Bypass, the lifecycle that keeps a grant from running unattended, and where the device keeps its copy. [Host tools](tools.md#approvals) covers the approvals around them.

## Grants: for a time, and a stretch of work

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

## Bypass

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

## The grant lifecycle

A grant that outlives the person's attention is a risk: the app is closed, the screen is locked, and the agent still runs
under it. `GrantLifecycle` holds the rules for that, so every app gets the same ones. It looks after this device's stretches
of work and Bypass grants (`GrantLifecycleOptions.Manages` adds or narrows which), uses the server's clock for every time,
and does no operating-system work: the app feeds it what only the app knows.

```csharp
using var lifecycle = new GrantLifecycle(agent.Grants);
lifecycle.Ended += (_, e) => Say(e.Grant, e.Reason, e.Ran);       // once per grant, and why
lifecycle.EndingSoon += (_, e) => Offer(e.Grant, e.Left);          // two minutes before the end

await lifecycle.AppStartedAsync();                                  // once signed in
await lifecycle.SetScreenLockAsync(ScreenLockState.Locked);         // from the OS monitor
await lifecycle.AppEndingAsync();                                   // when the app closes
```

| The app tells it | It does |
|---|---|
| `AppEndingAsync(within)` | Pauses every running grant at the server (default wait: 3 seconds). The time runs on. Offline, a grant runs on at the server until its time. Returns how many were paused. |
| `AppStartedAsync()` | Reads the server's list; resumes each grant paused for less than 30 minutes (`GrantLifecycle.PausedLimit`), with no card and no second sign-in, and ends each paused longer (`GrantEndReason.Away`). The server may refuse the resume (403: it ended the grant, `Ended` says why), not be able to check (503) or be out of reach: the grant stays paused and is tried again at each tick. A grant found running runs on. With the screen locked, a paused grant resumes with the unlock. Returns how each fared (`GrantResumeOutcome`). |
| `SetScreenLockAsync(state)` | `Locked` pauses each stretch of work; `Unlocked` resumes what the lock paused. Locked for 30 minutes (`LockedFor`), the stretch of work ends (`GrantEndReason.Locked`). `Unknown` (the app cannot read the lock) never pauses, and after a lock it never resumes: only `Unlocked` does, and the 30-minute end still holds. Bypass is paused by the app's end only (`GrantLifecycleOptions.PausedByLock` changes this). |
| `StopAsync()` | Ends every grant it looks after, a paused one too, and says `Stopped` without asking the server. Returns how many ended; the rest run on at the server until their time. The app stops the running turns of their conversations itself. |
| `TickAsync()` | One look: the server's list once in 30 seconds while a grant runs, a resume the server could not take is tried again, the 30-minute lock end, grants that ended, grants near their end. The lifecycle ticks by itself every 15 seconds unless `GrantLifecycleOptions.TickEvery` is zero. |

It tells the app, on any thread: `EndingSoon` once per end time, two minutes before it (never for Bypass, which is never
extended; `KeepGoingAsync(id)` adds 30 minutes and the server cuts it at the longest grant), and `Ended` once per
grant with a `GrantEndReason`: `Time`, `Stopped`, `Spent`, `Locked`, `Away`, `Host` (`EndAsync`, for a setting of the app's
own), or the server's word when something else ended it (`Admin`, `KillSwitch`, `Limit`; `Elsewhere` when the server
cannot say). `Changed` is raised after each look, for a marker that counts down.

The chat panel's `GrantMarkerViewModel` shows the lifecycle for every chat at once: one marker for all running grants with
the time left, Stop, the "another 30 min" offer, and one summary per grant that ended, saying why. Its texts are in the
package's resources in every language it ships. A summary for a grant the lock ended waits until the screen is unlocked.
Where an app offers a grant in a chat, `AgentChatViewModel.Session` is the session to offer through, and the card names its
conversation (`GrantOffer.ConversationId`, `GrantPart.ConversationId`): chats sharing one `ApprovalBroker` show only their
own cards. A card offered with no conversation, or in a chat with no session open yet, shows in every chat.

## Stores per signed-in user

What a person allowed always, and their sessions, are kept in their own folder on the device (`users\<user>\approvals` and `users\<user>\sessions` in the data folder), so two people who use one operating system account never see each other's. The gateway names the user in the profile; from one that does not, the shared folders of an older SDK are used. A store you pass (`UseSessionStore`, `UseApprovalStore`) is used as it is, and keeping people apart is then yours. `agent.ListApprovalsAsync()` lists what the person allowed always, for a settings page, and `agent.ResetApprovalsAsync(tool)` ends it. [Sessions on the device](sessions.md#where-they-are-stored) describes the folders, and [Export and delete my data](sessions.md#export-and-delete-my-data) what a person can ask for.

# Sessions on the device

This page covers where an agent's sessions are stored and how they are protected, how long they are kept, how a person exports or deletes their data, how to continue a session, and how to plug in your own store.

## Sessions stay on the device

An agent's conversations are kept on the device that ran them, not on the server. The session's transcript is the truth about the conversation: each request's history is built from it. The server keeps usage and activity events, never the conversation's content.

[Sessions](../../concepts/sessions.md) explains what the server keeps.

## Where they are stored

By default the agent uses a `FileSessionStore`. It keeps one append-only file per session, per agent, in the folder of the signed-in Fadenstack user, inside the operating system user's own data folder:

```text
<data folder>\users\<user>\sessions\<agent>\<session id>.jsonl
```

The data folder is `%LOCALAPPDATA%\Fadenstack` on Windows, `~/Library/Application Support/Fadenstack` on macOS, and `$XDG_DATA_HOME/Fadenstack` on Linux (`~/.local/share/Fadenstack` when the variable is not set). It is never a machine-wide folder. `FadenstackPaths` in `Fadenstack.Storage` knows these paths. `<user>` is the user id the server names in the agent's profile, made safe as a folder name, so two people who use one operating system account each have their own sessions and "Allow always" approvals (in `users\<user>\approvals`). The folders are created so that only their owner may read them on macOS and Linux.

A server that names no user gets the shared `sessions` and `approvals` folders directly under the data folder, as the SDK before 0.3.0 kept them. When the SDK first learns of a user on a device that has no user folder yet, it moves those shared folders into that user's folder, once. After any user folder exists, nothing moves again, because one person's data must never go to another.

Each line of the file is one transcript entry. A transcript is personal data, so the default store writes it to disk only when the line can be encrypted:

| System | What encrypts the lines |
|---|---|
| Windows | DPAPI, for the current user: only that user on that machine can read them. |
| macOS and Linux with `Fadenstack.Secrets` | AES-256-GCM, with the data key in the Keychain or the Secret Service. |
| macOS and Linux without it | Nothing. The agent keeps the transcript in memory (`MemorySessionStore`) and logs one warning, event 2040. |

To encrypt on macOS and Linux, add the `Fadenstack.Secrets` package and call this once at start-up, before any store, token store or agent is created:

```csharp
using Fadenstack.Secrets;

FadenstackSecrets.UseOsKeyStore();   // true when data is now encrypted at rest
```

It returns false, and changes nothing, on a headless Linux without a Secret Service. Data written earlier without encryption is still read, and encrypted on its next write. Sign-in tokens and "Allow always" approvals use the same protection; without a key store they stay in files that only their owner may read. A store you create yourself with `new FileSessionStore()` writes through the same protector, so on macOS and Linux without a key store its files are readable by their owner and not encrypted. Data that one protector wrote, another cannot read, which is why the choice is made once, before the first store exists.

Entries are only ever appended. A turn that was stopped is still recorded, as stopped.

## What a session holds

`session.Transcript` lists the session's `TranscriptEntry` values:

| Property | Meaning |
|---|---|
| `Id`, `ParentId` | The entry's id, and the id of the entry before it. |
| `Timestamp` | When it was written. |
| `Type` | One of the `EntryTypes` constants, such as `user.message`, `assistant.message`, `tool.execution_complete`, `approval.decision`, `governance.notice` or `assistant.turn_end`. |
| `TurnId` | The turn it belongs to. |
| `Status` | For a turn's end: one of the `TurnStatus` constants, `ok`, `prompt_blocked`, `response_blocked`, `error` or `cancelled`. |
| `Data` | The entry's content as JSON: text, tool calls, results, notices. |

`session.History` shows what the next request would carry. It is built from the transcript by these rules:

- A turn that holds a `user.command` entry (a host tool the person started, see [Host commands](host-commands.md)) is left out entirely: it is local tool activity, not a conversation with the model.
- A refused prompt, a failed turn and a cancelled turn are left out entirely, and so is an older turn without an end (the app closed while it ran). Only the newest turn may still be running.
- Of a turn whose answer was stopped, only the question stays.
- Consecutive user messages are merged into one.
- Pictures the user attached go with the newest messages only, four in all; older ones are named in their place. A picture counts as 4,000 characters of the budget.
- When the history is larger than the agent's history budget (`UseHistoryBudget`), the oldest turns are left out. The current turn is always sent.

## Retention

`FileSessionStore` keeps the newest 50 sessions per agent, and none older than 90 days. Older sessions are deleted when a new session is created.

To keep more or fewer in the store the agent creates, use `UseSessionRetention`:

```csharp
using Fadenstack.Agents.Sessions;

builder.UseSessionRetention(new SessionRetention(MaxSessions: 20, MaxAgeDays: 30));
```

**The server's limits.** The agent's profile can carry `local_retention` (`AgentProfile.LocalRetention`, with `MaxSessions` and `MaxAgeDays`). Only positive whole numbers count. The agent takes the stricter of your limits and the server's for each; a limit the server leaves out stays yours, and the server cannot raise a limit above yours. This applies to the store the agent creates, not to one you pass.

To set the limits of a store you create yourself, pass them to it:

```csharp
builder.UseSessionStore(new FileSessionStore(
    retention: new SessionRetention(MaxSessions: 20, MaxAgeDays: 30)));
```

`FileSessionStore(directory, protector, retention)` also takes another folder and another `ISecretProtector`.

## Export and delete my data

A person can ask for the data a device holds about them (an access request, GDPR Art. 15 and 20) and for its deletion (an erasure request, Art. 17). The agent answers both for the signed-in user:

```csharp
using (var file = File.Create("my-data.zip"))
{
    await agent.ExportMyDataAsync(file);
}

var deletion = await agent.DeleteMyDataAsync();
if (deletion.HostStoresLeft)
{
    // A store you passed still holds this person's data: delete their part there.
}
await tokens.SignOutAsync();
```

`ExportMyDataAsync(stream)` writes a ZIP to the stream and leaves it open. The ZIP holds `README.txt`, one file `sessions/<agent>/<session>.json` per session, decrypted, and `approvals/<agent>.json` for each agent with tools allowed always. It covers the sessions of every agent the stores hold. A session file that cannot be read, because it is damaged or belongs to another user, is left out, and the README counts it. Sign-in tokens and secrets are never exported, and neither is what the server keeps; the README says so. A store of your own that cannot list its agents is read for this agent only.

`DeleteMyDataAsync()` deletes the signed-in user's folder with everything in it: the sessions and approvals of all agents and the person's preferences, such as how long they let the agent think. From a server that names no user, it deletes the shared `sessions`, `approvals` and `preferences` folders. Afterwards the stores start empty. It does not sign out: the sign-in tokens stay, and ending the sign-in is your call (`SignOutAsync`). It does not touch what the server keeps; that is for the server's operator to erase. When a folder cannot be deleted, it writes a warning and rethrows the `IOException` or `UnauthorizedAccessException`.

It does not empty a store you passed with `UseSessionStore` or `UseApprovalStore`, because that store may hold other people's data. The result says so, and the agent writes a warning to its log:

| `DataDeletion` | Meaning |
|---|---|
| `FoldersRemoved` | How many of the SDK's folders existed and were removed. |
| `HostSessionStoreLeft`, `HostApprovalStoreLeft` | A store you passed was left alone. |
| `HostStoresLeft` | Either of them: the person's data may still be in a store you keep. |

Both methods start the agent first, so they need a sign-in.

## Continue a session

```csharp
var sessions = await agent.ListSessionsAsync();
var session = await agent.OpenSessionAsync(sessions[0].Id);
```

- `ListSessionsAsync()` returns `SessionInfo` values, the most recently used first: `Id`, `Agent`, `Started`, `LastActivity` and `Title`, which is the beginning of the session's first message.
- `OpenSessionAsync(id)` continues the session in the tool mode it was last used in. What the user allowed for the chat ended with it: those tools ask again. What they allowed always still holds. An id that is not stored on this device throws `KeyNotFoundException`.
- `DeleteSessionAsync(id)` deletes a session from the device.

## Plug in your own store

To keep sessions somewhere else, for example in the host application's project file, implement `ISessionStore` and pass it to the builder:

```csharp
public interface ISessionStore
{
    Task AppendAsync(string agent, string sessionId, TranscriptEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptEntry>> ReadAsync(string agent, string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionInfo>> ListAsync(string agent, CancellationToken cancellationToken = default);
    Task DeleteAsync(string agent, string sessionId, CancellationToken cancellationToken = default);
}
```

```csharp
builder.UseSessionStore(new ProjectSessionStore(project));
```

- `AppendAsync` adds one entry at the end of a session. A session exists once its first entry is appended.
- `ReadAsync` returns a session's entries in the order they were appended, or an empty list for an unknown session.
- `ListAsync` returns the agent's sessions, the most recently used first.
- `DeleteAsync` removes a session.

The agent appends entries with `CancellationToken.None`, so a stopped turn is still recorded. Protect what you store: the transcript holds the conversation, tool arguments and tool results.

`MemorySessionStore` keeps sessions in memory only, for tests and demos.

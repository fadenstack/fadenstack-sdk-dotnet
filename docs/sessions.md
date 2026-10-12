# Sessions on the device

This page covers where an agent's sessions are stored and how they are protected, how long they are kept, how to continue one, and how to plug in your own store.

## Sessions stay on the device

An agent's conversations are kept on the device that ran them, not on the server. The session's transcript is the truth about the conversation: each request's history is built from it. The server keeps usage and activity events, never the conversation's content.

[Sessions](../../concepts/sessions.md) explains what the server keeps.

## Where they are stored

By default the agent uses a `FileSessionStore`. It keeps one append-only file per session, per agent, in the user's local data folder:

```text
%LOCALAPPDATA%\Fadenstack\sessions\<agent>\<session id>.jsonl
```

On systems other than Windows, the folder is under the user's local data folder in the same layout.

Each line of the file is one transcript entry. On Windows every line is encrypted with DPAPI for the current user, so only that user on that machine can read it. On other systems the lines are not encrypted; the folders and files are created so that only their owner may read them.

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

- A refused prompt, a failed turn and a cancelled turn are left out entirely.
- Of a turn whose answer was stopped, only the question stays.
- Consecutive user messages are merged into one.
- When the history is larger than the agent's history budget (`UseHistoryBudget`), the oldest turns are left out. The current turn is always sent.

## Retention

`FileSessionStore` keeps the newest 50 sessions per agent, and none older than 90 days. Older sessions are deleted when a new session is created.

To keep more or fewer, create the store yourself:

```csharp
using Fadenstack.Agents.Sessions;

builder.UseSessionStore(new FileSessionStore(
    retention: new SessionRetention(MaxSessions: 20, MaxAgeDays: 30)));
```

`FileSessionStore(directory, protector, retention)` also takes another folder and another `ISecretProtector`.

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

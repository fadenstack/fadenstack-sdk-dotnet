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

`<user>` is the user id the server names in the agent's profile, made safe as a folder name, or `standalone` for the person's own AI, so two people who use one operating system account each have their own sessions and "Allow always" approvals (in `users\<user>\approvals`). A server that names no user gets the shared `sessions` and `approvals` folders directly under the data folder, as the SDK before 0.3.0 kept them. [Data on the device](data-on-device.md) lists where the data folder is on each system, everything else the SDK keeps there, and [when the shared folders move to a person](data-on-device.md#the-move-from-before-030).

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

By default the SDK keeps the newest 50 chats per agent on the device, and none older than 90 days. Who decides depends on how the agent is connected:

| Connection | Who decides | What the person sees |
|---|---|---|
| Standalone | The person, in the app's settings: automatic deletion on or off, and the limits. Defaults: on, 90 days, 50 chats (or the app's `UseSessionRetention` limits). The machine policy may fix it (`chatRetention`, see [Machine policy](machine-policy.md)). | A switch and two limits; read only, with "Set by your organisation's policy on this computer", when the policy fixes it. |
| Governed | The server: the stricter of the app's limits and the profile's `local_retention`. The person's stored choice is ignored. | No setting: "Your organisation keeps at most N chats for D days on this device", or, when the server sets none, "This device keeps at most N chats for D days" (the app's limits, which still apply). |

**When it runs.** Pruning runs when the agent starts (after the profile is known, for this agent's chats, so an idle profile ages out too), when a new chat is created, and at once when the person turns automatic deletion on or lowers a limit. A chat that is open (one the agent gave out with `StartSessionAsync` or `OpenSessionAsync` and your app still holds) is never deleted, and counts among the chats kept. With automatic deletion off, nothing is deleted: chats stay until the person deletes them (`DeleteSessionAsync`). Only the store the agent creates is pruned, not one you pass.

**The person's choice** is kept per person on the device, in `users/<user>/preferences/retention.json` (see [Data on the device](data-on-device.md)), beside the thinking level. It belongs to the person, not to one agent. Read and change it with:

```csharp
ChatRetentionState state = agent.ChatRetention;   // Control: Person, Organisation or MachinePolicy; AutoDelete, MaxAgeDays, MaxSessions, CanChange
// A settings page, standalone only; the limits are whole numbers: days 1 to 3650, chats 1 to 10000 (ArgumentOutOfRangeException otherwise).
state = await agent.SetChatRetentionAsync(new ChatRetentionChoice(autoDelete: true, maxAgeDays: 30, maxSessions: 100));
```

`SetChatRetentionAsync` returns the state in force. In governed mode, or when the machine policy fixes the setting, it keeps nothing and returns the state as it is. Before `StartAsync`, `ChatRetention` shows the app's defaults (and, governed, no server limit yet).

**The settings control.** `Fadenstack.UI` has `ChatRetentionSettingsViewModel` (the switch, the two limits, Save and Cancel, the read-only states and their sentences, `Retext()` for a new language) and the four skins have a view for it: `ChatRetentionSettingsView` in `Fadenstack.UI.Wpf` and `Fadenstack.UI.Avalonia`, and the React and Vue components of the TypeScript SDK. Put the view where your app keeps its settings, set its `DataContext` to `new ChatRetentionSettingsViewModel(agent)` and call `LoadAsync()` once. The texts are in the five languages the package ships.

To keep more or fewer chats by default in the store the agent creates (the standalone person's starting point, and the app's limit in governed mode), use `UseSessionRetention`:

```csharp
using Fadenstack.Agents.Sessions;

builder.UseSessionRetention(new SessionRetention(MaxSessions: 20, MaxAgeDays: 30));
```

**The server's limits** (governed mode). The agent's profile can carry `local_retention` (`AgentProfile.LocalRetention`, with `MaxSessions` and `MaxAgeDays`). Only positive whole numbers count. The agent takes the stricter of your limits and the server's for each; a limit the server leaves out stays yours, and the server cannot raise a limit above yours. This applies to the store the agent creates, not to one you pass.

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

Both calls cover **this app's data** by default (since 0.3.0; owner, 16 Nov). Pass `DataScope.AllApps` for all of the person's Fadenstack data on the device, every app's: `ExportMyDataAsync(stream, DataScope.AllApps)` and `DeleteMyDataAsync(DataScope.AllApps)`. Before 0.3.0 the calls took everything; a caller that means everything passes `AllApps` now. `MyDataAppsAsync()` lists the apps that use the person's data, so that a confirmation can name them.

`DeleteMyDataAsync()` (this app) removes this app's agents' sessions, "Allow always" and thinking levels, and the app's record. The person's shared data (skills, the MCP list, keys, provenance records, the other preferences) goes only when no other Fadenstack app on the device uses the person's data; then the whole folder goes, as with `AllApps`. An agent another app also uses keeps its data. The result says what stayed: `DataDeletion.OtherApps`, `SharedDataKept` and `AgentsKept`. `DeleteMyDataAsync(DataScope.AllApps)` deletes the signed-in user's folder with everything in it. From a server that names no user, this app's removal takes this agent's sessions, "Allow always" and thinking level from the shared folders, and `AllApps` deletes the shared `sessions`, `approvals` and `preferences` folders. Afterwards the stores start empty.

`ExportMyDataAsync(stream)` writes a ZIP to the stream and leaves it open, in the same scope: exactly what the removal of this app would take, and the README names what stayed and for which apps. The ZIP holds `README.txt`, one file `sessions/<agent>/<session>.json` per session, decrypted, and `approvals/<agent>.json` for each agent with tools allowed always. A session file that cannot be read, because it is damaged or belongs to another user, is left out, and the README counts it. With `AllApps`, or when no other app uses the person's data, the rest of the person's folder goes in too: the preferences, the language, skills, the MCP servers without what can hold a secret, and the provenance records ([Data on the device](data-on-device.md#export-and-delete) lists them). Sign-in tokens, keys and secret values are never exported, and neither is what the server keeps; the README says so, and names any file it does not know. A store of your own that cannot list its agents is read for this agent only.

The removal does not sign out: the sign-in tokens stay, and ending the sign-in is your call (`SignOutAsync`). It does not touch what the server keeps; that is for the server's operator to erase. When a folder cannot be deleted, it writes a warning and rethrows the `IOException` or `UnauthorizedAccessException`.

[Data on the device](data-on-device.md#export-and-delete) lists what the export and the deletion leave, such as the shared folders.

**The settings control.** `Fadenstack.UI` has `MyDataSettingsViewModel`, "Your data on this device", and the WPF and Avalonia skins have `MyDataSettingsView` for it: export, "Remove my data…" (this app only, after a confirmation that names the apps that keep the shared data) and "Remove all my Fadenstack data on this device…". It sits beside `ChatRetentionSettingsView` on an app's settings page; see [Data on the device: Export and delete](data-on-device.md#export-and-delete).

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

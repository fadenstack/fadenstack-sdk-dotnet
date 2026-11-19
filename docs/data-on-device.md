# Data on the device

This page lists everything the SDK keeps on a device, in one place: every folder it writes, what each holds, how it is protected, how long it is kept, and how a person exports or deletes it. It also says what the server keeps in governed mode. The other pages link here instead of repeating it.

## Three folders of the OS user

The SDK keeps everything in the operating system user's own folders, never in a machine-wide one. `FadenstackPaths` in `Fadenstack.Storage` knows them:

| Folder | Windows | macOS | Linux |
|---|---|---|---|
| The data folder (`DataRoot`) | `%LOCALAPPDATA%\Fadenstack` (local, never roamed) | `~/Library/Application Support/Fadenstack` | `$XDG_DATA_HOME/Fadenstack` (default `~/.local/share/Fadenstack`) |
| The settings folder (`SettingsRoot`) | `%APPDATA%\Fadenstack` | the data folder | `$XDG_CONFIG_HOME/Fadenstack` (default `~/.config/Fadenstack`) |
| The log folder (`LogRoot`) | `logs` in the data folder | `~/Library/Logs/Fadenstack` | `$XDG_STATE_HOME/Fadenstack/logs` (default `~/.local/state/Fadenstack/logs`) |

An XDG variable counts only when it is an absolute path. On macOS and Linux the SDK creates the folders in the data folder and the log folder so that only their owner may open them (mode 0700), and writes its protected files with mode 0600, the file log's files included (`FadenstackFileLoggerProvider` creates them 0600; a file that cannot be made owner-only is not written, its entries are dropped and counted in `Dropped`, and a log folder that stays wider than 0700 gets one warning entry); on Windows the user profile's permissions apply. No setting moves a folder: a store you create yourself takes another folder (`new FileSessionStore(directory)`, `new FileTokenStore(directory)` and so on).

What the SDK writes, by folder (shown with `/`; Windows uses `\`):

```text
<data folder>
  users/
    standalone/                       the person's own AI (standalone mode)
      sessions/<agent>/<session id>.jsonl
      approvals/<agent>.json
      approvals/approvals-seal.key
      preferences/reasoning.json
      preferences/retention.json
      audit/audit.jsonl                 the record of tool calls (every app of the person writes to it)
      audit/audit-seal.key
      audit/audit-anchor.json
      audit/audit-head.json
      audit/audit.lock
      language
      skills/<skill>/SKILL.md
      preferences/skills.json
      preferences/web-search.json
      keys/web-search-brave.key
      mcp/servers.dat
      mcp/servers.dat.lock
      mcp/audit.jsonl
      keys/provenance-signing.key
      provenance/records/<record id>.json
      provenance/placements.jsonl
    <user>/                           each person a server names (governed mode)
      sessions/<agent>/<session id>.jsonl
      approvals/<agent>.json
      approvals/approvals-seal.key
      preferences/reasoning.json
      audit/audit.jsonl                 the record of tool calls (every app of the person writes to it)
      audit/audit-seal.key
      audit/audit-anchor.json
      audit/audit-head.json
      audit/audit.lock
      skills/<skill>/SKILL.md
      preferences/skills.json
      preferences/web-search.json
      keys/web-search-brave.key
      mcp/servers.dat
      mcp/servers.dat.lock
      mcp/audit.jsonl
  tokens/<hash>.token                 shared by everyone on the OS account
  keystore/                           shared (macOS and Linux, with Fadenstack.Secrets)
  agents/<agent>-<process>-<instance>-<window>.json   shared
  leases/<hash>.json                  shared
  logs/                               Windows; on macOS and Linux in the log folder
<settings folder>
  preferences.json                    shared: the chat panel's choices
```

## A person's folder

`users/<user>/` holds what belongs to one person, so two people who use one OS account never see each other's chats:

- **Standalone** (`UseEndpoint`, `UseChatClient`): `users/standalone/`, apart from any governed person's. A governed server that names a user `standalone` (in any letter case) gets `users/governed.standalone/` instead: the SDK puts `governed:` in front of that id, and a folder name with a dot comes from no other id, so the person's own AI never shares its folder with a server's user.
- **Governed:** the user id the server names in the agent's profile (`AgentProfile.Principal.UserId`), made safe as a folder name (`FadenstackPaths.SafeName`).
- **A server that names no user:** `sessions`, `approvals` and `preferences` directly in the data folder, shared, as the SDK kept them before 0.3.0.

| In the folder | What it holds | How it is protected | How long it is kept | Export and delete |
|---|---|---|---|---|
| `sessions/<agent>/<session id>.jsonl` | The chats: one append-only file per session, one transcript entry per line, with the messages, attachments, tool calls and results, approval decisions and notices ([What a session holds](sessions.md#what-a-session-holds)). | Every line encrypted: DPAPI for the current user on Windows; AES-256-GCM on macOS and Linux with `Fadenstack.Secrets`, the data key in the Keychain or the Secret Service. Without either, nothing is written: the chat stays in memory (event 2040). | By default the newest 50 sessions per agent, none older than 90 days; the others go when the agent starts, when a new session starts, and when the person turns deletion on or lowers a limit. In standalone the person can change the limits or turn deletion off (`preferences/retention.json`, below), and the machine policy can fix them; in governed mode the server's `local_retention` and `UseSessionRetention` decide ([Retention](sessions.md#retention)). | Exported, decrypted; deleted. |
| `approvals/<agent>.json` | "Allow always": each tool, its class, its source and server, when it was allowed, and its seal ([Host tools](tools.md#allow-always)). | Encrypted as above; without a key store, a file only its owner may read. | Until the person resets them (`ResetApprovalsAsync`). | Exported; deleted. |
| `approvals/approvals-seal.key` | The key that seals the approvals beside it: 32 random bytes. | Encrypted as the approvals. | As long as the approvals. | Never exported; deleted. |
| `preferences/reasoning.json` | How long the person lets each agent think. | Plain JSON. | Until the person changes it. | Exported; deleted. |
| `preferences/retention.json` | The person's choice for old chats in standalone mode: `autoDelete` (true or false), `maxAgeDays` (1 to 3650) and `maxSessions` (1 to 10000), for example `{"autoDelete":true,"maxAgeDays":90,"maxSessions":50}`. Not read in governed mode, where the server decides, nor where the machine policy fixes the setting. No chat text. | Plain JSON. A damaged or out-of-range file counts as no choice (the defaults). | Until the person changes it. | Exported; deleted. |
| `skills/<skill>/` | The person's own skills (`SKILL.md` and its files), as the skill editor writes them (`SkillWorkshop`); the skills module reads them for the person the profile names ([Skills](skills.md)). | Plain files. | Until the person deletes the skill in the editor. | Exported as the files (the editor also exports one skill as a zip); deleted. |
| `preferences/skills.json` | The names of the skills the person turned off. | Plain JSON. | Until the person changes it. | Exported; deleted. |
| `preferences/web-search.json` | The person's web search provider (Brave or SearXNG) and the SearXNG address; no key ([Pictures and web search](web-search.md)). | Plain JSON, owner-only. | Until the person changes it. | Exported; deleted. |
| `keys/web-search-brave.key` | The person's Brave Search API key. | Encrypted as the sessions. Without a key store it is not written: the key stays in memory. | Until the person removes it. | Never exported; deleted. |
| `language` (standalone) | The language the person picked, a BCP 47 tag. In governed mode the server keeps it. | Plain text. | Until the person changes it. | Exported; deleted. |
| `mcp/servers.dat` | The person's own MCP servers: the program or address, its environment values and headers, on or off, the trust mark, the person's raises and the local-network switch ([Local MCP servers](local-mcp.md)). From a server that names no user, the list stays in memory. | Encrypted as the sessions. Without a key store it is not written: the list stays in memory. | Until the person removes a server. | Exported as `mcp/servers.json`, without what can hold a secret: the label, the program or the address without a user, password, query or fragment, the settings, the number of arguments, and the names of the environment variables and headers, never their values; deleted. |
| `mcp/servers.dat.lock` | Nothing: an empty file whose lock keeps two apps' changes to the list apart. | Owner-only. | As long as the list. | Deleted. |
| `mcp/audit.jsonl` | The device's audit log of the switch "Allow plain http on the local network": the time, the server's origin, on or off, the warning's version and the person. | Not encrypted. | Rotates at 1 MiB; three old files kept. | Exported, with the old files; deleted. |
| `keys/provenance-signing.key` (standalone) | The device's key that signs provenance records ([Content provenance](provenance.md)). | Encrypted by DPAPI or the OS key store. Without either it is never written: a new key lives in memory for the run. | Until deleted. | Never exported; deleted. |
| `provenance/records/<record id>.json` (standalone) | Each signed provenance record with its time-stamp: hashes, the media type and the kind of edit, never the content. | A file only its owner may read; not encrypted. The id is a GUID: a file whose record has any other id is skipped when read (log event 4010, never the id), so an id read from disk never becomes a path. | No limit. | Exported (`KeptRecords()` also reads them); deleted. |
| `provenance/placements.jsonl` (standalone) | Where tools placed marked content: hashes and outcomes. | Not encrypted. | Rotates at 1 MiB; three old files kept. | Exported, with the old files; deleted. |
| `audit/audit.jsonl` | The record of tool calls: one entry per tool call, with the tool's name and class, who or what let it run, the outcome and a hash of the arguments, never the prompt, the answer, the arguments or the result ([The record of tool calls](audit-log.md)). Every Fadenstack app of the person on the device appends to the same file; each entry links to the one before by a hash. | Every line encrypted as the approvals are; sealed with the key below. | 180 days by default, or the machine policy's `auditRetentionDays` (30 to 3650); older entries go at the start and at most once a day, and the last removed one stays in the anchor. | Exported as `audit/tool-calls.csv` with its verification on top, and by `ExportAuditAsync` as a CSV or a web page. Shared data: deleted with "Remove all", or with this app's data when no other app uses the person's data. |
| `audit/audit-seal.key`, `audit/audit-anchor.json`, `audit/audit-head.json` | The seal key of the record (32 random bytes), the sealed mark a prune leaves (the last removed entry's number and hash) and the sealed head (the last entry's number and hash, so a cut-off end is found). | The key encrypted as the approvals; the anchor sealed with it. | As long as the record. | Never exported; deleted with the record. |
| `audit/audit.lock` | Nothing: an empty file whose lock keeps two apps' appends apart. | Owner-only. | As long as the record. | Deleted with it. |
| `apps/<app>.json` | Which apps use the person's data on this device: the app's name (what it described with `DescribeApp`, else the agent's slug), its agents, when it was first and last seen. Written at each agent start; no content. | A file only its owner may read. | As long as the app's data. | Exported; deleted with the app's data. |

In governed mode the server signs and keeps the provenance records, so nothing of provenance is written to the person's folder.

## Shared by the OS user

These folders belong to the OS user, not to one person: everyone who uses the SDK under that account shares them.

| Where | What it holds | How it is protected | How long it is kept | Export and delete |
|---|---|---|---|---|
| `tokens/<hash>.token` | The sign-in for one server and agent: the access and refresh tokens and the device id. The name is a hash of the server and the agent; a `.lock` file sits beside it. | DPAPI or the OS key store; without either, a file only its owner may read. | Until sign-out. A second sign-in to the same server and agent replaces the first. | Never exported. `SignOutAsync` revokes the sign-in and deletes the file; `DeleteMyDataAsync` keeps it. |
| `keystore/` | Only with `Fadenstack.Secrets` on macOS and Linux: the key store library's file. The data key itself, 32 random bytes and one per OS user, is in the Keychain or the Secret Service. | The OS key store. | As long as the OS account. | Never exported. Removing the key store's entry makes every file it encrypted unreadable. |
| `agents/<...>.json` | The device agent registry: which agents run, in which process, with a heartbeat, so the desktop agent and the add-ins find each other. | A file only its owner may read. | Removed when its process ends or its heartbeat stops for 2 minutes. | No personal content. |
| `leases/<hash>.json` | A short write lease on a document, so two agents never change it at once: the holder, its process and the expiry. | The folder's. | Removed when released; taken over once it expired or its holder ended. | No personal content. |
| The log folder: `<app>-<yyyyMMdd>.log` | Only when the app adds the SDK's file log (`FadenstackFileLoggerProvider`): the SDK's events, with names, ids, counts and durations; never prompts, answers, tool arguments or results, tokens, or who the user is. What the app logs through the same logger is written as it is ([Logging](agent.md#logging)). | The folder's. | 5 MB per file, 14 days, 50 MB in all per app. | Not exported. "Remove my data" deletes this app's files (every provider alive in the process: `<app>-*.log` and the numbered parts) and logging goes on in a new file; what was queued is dropped. With `DataScope.AllApps` every other file in the log folder and the per-user folders below it goes too (other apps' logs are the person's data). A file another process holds open goes where the OS allows it; the rest is counted in `DataDeletion.LogFilesLeft` and logged as event 2093. `DataDeletion.LogFilesRemoved` counts the files. |
| The settings folder: `preferences.json` | The chat panel's choices, such as the language picked in the panel; shared by every Fadenstack panel of the OS user. | Plain JSON. | Until changed. | Not exported or deleted. |

## The move from before 0.3.0

Before 0.3.0 the SDK kept `sessions`, `approvals` and `preferences` (the thinking level) directly in the data folder, for everyone on the OS account. The first time a governed agent learns of a user on a device where no person has a folder yet (`users/standalone/` does not count), it moves those three folders into that user's folder, once. After that nothing moves again, because one person's data must never go to another:

- a standalone start never takes them, so a governed person who signs in later still gets their older chats;
- a move that fails is logged with the kind of failure only, and the folders not moved yet stay where they were (`preferences` goes last, so the chats and approvals move first);
- a server that names no user goes on using the shared folders.

## Export and delete

A person can ask for the data a device holds about them (GDPR Art. 15 and 20) and for its deletion (Art. 17). [Export and delete my data](sessions.md#export-and-delete-my-data) shows the calls.

Several Fadenstack apps may use one person's folder: the Word add-in, the desktop agent, Excel. Since standalone.9 the calls work per app (`DataScope`):

- **This app** (`DataScope.ThisApp`, the default): the sessions and "Allow always" of the app's agents, their thinking levels, and the app's record (`apps/<app>.json`). An agent that another app's record names too keeps its data (`DataDeletion.AgentsKept`).
- **The shared data**, which no single app owns: skills and the skills turned off, the MCP list and its audit log, the record of tool calls, keys, provenance records and log, the web search and old-chats settings, the standalone language, the approvals' seal key. It goes with this app's data only when no other app uses the person's data: no other app's record, and no data of an agent that no record names (an app on an SDK before standalone.9 writes no record, so its data stands for it, which keeps the shared data). Otherwise it stays, and `DataDeletion.OtherApps` names the apps it stays for. When nothing is left, the person's folder goes as a whole. The seal key goes with the last "Allow always".
- **All apps** (`DataScope.AllApps`): the person's whole folder, every app's data and the shared data. `agent.MyDataAppsAsync()` lists the apps it affects first, this app first (`MyDataApp.Recorded` false for an app known only by its data, named by its agent).

**The control.** `Fadenstack.UI` has `MyDataSettingsViewModel` ("Your data on this device"), with a view in `Fadenstack.UI.Wpf` and `Fadenstack.UI.Avalonia`, in the five languages the package ships. It offers "Export my data…" and "Export all…" (the same scopes as the export calls), a line about the person's record of tool calls ("Record of tool calls: 12 entries since 01.08.2026, intact", or "damaged at entry 8") with "Export the record of tool calls…", which saves it as one web page through the same save target, "Remove my data…" and, visually quieter, "Remove all my Fadenstack data on this device…". Neither removal runs at once. The person first reads what goes and what stays:

- "Remove my data…" reads `agent.MyDataAppsAsync()`. When other apps use the person's data, it says that this app's chats, approvals and preferences go and that the shared data (skills, the MCP list, keys, provenance records) stays for those apps, and names them (an app on an older SDK by its agent's name). When no other app does, it says that everything of the person's on this device goes.
- "Remove all my Fadenstack data on this device…" lists every app it affects, this app marked.

The result then says what was removed and what stayed (`DataDeletion.OtherApps`, `AgentsKept`, `HostStoresLeft`), or that everything went. A failure is one sentence with no reason and no content: no exception text and nothing from the data reaches the screen or the log. Where an export is saved is the host's choice: the app passes an `IMyDataSaveTarget` (`WpfMyDataSaveTarget`: the system's save dialog; `AvaloniaMyDataSaveTarget`: the storage provider's save picker), because a view model opens no dialog. This follows OWASP ASVS 5.0 V14 (data protection): the person can see what a removal covers before it runs, a removal never reaches data another app still uses unless the person asks for all of it, and the export covers what the removal covers.

```csharp
var data = new MyDataSettingsViewModel(agent, new WpfMyDataSaveTarget(() => window), new WpfDispatcher());
settingsPage.Content = new MyDataSettingsView { DataContext = data };   // beside the old-chats setting
```

| Call | What it covers | What it leaves |
|---|---|---|
| `agent.ExportMyDataAsync(stream)`, `(stream, DataScope.ThisApp)` | A ZIP with exactly what this app's removal takes: its agents' sessions, decrypted, their "Allow always" approvals, their thinking levels and its record; with the shared data when no other app uses the person's data. The README names the scope and, when other apps use the data, what stayed and for which apps. | What other apps use; tokens, keys and secret values; what the server keeps. |
| `agent.ExportMyDataAsync(stream, DataScope.AllApps)` | A ZIP with all the person's data on this device: every app's sessions, decrypted, and "Allow always" approvals, the apps' records, the preferences (the thinking levels, the skills turned off), the standalone language, skills, the MCP servers without what can hold a secret and their audit log, the standalone provenance records and log, the record of tool calls as `audit/tool-calls.csv` (with its verification on top), and a README. A file the export does not know is named in the README, not exported. From a server that names no user: the shared folders, `preferences` too. | Tokens, the provenance signing key and secret values (MCP arguments, environment and header values), which are never exported; the shared folders; what the server keeps. |
| `agent.DeleteMyDataAsync()`, `(DataScope.ThisApp)` | This app's data, as above; the shared data and the folder only when no other app uses the person's data. From a server that names no user: this agent's sessions and "Allow always" in the shared folders. | Other apps' data and, while they use it, the shared data (`OtherApps`, `AgentsKept`); the sign-in; another person's folder; a store your app passed (`HostStoresLeft`); what the server keeps. |
| `agent.DeleteMyDataAsync(DataScope.AllApps)` | The person's whole folder, `users/<user>/`, with everything in it. From a server that names no user: the shared `sessions`, `approvals` and `preferences` folders. | The sign-in (sign out for that); the shared folders; another person's folder (deleting a governed person's data leaves `users/standalone/`, and the other way round); a store your app passed (`HostStoresLeft`); what the server keeps. |
| `tokens.SignOutAsync()` | Revokes the sign-in on the server where it can be reached, and deletes the token file. | The person's folder. |
| `agent.DeleteSessionAsync(id)`, `agent.ResetApprovalsAsync()` | One session; this agent's "Allow always". | |

## What your app keeps

- **A store you pass** (`UseSessionStore`, `UseApprovalStore`, your own `ITokenStore`) is yours: the SDK neither exports nor deletes it, and `DataDeletion.HostStoresLeft` says so.
- **The connection wizard** keeps nothing itself. Your app keeps the connection (`IConnectionStore`: the server or the endpoint, never a key or token) and the endpoint's key (`IEndpointKeyStore`: DPAPI, the Keychain or libsecret); see [Connecting an agent](connecting.md). The wizard's sign-out (`IConnectionStore.SignOutAsync(deleteLocalChats)`) lets the person choose whether their chats go too.
- **The machine policy** is a file the app or IT puts in place; the SDK reads it (`MachinePolicy.Load`) and never writes it ([Machine policy](machine-policy.md)).
- **A host app** may keep its own settings in the settings folder, such as Office's in `%APPDATA%\Fadenstack\Office\`. The app's own documentation describes them.

## What the server keeps in governed mode

The conversation stays on the device. The server keeps usage and governance data, never the conversation's content:

- **Model requests** pass through the server's gateway on their way to the model, where the organisation's policies apply ([Personal data](../../concepts/personal-data.md)), and are counted for usage.
- **The agent's events**, with the session's id where there is one: `tool.executed` (the tool, its source, its duration and outcome), `approval.decided` (the tool, the decision, how long the person took, what let it through), `local_mcp.attached` (the server's label, its program or its address without a user, password, query or fragment, and its tools' names), `local_mcp.plain_http` (an MCP server's origin, and whether its local-network switch is on, where the profile's features list `client_events.local_mcp_plain_http`), `session.stats` (counts, and the retention in force) and `agent.delegated`.
- **The app manifest:** the app's name and version, the SDK's version, the host tools with their classes, and the context and greeting the app describes ([Build an agent](agent.md)).
- **The person's settings,** such as their language, and the device's sign-in with its label, until it is revoked.
- **Server-held grants,** with their time limits ([Server-held grants and Bypass](server-grants.md)).
- **Provenance records:** what the server signs for content a tool places (content hashes, the media type, the kind of edit and the receipt, never the content) and the placement reports ([Content provenance](provenance.md)).

`UseEventReporting(false)` stops the events and the manifest. What the server keeps is for its operator to export or erase: the SDK's export and deletion cover the device only. [Sessions](../../concepts/sessions.md) explains what the server keeps.

In standalone mode no Fadenstack server takes part. The chat goes to the endpoint the person chose and a tool call to its tool; where the machine policy allows it, a provenance record's signature hash goes to the time-stamp authority. Nothing else leaves the device.

## No long-term memory yet

The agent keeps no memory of the person beyond the chat itself: nothing from one session reaches another, on the device or on the server, and each request carries its own session's history only. `FadenstackClient.Memory` ([Without an agent](plain-api.md)) reads and writes memory facts the server keeps; an agent never uses it.

OWASP ASVS 5.0: V11 (cryptography), V14 (data protection), V16 (security logging).

# The record of tool calls

Every agent keeps a record of what its tools did on this device: one entry for each tool call, with who or what let it run
or stopped it, and what became of it. The person has it for themselves, and for the defence file of the organisation that
runs the agent (EU product liability, the AI Act's logging duty). It is **content-free**: no prompt, no answer, no
argument and no result text, only names, classes, decisions and a hash of the arguments. It is hash-chained and sealed,
so a changed, deleted, swapped or forged entry is found. OWASP ASVS 5.0 V16 (security logging: integrity, protection,
retention). The TypeScript SDK keeps the same record in the browser ([TypeScript: The record of tool calls](../typescript/audit-log.md)).

It is on by default for every agent, governed and standalone, and every app of the person on this device (the Word and
Excel add-ins, the desktop agent) writes into the same log.

## What an entry holds

| Field | What it says |
|---|---|
| `seq` | The entry's number, from 1, without gaps. |
| `time` | When, UTC, ISO 8601 with milliseconds (`2026-11-16T09:30:00.123Z`). |
| `kind` | `tool` today; the field stays so later kinds (a stop, a switch turned off) fit. |
| `app` | The app's name as the per-app records use it (`DescribeApp`, else the agent's slug). |
| `agent`, `chat`, `turn` | The agent's slug, the chat's id and the turn's id. |
| `tool`, `class` | The tool's name (`(unknown)` for a name the model made up) and its class (`read`, `write`, `destructive`, `unknown`). |
| `source`, `server` | Where the tool comes from (`host`, `mcp`, `skills`, `web`, `gateway`) and the MCP server's label, as the approvals key them. |
| `decision` | Who or what let the call run, or stopped it (below). |
| `scope` | For how long an approval holds: `once`, `chat`, `always`, `time`; empty when none applies. |
| `outcome` | What became of the call (below). |
| `arguments_sha256` | SHA-256, lowercase hex, of the arguments' JSON text ([below](#the-hash-of-the-arguments)). |
| `prev`, `hash`, `seal` | The chain ([below](#the-chain)). |

**Decisions** (`AuditDecisions`):

| Decision | Meaning |
|---|---|
| `asked_approved`, `asked_denied`, `asked_timeout` | The person was asked and allowed it (the scope says for how long), declined, or did not answer in time. |
| `saved_approval` | An approval the person gave earlier stood: "Allow always" on this device (`always`), or "for this chat" (`chat`). |
| `mode` | Nobody was asked: the chat's tool mode lets this class of tool run. |
| `rule_allowed` | The server's rule for host tools said allow (the dispatch check answered `run`), and nobody was asked. |
| `rule_denied` | A rule refused it: the server's deny rule or the gateway's refusal, or the app's own hook. |
| `server_grant` | A grant the server holds (for a time, for a stretch of work, or the chat's Bypass) let it run, and nobody was asked. |
| `floor` | The approval floor says the question is never skipped, and the app has no way to ask it (no approval handler): the call did not run ([The never-skip list](approvals.md#the-never-skip-list)). |
| `hidden` | The chat's tool mode (ReadOnly) hides the tool: refused. |
| `none` | Nothing was decided: the tool does not exist, its arguments were not JSON, or the call stopped before a decision. |

**Outcomes** (`AuditOutcomes`): `ran`, `failed` (it ran and failed), `refused` (somebody or something said no), `cancelled`
(the turn was stopped while the call waited or ran) and `not_run` (it never ran and nobody refused it: an unknown tool,
arguments that were not JSON, a check that could not be made, a question nobody answered in time).

A call a gateway tool holds for the person (`faden.approval_required` with an approval id) is recorded when the person
answers, with source `gateway`: `ran` means it was released to the gateway, which runs it; the SDK sees no more of it.

## The hash of the arguments

`arguments_sha256` is the SHA-256 of the arguments' JSON text exactly as the tool call carried it (the model's
`function.arguments` string), UTF-8, lowercase hex. The server stores the SDK's value and never computes it itself, so both
SDKs must agree, and they do (a shared test vector is in both suites).

| Path | The text that is hashed |
|---|---|
| A gateway chat, or a standalone endpoint that streams tool calls | The `function.arguments` string as the stream carried it (`ChatWire.ArgumentsText` reads it from the call's `faden.arguments_text` property); blank is `{}`. |
| A provider that hands over parsed arguments only (Microsoft.Extensions.AI) | The JSON the SDK itself writes back as `function.arguments` in its next request (compact, properties in their order). |
| A person's own command (`InvokeToolAsync`) | The canonical command JSON the server's check also sees. |
| A call a gateway holds (`faden.approval_required` with an `approvalId`) | From core 0.7.1 rc22 the event carries `arguments` (the text, cut at 8000 characters), `argumentsTruncated` (only when it was cut) and `argumentsSha256` (of the whole text). The entry takes the gateway's hash; if only the text came and it was not cut, the SDK hashes it; a cut text without a hash leaves the field empty. An older gateway sends none of these, so the field is empty. |

## The chain

`hash` is the SHA-256 (lowercase hex) of a canonical text: `fadenstack.audit.v1`, then `seq`, `time`, `kind`, `app`,
`agent`, `chat`, `turn`, `tool`, `class`, `source`, `server`, `decision`, `scope`, `outcome`, `arguments_sha256` and `prev`,
joined by a line feed, an absent value as the empty string. It is never a JSON serialization, so both SDKs agree byte for
byte (`AuditChain.CanonicalText`). `prev` is the previous entry's `hash`, 64 zeros for entry 1. `seal` is the base64
HMAC-SHA256 of `hash` under a key of this log's own, made at the first write.

## Where it is kept

`FileAuditLog` keeps it in the person's data folder ([Data on the device](data-on-device.md)): `users/<user>/audit/`, or the
data root's `audit/` from a server that names no user.

| File | What it holds |
|---|---|
| `audit.jsonl` | One entry per line; each line is `base64(protector.Protect(utf8 json))`, with the same `ISecretProtector` the approvals use (DPAPI on Windows, owner-only files elsewhere). |
| `audit-seal.key` | The seal key: 32 random bytes, protected the same way. |
| `audit-anchor.json` | What a prune leaves: the last removed entry's `seq` and `hash`, sealed. |
| `audit-head.json` | The sealed head: the last entry's `seq` and `hash`, written under the lock with every append (HMAC over `fadenstack.audit.head.v1`, the seq and the hash, the same shape as the anchor's seal). A prune leaves it alone. |
| `audit.lock` | An empty file whose exclusive lock the writers of every app take. |

**Several processes.** Each writer holds the exclusive lock on `audit.lock` while it reads the last entry and appends the
next, retrying for a few seconds, so two apps never fork the chain. Two instances appending in parallel are tested.

**Writing never breaks a turn.** An append that fails is logged (the kind of failure only, event 2083), the tool call goes
on, and the next verify reports the gap.

## Keeping and removing it

Entries older than the retention go at the start of the agent and then at most once a day: 180 days by default, or the
machine policy's `auditRetentionDays` (30 to 3650; [Machine policy](machine-policy.md)). The last removed entry's `seq` and
`hash` stay in the sealed anchor, so what is left still verifies; the file is rewritten atomically (a temporary file, then
a replace). `agent.AuditRetentionDays` says the number in force.

## Checking and exporting it

```csharp
AuditVerification result = await agent.VerifyAuditAsync();
// result.Intact, Entries, FirstSeq, FirstTime, LastTime, BrokenAtSeq, Problem

await using var file = File.Create("tool-calls.html");
await agent.ExportAuditAsync(AuditExportFormat.Html, file);   // or AuditExportFormat.Csv
```

`VerifyAsync` checks that every line decrypts and parses, that `seq` has no gap, that every `prev` links, that every hash
recomputes, that every seal holds, and that the first entry links to the anchor (or to zeros at entry 1). A problem names
its kind (`AuditProblems`: `unreadable_line`, `seq_gap`, `prev_mismatch`, `hash_mismatch`, `seal_mismatch`, `anchor_mismatch`,
`anchor_missing`, `key_missing`, `head_mismatch`, `head_missing`) and the entry's number, never content. The head makes a cut-off end visible: the last entry must be the one the sealed head names (`head_mismatch` when entries after the head's number are missing, or the head names another hash or was forged; `head_missing` when entries exist and the head was deleted). A log from before the head existed takes its head with its next append.

`ExportAsync` writes every entry readable, with the verification result at the top. The CSV has the verification in its
first two lines, then one row per entry; a cell that starts with `=`, `+`, `-` or `@` gets a leading apostrophe so a
spreadsheet never reads it as a formula. The HTML is one self-contained page: a table, escaped text, inline CSS, no script
and no outside resource. Both are in English: they are a record, not a screen.

## In your app

```csharp
var agent = FadenstackAgent.CreateBuilder()
    /* ... */
    .UseAuditLog(new MemoryAuditLog())   // tests, or the app's own place; by default a FileAuditLog per person
    .Build();
```

- **By default** every agent keeps the durable record in the person's folder, whatever stores the app passed. An app that wants no
  durable record (one whose server keeps the record, like the Nextcloud app) passes a `MemoryAuditLog` on purpose.
- `UseAuditLog(IAuditLog)` replaces the log. `IAuditLog` has `AppendAsync`, `VerifyAsync`, `ExportAsync` and `PruneAsync`;
  `MemoryAuditLog` and `FileAuditLog` implement it.
- The agent appends in one place: where it settles and finishes a tool call (`ChatSession`), not in every tool.

## What the server is told

`approval.decided` and `tool.executed` of a call the SDK runs carry `arguments_sha256` **only when the profile's `features` list
`client_events.arguments_sha256`**. The answer to a call the gateway holds carries none: the server records its own hash of those
(it still accepts one). An older server
refuses a whole batch with an unknown field (400 `invalid_events`), so without the feature nothing new is sent
(`FadenstackAgent`'s `SendsArgumentsHash`; the fake gateways teach the field and the feature). The server can pair its own
record of a call with the device's by this hash without ever holding the arguments' text.

## What the person sees

"Your data on this device" ([Data on the device](data-on-device.md#export-and-delete)) has a line and a button for it:
"Record of tool calls: 12 entries since 01.08.2026, intact" (or "damaged at entry 8", "no entries yet", "could not be
checked"), and "Export the record of tool calls…", which saves the HTML page through the control's save path
(`IMyDataSaveTarget`). The export of the person's data holds the record as `audit/tool-calls.csv` with its verification on
top. The record is shared data, like the provenance records: "Remove my data…" leaves it while another Fadenstack app uses
the person's data on the device, and "Remove all my Fadenstack data on this device…" or the last app takes it. The texts
are in the five languages the package ships.

## What it does not do

It is tamper-evident, not tamper-proof. Someone who can read the person's protected files as that person (the same OS
account) holds the seal key and can write a chain that verifies. What it finds is what the person or a defence file needs: a
changed field, a deleted or swapped entry, an entry from another person's log, a seal that does not hold, a log cut at the
front without its anchor, and one cut at the end (the sealed head). What it can still miss: the whole folder replaced by an
older copy of itself, which verifies as it did then.

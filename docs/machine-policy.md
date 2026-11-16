# Machine policy

An organisation that manages its computers can set, for every app built on the SDK, whether its agents may use the
person's own model, which endpoints, and whether local MCP servers, local skills, web search and plain http in the local
network are allowed. The app reads the file from its managed location; the SDK enforces it.

```csharp
var policy = MachinePolicy.Load(@"C:\ProgramData\Fadenstack\Office\policy.json"); // MachinePolicy.None when there is none
var agent = FadenstackAgent.CreateBuilder()
    .UseEndpoint(endpoint).UseLocalProfile(definition)
    .UseMachinePolicy(policy)   // Build() refuses what the policy forbids
    .Build();
```

## Keys

| Key | Values | Default |
|---|---|---|
| `server` | the organisation's server; the app enrols, and the person signs in at the next start | none |
| `standalone` | `allowed`, `forbidden` | `forbidden` when `server` is set, else `allowed` |
| `endpoints` | a list of allowed bases or origins (`https://llm.example`, `http://localhost:11434`) | any |
| `plainHttp` | `allowed`, `forbidden`: plain http in the local network (this computer stays allowed) | `allowed` |
| `mcp` | `allowed`, `forbidden`, or a list of allowed local MCP sources | like `standalone` |
| `skills` | `allowed`, `forbidden`, or a list of allowed skill folders | like `standalone` |
| `webSearch` | `allowed`, `forbidden` | like `standalone` |
| `chatRetention` | An object that fixes how long a standalone agent keeps old chats: `{ "autoDelete": true, "maxAgeDays": 30, "maxSessions": 100 }`. `autoDelete` is true or false (default true), `maxAgeDays` a whole number from 1 to 3650 (default 90), `maxSessions` from 1 to 10000 (default 50). The person sees the values in their settings and cannot change them. A governed agent ignores it: the server decides. | the person chooses |
| `auditRetentionDays` | A whole number from 30 to 3650: how many days entries of the person's [record of tool calls](audit-log.md#keeping-and-removing-it) are kept, in governed and standalone mode. A value out of bounds, or not a whole number, is reported in `policy.Warnings` and the default applies. It is `policy.AuditRetentionDays` (null when the key is absent) and `agent.AuditRetentionDays` says the number in force. | 180 |
| `timestamping` | `on` (DigiCert), `off`, or the address of the organisation's own time-stamp server: RFC 3161 time-stamps for records signed on the device (only a hash leaves); anything else is `off` | `on` |

## Rules

- **Fail closed.** An unknown key is ignored and reported in `policy.Warnings`; an unknown value narrows to `forbidden`; a
  file the SDK cannot parse or open (not JSON, not an object, no access, held by another process) forbids standalone, local
  MCP, local skills, web search and plain http, and turns time-stamps off. A warning names the key, never the value.
- **A source that fails:** a host that reads its policy from somewhere else (the registry, a managed store) and cannot
  read it uses `MachinePolicy.Unreadable`, not `MachinePolicy.None`. It forbids the same, fixes `chatRetention` at the
  defaults (old chats are deleted after 90 days, at most 50 kept), locks those keys, and leaves `server` open, so the
  person can still sign in to their organisation's server.
- **The policy wins over the person's settings**, and with a server it narrows what the server allows (a server's
  `local_mcp` allow is off when the policy forbids local MCP); it never turns on what they left off.
- **Enforced in the SDK:** `Build()` throws `MachinePolicyException` (with its `Key`) for an agent on an endpoint the
  policy forbids or does not list; with only listed endpoints, a host's own chat client is refused, since it names none.
  Plain http forbidden takes the endpoint's LAN opt-in away (`EndpointPlainHttpRefusedException`, `ForbiddenByPolicy`).
- **`chatRetention` is read as a whole:** a value that is not understood (not an object, `autoDelete` not true or false, a limit that is not a whole number within its bounds, an unknown member) is reported in `policy.Warnings` and the default for it applies (on, 90 days, 50 chats), so chats are never kept longer than the default because of a typo. It is `policy.ChatRetention` (a `ChatRetentionChoice`; null when the key is absent) and shows in `agent.ChatRetention` with `Control` = `MachinePolicy`. See [Sessions: Retention](sessions.md#retention).
- **Lists are the modules' to apply:** `agent.MachinePolicy` gives a module the listed MCP sources and skill folders.
- **Locked values:** `policy.Locked` names the keys the policy set; the app locks them in its UI and never writes them into
  the person's settings file.
- **Enrolment keeps data apart:** an agent on the person's own endpoint keeps its chats under `users/standalone/`, never
  uploaded, and its "Allow always" decisions do not carry over to the organisation's agent ([Data on the device](data-on-device.md#a-persons-folder)).

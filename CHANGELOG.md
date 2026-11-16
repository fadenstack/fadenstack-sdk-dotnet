# Changelog

All notable changes to the Fadenstack .NET SDK. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the packages follow [Semantic Versioning](https://semver.org/): before 1.0, a minor version may break the API, and
this file says where it does.

## [0.3.0] - Unreleased

### Added

- **Your data per app** ([Data on the device: Export and delete](docs/data-on-device.md#export-and-delete)). `DeleteMyDataAsync`
  and `ExportMyDataAsync` take a `DataScope`: `ThisApp` (the default) covers the calling app's chats, "Allow always" and
  thinking levels, and the person's shared data (skills, the MCP list, keys, provenance records, other settings) only when no
  other Fadenstack app on the device uses it; `AllApps` covers everything. `MyDataAppsAsync` lists the apps that use the
  person's data; each agent start records its app in `apps/<app>.json` (names and times, no content). **Behaviour change:**
  `DeleteMyDataAsync()` and `ExportMyDataAsync(stream)` without a scope used to cover every app; pass `DataScope.AllApps` for
  that.
  - **"Your data on this device"**: `MyDataSettingsViewModel` in `Fadenstack.UI` (`MyDataState`: idle, confirm this app, confirm
    all, busy, done, error) with `MyDataSettingsView` in `Fadenstack.UI.Wpf` and `.Avalonia`, and texts in five languages. "Remove
    my data…" asks first and says what goes and what stays, naming the other apps that keep the shared data; "Remove all my
    Fadenstack data on this device…" lists every app it affects; "Export my data…" and "Export all…" follow the same scopes. The
    host picks where an export goes through `IMyDataSaveTarget` (`WpfMyDataSaveTarget`, `AvaloniaMyDataSaveTarget`); the view model
    opens no dialog.
- **Old chats are a setting** ([Sessions: Retention](docs/sessions.md#retention)). In standalone mode the person chooses whether old
  chats are deleted automatically, and the limits (days 1 to 3650, chats 1 to 10000; defaults on, 90 days, 50 chats, or the app's
  `UseSessionRetention`); off keeps every chat until the person deletes it. In governed mode the server decides as before (the
  stricter of the app's limits and `local_retention`) and the person has no setting.
  - `FadenstackAgent.ChatRetention` (`ChatRetentionState`: `Control`, `AutoDelete`, `MaxAgeDays`, `MaxSessions`, `CanChange`),
    `SetChatRetentionAsync(ChatRetentionChoice)`; the choice is kept per person in `preferences/retention.json`.
  - The machine policy may fix it: `chatRetention` (`MachinePolicy.ChatRetention`); see [Machine policy](docs/machine-policy.md).
  - Pruning also runs when the agent starts and when the person turns it on or lowers a limit, never on a chat that is open.
  - `ChatRetentionSettingsViewModel` in `Fadenstack.UI`, with `ChatRetentionSettingsView` in `Fadenstack.UI.Wpf` and `.Avalonia`;
    texts in five languages.

- **Standalone mode** ([Standalone mode](docs/standalone.md)): an agent on the person's own OpenAI-compatible model
  endpoint, with no Fadenstack server. `AgentBuilder.UseEndpoint(ModelEndpoint)` or `UseChatClient(IChatClient)`, with
  `UseLocalProfile(AgentDefinition, ownInstructions)`; `Build()` refuses a server and a model together.
  - `AgentDefinition.FromJson`: the agent's definition as an app ships it; an unknown risk asks.
  - `ModelEndpoint`, `EndpointDialect` (OpenAI, Azure OpenAI, Ollama, LM Studio, vLLM, SGLang, llama.cpp, LiteLLM,
    OpenRouter, Mistral), `EndpointPreset`, and `DirectChatClient`, the endpoint as an `IChatClient`.
  - `EndpointCheck` and its serialisable `EndpointReport`: reachable, the key, the models, usage figures, tool calls,
    vision, the context length, and the text watermark status from a sourced table (`TextWatermark`, `EndpointWatermarkStatus`);
    `FindLocalAsync` looks for local servers only with the person's consent.
  - Typed errors: `EndpointKeyRejectedException`, `EndpointOriginRefusedException`, `EndpointModelNotFoundException`,
    `EndpointContextLengthException`, `EndpointNotCompatibleException`, `EndpointUnreachableException`,
    `ServerCertificateException`, `EndpointPlainHttpRefusedException`.
  - `EndpointMarking` on each endpoint answer (`FadenstackChatOptions.EndpointMarking`).
  - `FadenstackAgent.EndpointUrl`, `AgentContext.ServerClient` and `AgentContext.Connection`, `LocalProfileOptions`.
  - `AgentProfile.LocalSkills` reads the profile's `local_skills` (a Fadenstack server from 0.7.1).
  - `ContextBuilder.Build(entries, reservedTokens)`: room in the history budget for what goes with every request.
- **The agent is a Microsoft Agent Framework `AIAgent`** (`Microsoft.Agents.AI.Abstractions` 1.28): MAF's `RunAsync`,
  `RunStreamingAsync`, sessions (`FadenstackAgentSession`, which serializes only the session's id) and workflows run it as a
  turn of a `ChatSession`. A run needs an approval handler. `Microsoft.Extensions.AI` is 10.11.
- **Records signed on the device** (`Fadenstack.Provenance`): `LocalProvenanceBackend` for an agent without a server
  (chosen by the agent's connection), with `DeviceSigner` (the engine's JWS format, ES256, a key per installation kept by
  the OS key store), RFC 3161 time-stamps (`LocalProvenanceOptions`, `ProvenanceOptions.UseLocalBackend`) and a local
  placement log that rotates. DigiCert by default, with a local backup: every record kept on the device with its token,
  the labelled device clock and a queue that survives a restart when offline (`KeptRecords`, `FlushTimestampQueueAsync`); a
  token counts only when its CMS signature, a time-stamping certificate that chains to a trusted root, and the nonce
  check out (`TimestampRoots` for an organisation's own server).
- **The endpoint's text watermark** (part of D18): never switched off for text people read; `FadenstackChatOptions.Unread`
  for a call nobody reads, where vLLM and SGLang may leave it off (`EndpointDialect.WatermarkSwitchOff`); each answer's
  transcript entry records `endpoint_marking`; `FadenstackAgent.EndpointWatermark`; `EndpointCheck.WatermarkOf` and
  `ServerOf`; the provenance module's watermark layer relies on the endpoint (`endpoint-<server>`) and never claims more.
- **`MachinePolicy`** ([Machine policy](docs/machine-policy.md)): one vocabulary for managed installs (`server`,
  `standalone`, `endpoints`, `plainHttp`, `mcp`, `skills`, `webSearch`, `timestamping`), read fail-closed, enforced by
  `AgentBuilder.UseMachinePolicy` (`MachinePolicyException`), narrowing local MCP and skills with a server too.
- **Typed sign-in errors** (for the connection wizard): `FadenstackUnreachableException`, `ServerCertificateException`,
  `FadenstackPairingPageElsewhereException`, `FadenstackHttpsRequiredException` (an `ArgumentException`),
  `FadenstackBadCredentialsException`; and `PasswordLogin.LoginAsync`, the password sign-in of a server without pairing
  (TypeScript has `loginWithPassword`).
- **The connection wizard's view model** (`Fadenstack.UI.Wizard`, sdk/51 F15, I15 step 2):
  `ConnectionWizardViewModel`, one state machine for "Connect to my organisation" (the address rule, the server check,
  pairing with one approval for all of the app's agents, the password fallback) and "Use my own AI" (the acceptance and
  its record, the local search only with consent, presets and a typed address, plain http to the local network only after
  confirming and never with a key, the key straight into the key store, the check, the model), and for Settings ›
  Connection. The app gives `IConnectionStore`, `IEndpointKeyStore`, its own `IWizardStep`s and its `WizardOptions`; the
  machine policy shapes the choices. `ServerSignIn` and `EndpointChecker` drive the SDK; `ServerCheck.CheckAsync` checks a
  server's `/api/health`; `EndpointCheck.PlaceOfAsync` tells this computer, the local network and the internet apart on the
  resolved address. The skins and the texts follow.
  `WizardOptions.TrustedCaCertificate`: the app's CA reaches every endpoint the wizard checks and builds
  (`ModelEndpoint.TrustedCaCertificate`), as it does the server; the wizard offers no CA trust in its UI.
- `Fadenstack.Agents.Delegation`, for applications: one agent hands a task to another on the same device
  (`DelegationHost`, `LocalAgentDelegate`, the model's `delegate` tool), A2A 1.0 over a local pipe that only the current
  OS user can open. It is not on nuget.org: it is on the fadenstack organisation's GitHub Packages feed with the A2A fork
  it needs (a2aproject/a2a-dotnet issue #197). While A2A is a preview, a stable SDK version has it as a preview
  (0.3.0-preview). See [Delegating to agents](docs/delegation.md).
- `ITokenStoreLock`: a token store lets one holder at a time renew a sign-in. `FileTokenStore` (a lock file, across
  processes) and `MemoryTokenStore` implement it, and `AgentTokenProvider` renews under it.
- `FadenstackClientOptions.StreamIdleTimeout` (5 minutes): a streamed answer that sends nothing at all for that long is
  given up with a `FadenstackException`.
- `AgentTokenProvider` takes an optional `trustedCaCertificate`, for the client it builds when none is given.
- `FileTokenStore`, `FileApprovalStore` and `FileSessionStore` take an optional `logger` for what they cannot read
  (the kind of failure only).
- `ProvenanceTexts`: the words content marks show people, owned by `Fadenstack.Provenance` in English, German, French,
  Spanish and Simplified Chinese.
- `FadenstackAgent.Connection` (`AgentConnection.Governed` or `Standalone`), `FadenstackAgent.ServerClient` (null without
  a server) and `FadenstackAgent.ListServerToolsAsync()` (null on a server that does not list its tools). `Client`
  stays non-null with a server and throws `InvalidOperationException` without one.
- `AboutInfo.Create` with an `AgentConnection`, and `UiStrings.AboutEndpoint`: About names the person's model endpoint
  for an agent without a server.

### Changed

- `EndpointCheck` probes with the first model `/models` lists when the endpoint names none, and its report names it (the
  wizard checks before the person picks a model).
- **Breaking (unlikely to matter):** `find_tool` is the SDK's own name, `AgentTool.ToolSearchName`: `AddTool` and `AddTools`
  refuse an app's tool of that name, and an app's tool source that brings one has it left out (log event 2016). A
  Fadenstack gateway from 0.7.1 takes `find_tool` as read, so the local MCP module's search no longer asks under a server;
  the tools it finds still ask on every call. Only the SDK's own sources bring it.
- **Breaking:** four more tool names are the SDK's own, for its coming tools: `load_skill`, `read_skill_resource`,
  `run_skill_script` (never offered) and `web_search`; all five are in `AgentTool.ReservedNames`. An app's tool of one
  of these names is refused as `find_tool` is (`ArgumentException`); rename it. An app's tool source, or a local MCP
  server, that brings one has it left out (log events 2016 and 3007). A Fadenstack gateway gives these names the class
  of the SDK's tool, so an app's tool under one would be taken for it.
- A tool source's tool that loses its name to the app's own tool or to an earlier source is still left out, and is now
  logged with what won (event 2017). Between two local MCP servers the first one's tool is kept, as before, and the
  other is logged by the servers' labels (event 3008); `find_tool` no longer finds the second one.

- **Open-world calls ask below Auto** (`ToolModes.OpenWorldEnforced` is now true). In `ReadOnly` and `Ask`, a call to a
  tool marked `OpenWorld` (or a local MCP tool with `openWorldHint: true`, or one the gateway marks `openWorld`) asks,
  a read one too; `Auto` and `Bypass` run it. Its card offers *Allow for this chat* at most: *Allow always* and time
  grants are not offered below `Auto`, and an "always" saved before no longer answers it. `ToolModes.Decide` takes
  the mark (`openWorld`); `ToolModes.ConversationOnly` says when the rule applies. What such a tool is given leaves the
  organisation, and injected text can put private data into it.

- **Breaking:** `AgentSession` is now `ChatSession`. The Microsoft Agent Framework has an `AgentSession` of its own, and
  the agent becomes an `AIAgent` in this release; only the name changes.
- **Breaking:** `MarkableContent.Texts`, `ProvenanceOptions.UseTexts` and `HtmlAttributeMarker.Title` take
  `ProvenanceTexts` instead of `UiStrings`. `Fadenstack.Provenance` no longer references `Fadenstack.UI`.
- **Breaking:** a typed answer (`FadenstackTransport.GetAsync<T>` and the other typed calls) that is empty or JSON
  `null` throws a `FadenstackException` naming the route, instead of returning `null`. `bool` still means "no body".
- **Breaking:** the SDK's HTTP handler (`FadenstackTransport.CreateHandler`) follows no redirects. A 3xx answer is a
  `FadenstackApiException` with the code `redirect`, whose message names where it pointed.
- `AgentPairing.WaitAsync`: in a pairing for several agents, a first agent the person did not approve is reported with
  `FadenstackPairingAgentException` (it was "expired"); the first approved other agent is signed in on the way.
- The request timeout (`FadenstackClientOptions.Timeout`) also covers a stream's wait for its headers.
- **Breaking (unlikely to matter):** `ProvenanceOptions.Backend` is null unless the app set one; the module then picks the
  server's or, without a server, the device's backend.
- **Errors are more specific** (each a subclass of what was thrown before, so a `catch` of the old type still works; a
  test that asserts the exact old type sees the new one): no answer from the server is `FadenstackUnreachableException`,
  or `ServerCertificateException` when TLS refused the certificate; a sign-in page on another address is
  `FadenstackPairingPageElsewhereException`; plain http where https is required is `FadenstackHttpsRequiredException`.
- `AgentContext.Client` throws `InvalidOperationException` for an agent without a server (it is unchanged with one);
  `AgentContext.ServerClient` is the way to ask.
- A stream whose data is not JSON breaks off with a `FadenstackStreamException` (code `invalid_stream`) instead of a
  parser's exception. Streamed tool calls without an `index`, or a new call id under an index already used, are kept
  apart instead of merged into one call.

### Removed

- **Breaking:** `UiStrings.AiGenerated` and `UiStrings.AiGeneratedTitle` (now `ProvenanceTexts`).

### Fixed

- **A local MCP server on 2026-07-28 only attaches when it is slow to start.** The MCP SDK's 5-second probe for that revision
  ran out while `uvx` was still fetching the server, the client took it for an older server, sent `initialize`, and was
  refused. The client now asks once more with 2026-07-28 pinned; 2025-11-25 servers still attach by the SDK's fallback.
  `McpServerStatus.ProtocolVersion` says which revision a server runs on.
- **An approval answer lost on the way is sent once more.** Answering a call the gateway holds now waits 10 seconds for a
  word back, then sends the same answer once more when none came (no answer, the server unreachable, a proxy's 502, 503 or
  504). A Fadenstack gateway from 0.7.1 (`approvals.idempotent`) takes a repeated answer once; an older one answers the
  repeat of a taken answer 404, which now counts as taken. A 409 is never sent again. Before, a lost decline left the call
  waiting until the gateway's own timeout (5 minutes). Log event 2053.
- **A chat no longer loses its own grant to another chat's refresh.** The device's copy of grants took the server's list
  (or a stream's `faden.grants`) as it was when it was asked for; a grant made meanwhile in another chat, a Bypass
  started in parallel, was dropped from the copy, so that chat ran without it and its Stop could not end it at the
  server. The copy now keeps what the server answered this device after the list or snapshot was asked for.
- Two processes that keep the same agent sign-in no longer sign each other out by renewing it at the same time.
- A kept sign-in, stored approvals or a saved chat that cannot be read no longer throws: it counts as nothing kept, and a
  chat line that cannot be read costs only that line.
- Pairing works on macOS and Linux when the server names its sign-in page as a path.
- Where `libc` cannot be loaded, a grant's path check asks the person instead of throwing.
- On .NET Framework, a response without content (a bare 204 from a host's own handler) no longer throws a
  `NullReferenceException`: it reads as an empty body, as on .NET 8.

### Security

- A model endpoint (standalone mode) is reached under transport rules: https, or plain http only to this computer or, with
  the person's opt-in, the local network; a key over plain http only to this computer, decided by the address (resolved
  once and kept), never the name; no redirect to another origin; limits on what a
  stream may send, so an endpoint cannot exhaust the device's memory (OWASP ASVS 5.0 V12, V13).
- An endpoint cannot speak the gateway's protocol: its named events and its answer's `faden` block are not read, so it
  cannot forge a refusal, an approval or a provenance receipt.
- No request follows a redirect, so a 307 or 308 can no longer send a request's body (chat text, a refresh token, a
  pairing code) to another address (OWASP ASVS 5.0 V12).
- Files that hold secrets are written owner-only (0600) before anything goes into them, replaced atomically, and not
  written at all when the mode cannot be set (OWASP ASVS 5.0 V14).

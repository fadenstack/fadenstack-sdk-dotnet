# Parity with the TypeScript SDK

This page lists each feature of the SDK with its name in the .NET SDK and in the [TypeScript SDK](../typescript/index.md), and where a browser does it differently. The two SDKs follow one design: the same concepts, the same tool classes and modes, the same transcript format and the same wire. TypeScript uses camelCase and drops C#'s `Async` suffix; durations in the grant API are milliseconds there and `TimeSpan` here.

This table is for SDK 0.3.0, against Fadenstack server 0.7.1. A dash means the SDK has nothing of that name. The last column is empty where the two work alike.

## Packages

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| Server connection | `Fadenstack.Client` | `@fadenstack/client` | |
| The agent | `Fadenstack.Agents` | `@fadenstack/agents` | |
| View models of the chat | `Fadenstack.UI` | `@fadenstack/ui` | |
| The skin of the chat | `Fadenstack.UI.Wpf`, `Fadenstack.UI.Avalonia` | `@fadenstack/ui-web` (the look), `@fadenstack/ui-react`, `@fadenstack/ui-vue` | |
| Local MCP servers | `Fadenstack.Mcp` | `@fadenstack/mcp` | A browser starts no program: over HTTP only, no stdio, no scan ([Local MCP servers](../typescript/local-mcp.md)). |
| Delegation between agents on one device | `Fadenstack.Agents.Delegation` (a preview, on the fadenstack organisation's GitHub Packages feed, not on nuget.org: [Delegating to agents](delegation.md)) | — | A browser has no local pipe. A TypeScript A2A client comes later. |
| Content provenance | `Fadenstack.Provenance` | `@fadenstack/provenance` | |
| Encryption at rest on macOS and Linux | `Fadenstack.Secrets` | — | A browser encrypts its stores with a WebCrypto key ([Stores](#stores-and-data-rights)). |
| Test helpers | Seams and the provenance kit ([Testing](testing.md)) | `@fadenstack/testing` (`FakeGateway`) | |
| Content marking | `Fadenstack.Provenance` | `@fadenstack/provenance` | |

## Signing in

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| A client for one agent | `FadenstackClient.ForAgent(serverUrl, agent, out tokens)` | `FadenstackClient.forAgent({ serverUrl, agent })` | |
| Sign in with a login token | `tokens.SignInAsync(loginToken, deviceLabel)` | `tokens.signIn(loginToken, deviceLabel)` | |
| A login token from a password | `PasswordLogin.LoginAsync` | `loginWithPassword({ apiUrl, email, password })` | |
| Sign in by pairing | `AgentPairing`: `StartAsync`, `WaitAsync`, `RedeemAsync` | `AgentPairing`: `start`, `wait`, `redeem` | |
| Several agents with one approval | `StartAsync(..., agents)`; at most `AgentPairing.MaxAgents` (8); agents an old server left out in `PairingStart.AgentsLeft` | `start(..., { agents })`; agents an old server left out in `PairingStart.ignoredAgents` | |
| A server too old for pairing | `FadenstackPairingUnsupportedException` (404 and 405) | `FadenstackPairingUnsupportedError` (404 and 405) | |
| Denied, expired, agent not redeemable | `FadenstackPairingDeniedException`, `FadenstackPairingExpiredException`, `FadenstackPairingAgentException` | `FadenstackPairingDeniedError`, `FadenstackPairingExpiredError`, `FadenstackPairingAgentError` | |
| The sign-in page must be the server's own | `StartAsync` throws `FadenstackPairingPageElsewhereException` | `start` throws `FadenstackPairingPageElsewhereError` | |
| Is it a Fadenstack server (the wizard's Checking) | `ServerCheck.CheckAsync(server, http, allowInsecureHttp, trustedCaCertificate)`, `ServerCheck.Timeout` | `checkServer({ server, fetch, signal, allowInsecureHttp })`, `SERVER_CHECK_TIMEOUT_MS` | No `trustedCaCertificate`: the platform's trust decides. |
| The connection wizard's view model | `Fadenstack.UI.Wizard.ConnectionWizardViewModel` (commands as `RelayCommand`s), `WizardOptions`, `IConnectionStore`, `IEndpointKeyStore`, `IWizardStep`, `IServerSignIn`, `IEndpointChecker`, `IWizardClock`, `ServerSignIn`, `EndpointChecker`, `SystemWizardClock` | `ConnectionWizardViewModel` (commands as methods, availability in `can`, change events through `subscribe`/`onCompleted`) and its helpers | See [Connecting an agent](connecting.md). |
| Plain chat completions | `FadenstackClient` without an agent ([Without an agent](plain-api.md)) | `FadenstackClient` without an agent | |
| Where the sign-in is kept | `ITokenStore`: `FileTokenStore`, `MemoryTokenStore` | `TokenStore`: `MemoryTokenStore` | A browser extension keeps it in `chrome.storage` with a store of its own ([Build an agent](../typescript/agent.md)). |

## The agent

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| The builder | `FadenstackAgent.CreateBuilder()...Build()` | `FadenstackAgent.createBuilder()...build()` | |
| Host tools | `[AgentTool(ToolClass.Read)]` methods, `AddTools`, `AddTool` | `defineTool({...})`, `addTools` | |
| Tool classes and modes | `ToolClass`, `ToolMode` | `ToolClass`, `ToolMode` (`off`, `read_only`, `ask`, `auto`, `bypass`) | |
| Approvals | `UseApprovals`, `ApprovalDecision.Approve()`, `ApproveForSession()`, `ApproveAlways()`, `Deny()` | `useApprovals`, `ApprovalDecision` | |
| The request names its session | `ApprovalRequest.SessionId` | `ApprovalRequest.sessionId` | |
| Sessions, streaming a turn | `StartSessionAsync`, `OpenSessionAsync`, `StreamAsync`, `SendAsync` | `startSession`, `openSession`, `stream`, `send` | |
| A turn | `session.StreamAsync(message)`, `session.SendAsync(message)` | `session.stream(message)`, `session.send(message)` | |
| Stopping a turn | A `CancellationToken` | An `AbortSignal` | |
| Attachments, pictures, skills | `MessageAttachment`, `SendOptions` | `MessageAttachment`, `SendOptions` | |
| Model rounds, history budget | `UseMaxRounds`, `UseHistoryBudget` | `useMaxRounds`, `useHistoryBudget` | |
| Reporting to the server | `UseEventReporting`, `DescribeApp` | `useEventReporting`, `describeApp` | |
| Outcomes, notices and errors | `AgentResult`, `FadenstackException` and its subclasses ([Outcomes and errors](outcomes.md)) | `AgentResult`, `FadenstackError` and its subclasses | |
| Events of a turn | `TextDelta`, `ToolCallStarted`, `TurnCompleted`... | `{ type: "text" }`, `{ type: "tool_started" }`, `{ type: "turn_completed" }`... | |
| SDK ids for empty or repeated call ids | `fs-<turn id>-<round>-<index>` | the same | |
| How long the agent thinks | `UseReasoning(ReasoningEffort)`, `agent.ReasoningChoices`, `agent.SetReasoningAsync(ReasoningLevel)` | `useReasoning(effort)`, `agent.reasoningChoices`, `agent.setReasoning(level)` | |
| The server's cap, `ui.reasoning.max` | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`; `minimal` caps at `none`, `max` sets no cap | the same scale and the same rules | `ReasoningEffort` has no `minimal`; TypeScript's `useReasoning` accepts it. |
| Skills | `agent.ListSkillsAsync(query)`, `SendOptions.Skills`; `Fadenstack.Skills`: `SkillsModule`, `SkillsOptions` (folders, bundled skills), `ILocalSkillsPolicy` ([Skills](skills.md)) | `agent.listSkills(query)`, `skills` in the send options; `@fadenstack/skills`: `skillsModule`, `SkillsOptions` (bundled skills, a store), `LocalSkillsPolicy` | The person's own skills are in IndexedDB per principal, not in folders. |
| The user's language | `agent.SetUserLanguageAsync(tag)` | `agent.setUserLanguage(tag)` | |
| The organisation's tools | `agent.ListServerToolsAsync()` returns `AgentToolInfo`, or `null` on an old server | `client.agent.listTools()` | |
| Tool sources that change | `IChangingToolSource` and its `Changed` event | `ChangingToolSource` and `onChanged` | |
| Tools in scope per conversation | `AgentTool.Scope`, `IToolScope` | `ToolScope` (`offers`, `beforeCall`) | |
| Tools on request, `find_tool` | `LocalMcpModule` (`LocalMcpModule.FindToolName`, `MaxFound` 5, `MaxToolsInScope` 10) | `OnRequestTools` (`FIND_TOOL_NAME`, `MAX_FOUND`, `MAX_TOOLS_IN_SCOPE`) over the host's own tools | A browser has no local MCP process; the same seam works over the host's tools. |
| The SDK's own tool names (`find_tool`, `load_skill`, `read_skill_resource`, `run_skill_script`, `web_search`) | `AgentTool.ReservedNames`; refused by `AddTool`/`AddTools` (`ArgumentException`); an app's source's left out (event 2016) | `RESERVED_TOOL_NAMES`; refused by `addTools` and `OnRequestTools` (`TypeError`); an app's source's left out (logged) | |
| A tool source's tool whose name is taken | Left out (the app's tool wins, then the first source) and logged (event 2017) | The same, logged | |
| Open-world calls ask below Auto | `ToolModes.Decide(mode, class, openWorld)`, `ToolModes.ConversationOnly`, `OpenWorldEnforced` true | `decideTool(mode, class, approval, openWorld)`, `conversationOnly`, `OPEN_WORLD_ENFORCED` true | |
| Tools that make pictures; image generation off in core 0.7.1 ([Tools that make pictures](tools.md#tools-that-make-pictures)) | `AgentToolAttribute.MakesPictures`, `AgentTool.MakesPictures`, `FadenstackAgent.ImagesAvailable`, `FadenstackApiException.IsImagesDisabled`, `ErrorCodes.ImagesDisabled`, notice `images_disabled` | `ToolSpec.makesPictures`, `AgentTool.makesPictures`, `agent.imagesAvailable`, `FadenstackApiError.isImagesDisabled`, `isImagesDisabled`, `ErrorCodes.imagesDisabled`, notice `images_disabled` | The TypeScript client has no images call: an app asks the route itself |
| The web search tool | `WebSearchModule`, `WebSearchOptions`; `WebSearchStore` in the person's folder ([Pictures and web search](web-search.md)) | `webSearchModule`, `WebSearchOptions`; `IndexedDbWebSearchStore`, `MemoryWebSearchStore` | The host needs permission for the provider's origin; the Chrome agent keeps the browser's own search |
| Approvals keyed by source, sealed | `StoredApproval.Source`, `Server`, `Covers`; `FileApprovalStore` seals each with an HMAC | `StoredApproval.source`, `server`, `approvalCovers`; IndexedDB seals each with the person's WebCrypto key | |
| The approval floor (the never-skip list) | `ApprovalFloor` (`NeverSkipped`, `NotMarkedYet`, one constant per item, `DestructiveOrUnclassified`); [Approvals](approvals.md) | `ApprovalFloor` (`neverSkipped`, `notMarkedYet`, one key per item, `destructiveOrUnclassified`) | An MCP server's question: .NET passes it to the app (`LocalMcpModule.Ask`); the browser's MCP client declares no elicitation, so a server cannot ask. |
| A reason for declining | `ApprovalDecision.Deny(reason)`, `MaxReasonChars`, `CleanReason`; the transcript's `reason` | `ApprovalDecision.deny(reason)`, `MAX_REASON_CHARS`, `cleanReason`; the transcript's `reason` | |
| AG-UI events | `AgUiEvents.From(events, options)`, `AgUiJson` ([Interop](interop.md)) | `agUiEvents(events, options)`, `agUiJson`; our own types following AG-UI 1.0.4 | |
| Hooks | `IAgentHooks`, `AgentHooks`, `AddHooks(hooks, order)`, `ToolHookVerdict` | `AgentHooks`, `addHooks(hooks, order)`, `toolHook` | |
| OpenTelemetry | `UseOpenTelemetry(...)`, opt-in | Later | |
| Host commands | `session.InvokeToolAsync`, `session.StreamToolAsync` | `session.invokeTool`, `session.streamTool` | |
| Fresh policy for host calls | `RequireHostToolPolicyChecks()` | `requireHostToolPolicyChecks()` | |
| Modules | `IFadenstackModule`, `Use` | `FadenstackModule`, `use` | |
| Required client modules | `required_client_modules` | `required_client_modules` | |
| Stages and modules | `AddChatStage`, `AddToolStage`, `Use(module)` | `addChatStage`, `addToolStage`, `use(module)` | |
| Tool services | `AddToolService<T>(factory)`, `GetService<T>()` | `addToolService(key, factory)`, `context.services.get(key)` | A service has a typed key (`toolServiceKey`), not a type. |
| The client's transport | `client.Transport`, `FadenstackTransport` | `client.transport`, `FadenstackTransport` | |
| Dependency injection | `AddFadenstackAgent` | — | |
| Device agent registry | `DeviceAgentRegistry` in `Fadenstack.Agents.Registry` | — | |

## Grants and Bypass

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| Grants the server holds | `agent.Grants` (`AgentGrants`) | `agent.grants` | |
| A grant for a time | `ApprovalDecision.ApproveFor(duration)` | `ApprovalDecision.approveFor()` | |
| A grant for a stretch of work | `session.OfferWorkAsync(places)`, `GrantPlace.Document`, `Folder`, `Site`, `App`, `Desk` | `session.offerWork(places)`, `GrantPlace.document/folder/site/app/desk` | The SDK resolves paths itself; in a browser the host supplies `useGrantPlaceResolver`. |
| Bypass | `session.StartBypassAsync()`, `StopBypassAsync()`, `UsePermissionMode(PermissionMode.Bypass)` | `session.startBypass()`, `stopBypass()`, `usePermissionMode("bypass")` | |
| The second sign-in | `UseStepUpHandler`, `IStepUpHandler` | `useStepUpHandler`, `browserStepUp()` | The page opens in the system browser or a new window, never in an iframe. |
| Keeping a grant from running unattended | `GrantLifecycle` | `GrantLifecycle` | A page feeds it from `pagehide` and `visibilitychange`; a tab that only loses focus is not a locked screen. |

## Stores and data rights

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| Session store | `ISessionStore`: `FileSessionStore`, `MemorySessionStore` | `IndexedDbSessionStore`, `MemorySessionStore` | A browser keeps sessions in IndexedDB; Node keeps them in memory. |
| Stores per signed-in user | `users\<user>\sessions` and `approvals` in the data folder | the databases `fadenstack-sessions:<tenant>:<user>` and `fadenstack-approvals:<tenant>:<user>` | |
| Everything kept on the device | [Data on the device](data-on-device.md): a folder per person (skills included), and shared folders for tokens, the key store, the agent registry, leases and logs | [Data on the device](../typescript/data-on-device.md): eight IndexedDB databases per person, and two `localStorage` helpers | A browser has no shared folders: the sign-in, the endpoint's key and logs are the app's. |
| Encryption | `ISecretProtector`: DPAPI, or the OS key store with `FadenstackSecrets.UseOsKeyStore()`; without either, transcripts stay in memory | `SecretProtector`, `useSecretProtector`: a non-extractable WebCrypto key per user | There is no OS key store in a browser. |
| Retention | `UseSessionRetention(SessionRetention)`; the server's `local_retention` can only lower it | `useSessionRetention({ maxSessions, maxAgeDays })`; the same rule | |
| The record of tool calls | `agent.VerifyAuditAsync()`, `ExportAuditAsync(AuditExportFormat, stream)`, `AuditRetentionDays`; `UseAuditLog(IAuditLog)`: `FileAuditLog`, `MemoryAuditLog` ([The record of tool calls](audit-log.md)) | `agent.verifyAudit()`, `exportAudit("csv" \| "html")` (text), `auditRetentionDays`; `useAuditLog(log)`: `IndexedDbAuditLog`, `MemoryAuditLog` | The same entries, hash chain, seal, test vectors and texts; a file per person here, an IndexedDB database per person there. |
| Fencing content you did not write | `UntrustedContent.Fence(label, origin, body)`, `UntrustedContent.Clean(text)` ([Tools](tools.md)) | `fenceUntrusted({ label, origin, body })`, `cleanUntrusted(text)` | |
| The injection suite | `tests/Fadenstack.Agents.Tests/Injection`, trait `Category=Injection` | `packages/agents/test/injection` | The same corpus, byte for byte; TS has no delegation channel. |
| Content logging for a time | `agent.ContentLogging`, `StartContentLogging(TimeSpan)`, `StopContentLogging()`, `ContentLoggingSettingsViewModel`, the band in the chat; machine policy `contentLogging` | `agent.contentLogging`, `startContentLogging(ms)`, `stopContentLogging()`, `ContentLoggingSettingsViewModel`, the band in the chat; machine policy `contentLogging` | TS has no OpenTelemetry chat client: only the hooks follow the switch. |
| The mark on copy | `agent.MarkCopyAsync(AnswerCopy)` (`MarkedCopy`), `IAnswerCopyMarker`, `IHtmlClipboard` | `agent.markCopy({ text, html, chat, turn })`, `AnswerCopyMarker`, `Clipboard.setContent` | .NET has no chat export; TS's saved chat is marked in its front matter. |
| A local MCP server's program pinned | `McpServerEntry.ProgramPin`, `McpHost.ConfirmNewProgramAsync`, the Tools view's "Use the new program" | — | A browser starts no program: an MCP server over http has nothing to pin. |
| What the app reports about the device (manifest `device`) | `DescribePlugins`, `ManifestDevice`, `ManifestPlugin`, `ManifestMcpProgram`, `DeviceLimits` | `describePlugins`, `ManifestDevice`, `ManifestPlugin`, `ManifestMcpProgram`, `DEVICE_LIMITS` | `os` is the runtime's (TS: the browser's platform or Node's); TS never lists MCP programs: a browser starts none. |
| The hint rule and the person's servers | `McpServerStore` in `Fadenstack.Mcp` (a file); [Local MCP servers](local-mcp.md#the-class-of-a-tool) | `toolClassFor`; `McpServerStore` (`IndexedDbMcpServerStore`: IndexedDB per principal, header values sealed with the principal's WebCrypto key, deleted by `deleteMyData`); `parseMcpConfig` and `McpModule.importConfig` | A tool arrives unclassified; `destructiveHint: true` from any server makes it destructive; a `readOnlyHint` counts only from a server the person (standalone) or the administrator (governed) marked trusted; only they raise a tool to read or write; destructive stays a floor. Stdio entries are listed as not possible in a browser. Plain http only to this computer. |
| Per-app data rights | `DataScope.ThisApp` (default) or `AllApps`; `MyDataAppsAsync()`; the app record `apps/<app>.json`; the approvals' seal key goes with the last approvals | `scope: "thisApp"` (default) or `"allApps"`; `myDataApps()`; the app record in `fadenstack-apps`; no seal key (approvals are sealed under the person's data key, which goes only when no other app uses the data) | In a browser the texts say "in this browser", not "on this device". |
| The "Your data" control | `MyDataSettingsViewModel` in `Fadenstack.UI`, with views in WPF and Avalonia | `MyDataSettingsViewModel` in `@fadenstack/ui`, with `MyDataSettings` in `@fadenstack/ui-react` and `@fadenstack/ui-vue` | |
| Export my data | `agent.ExportMyDataAsync(stream, DataScope)` writes a ZIP | `agent.exportMyData({ scope })` returns a `MyDataExport` object | A page hands the person a document to download. |
| Delete my data | `agent.DeleteMyDataAsync(DataScope)` returns `DataDeletion` (`FoldersRemoved`, `HostSessionStoreLeft`, `HostApprovalStoreLeft`, `HostStoresLeft`, `Scope`, `OtherApps`, `SharedDataKept`, `AgentsKept`) | `agent.deleteMyData({ scope })` returns `storesCleared`, `hostSessionStoreLeft`, `hostApprovalStoreLeft`, `hostStoresLeft`, `scope`, `otherApps`, `sharedDataKept`, `agentsKept` | It deletes the IndexedDB databases and their key. |
| Allow always | `IApprovalStore`: `FileApprovalStore`, `MemoryApprovalStore`; `agent.ListApprovalsAsync()`, `ResetApprovalsAsync()` | `agent.listApprovals()`, `resetApprovals()` | |

## Standalone mode

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| The person's own endpoint | `UseEndpoint(ModelEndpoint, EndpointReport?)` | `useEndpoint(endpoint, report?)` | |
| Any chat client as the model | `UseChatClient(IChatClient)` | `useChatStreamer(streamer)` | |
| The agent's definition | `UseLocalProfile(AgentDefinition.FromJson(json), ownInstructions, LocalProfileOptions?)` | `useLocalProfile(AgentDefinition.fromJson(json), ownInstructions?, options?)` | |
| The agent as a Microsoft Agent Framework `AIAgent` | `FadenstackAgent : AIAgent`; `FadenstackAgentSession`; `RunAsync`, `RunStreamingAsync`, workflows | — | The Agent Framework is .NET's. |
| The machine policy | `MachinePolicy.Load(path)`, `FromJson`, `From(JsonElement)`; `UseMachinePolicy(policy)`; `MachinePolicyException` | `MachinePolicy.from(object)`, `fromJson`; `useMachinePolicy(policy)`; `MachinePolicyError` | An extension reads `chrome.storage.managed`. |
| How the agent is connected | `agent.Connection`, `agent.ServerClient`, `agent.EndpointUrl`; `context.Connection`, `context.ServerClient` | `agent.connection`, `agent.serverClient`, `agent.endpointUrl` | |
| The endpoint and its dialect | `ModelEndpoint`, `EndpointDialect`, `EndpointPreset` | the same names | |
| The endpoint as a chat client | `DirectChatClient : IChatClient` | `DirectChatClient`, a `ChatStreamer` | |
| The endpoint check | `new EndpointCheck(endpoint).RunAsync(options)` | `new EndpointCheck(endpoint).run(options)` | |
| Its report | `EndpointReport`, `ToJson()`, `FromJson(json)`; the same JSON in both | `EndpointReport`, `toJson()`, `fromJson(json)` | |
| Looking for local servers | `EndpointCheck.FindLocalAsync(personConsented, ...)` | — | A page cannot scan the computer's ports: the check covers only the configured origin. |
| The text watermark status | `report.Watermark` (`TextWatermark`, `EndpointWatermarkStatus`); `EndpointMarking` on each answer | `report.watermark`; `endpointMarking` on each answer | |
| Endpoint watermarks (no server) | `FadenstackChatOptions.Unread` (`faden.unread`), `EndpointDialect.WatermarkSwitchOff`, `FadenstackAgent.EndpointWatermark`, `EndpointCheck.WatermarkOf`/`ServerOf` | `ChatOptions.unread`, `EndpointDialect.watermarkSwitchOff`, `agent.endpointWatermark`, `EndpointCheck.watermarkOf`/`serverOf` | `unread` is a plain option: TypeScript has no `AdditionalProperties` bag. |
| Where an endpoint points | `EndpointCheck.PlaceOfAsync(baseUrl)` (resolves the name), `EndpointPlace` | `EndpointCheck.placeOf(url)`, `EndpointPlace` | Synchronous, by the host's spelling: a browser cannot resolve names. |
| Records signed on the device (no server) | `LocalProvenanceBackend`, `DeviceSigner`, `UseLocalBackend`: the key in the OS key store, the log a rotating file, DigiCert by default ([Content provenance](provenance.md#without-a-server-records-signed-on-the-device)) | `LocalProvenanceBackend`, `DeviceSigner`, `localBackend`: a non-extractable WebCrypto key, the log and the kept records in IndexedDB per principal, DigiCert by default | A browser has no key store, files or certificate store, and `fetch` cannot reach an http-only authority from an https page (the records wait in the queue). The log keeps a number of entries, not bytes. |
| Plain http in the local network | `ModelEndpoint.AllowPlainHttpInLan`; the addresses a name resolves to decide | `allowPlainHttpInLan`; a name decides by its form (one label, `.local`, `.lan`, `.home.arpa`, `.internal`) | A page cannot resolve names. |
| Redirects | one to the same origin is followed (at most three); any other is refused | the same in Node | A browser hides where a redirect points: none is followed, the typed `redirect` error results. |
| The skill editor | `Fadenstack.UI.Skills` (`SkillLibraryViewModel`, `SkillEditorViewModel`, `SkillDraftViewModel`), `ISkillWorkshop`, `SkillWorkshop` in `Fadenstack.Skills` over the person's folders; import of a folder or a zip, export as a zip ([Skills](skills.md#the-editor)) | The same view models in `@fadenstack/ui`, `SkillWorkshop` and the reader in `@fadenstack/agents`, `StoreSkillWorkshop` in `@fadenstack/skills` over IndexedDB; import and export as a zip only, in the browser | There is no folder in a browser: import is a zip the person picks, export a download. The switches are in the person's IndexedDB, not `skills.json`. A resource that is not text is refused, one more check than .NET. |
| Errors | `EndpointKeyRejectedException`, `EndpointOriginRefusedException`, `EndpointModelNotFoundException`, `EndpointContextLengthException`, `EndpointNotCompatibleException`, `EndpointUnreachableException`, `ServerCertificateException`, `EndpointPlainHttpRefusedException`; a 429 is `FadenstackRateLimitedException` | the same names ending in `Error`; a 429 is `FadenstackRateLimitedError` | A browser cannot tell an untrusted certificate from an unreachable server: both are `EndpointUnreachableError`. |

## Logging and telemetry

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| Content-free logs | `UseLogging(ILoggerFactory)`; ids 1000-1099 client, 2000-2099 agent | `useLogging(FadenstackLogger)`; the same event ids and names | |
| A log file | `FadenstackFileLoggerProvider` | — | There is no file sink in a browser. |
| Traces and metrics | `FadenstackTelemetry` sources and meter | — | |

## The chat panel

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| The chat view model | `AgentChatViewModel` | `AgentChatViewModel` | |
| The skins | `Fadenstack.UI.Wpf`, `Fadenstack.UI.Avalonia` | `@fadenstack/ui-react`, `@fadenstack/ui-vue`, with `@fadenstack/ui-web` | |
| The approval broker | `ApprovalBroker` | `ApprovalBroker` | |
| Host commands in the panel | `AgentChatViewModel.InvokeToolAsync`, `AgentChatOptions.BeforeCommand`, `AfterCommand` ([Host commands](host-commands.md)) | `invokeTool`, `beforeCommand`, `afterCommand` | |
| The levels a person picks | `ReasoningLevel`: Auto, Off, Low, Medium, High | `ReasoningLevel`: `auto`, `off`, `low`, `medium`, `high` | |
| The pick, kept per person and agent | `agent.ReasoningChoices`, `SetReasoningAsync`, a file in the user's folder | `agent.reasoningChoices`, `setReasoning`, `ReasoningStore` (IndexedDB) | |
| The pill | `AgentChatViewModel`: `SelectReasoningCommand` and the options | `AgentChatViewModel`: `reasoningOptions`, `selectReasoning` | |
| Languages | English, German, French, Spanish, Simplified Chinese | The same | |
| Nested Markdown, three levels | `MarkdownParser.MaxNestingDepth` | `MAX_NESTING_DEPTH` | |
| A `markdown` fence as a preview | `PreviewBlock` (`Source`, `Blocks`) | a block of kind `preview` (`source`, `blocks`) | |
| A `mermaid` fence | `CodeBlock.IsDiagram`, shown as text | a code block with `diagram: true`, shown as text | |
| Details on an approval card | `ApprovalPart.TitleInlines`, `SourceLine`, `HasSourceLine`, `Reason` | `ApprovalPart.titleParts`, `sourceLine`, `hasSourceLine`, `reason` | |
| A gate on running turns | `AgentChatOptions.TurnGate`, a lease with `Dispose()` | `turnGate`, a lease with `dispose()` | |
| Answers without an AI mark | hidden by default; `chat.TellUnmarked`, preference key `tell-unmarked` | hidden by default; `chat.showUnmarkedNotice`, `setShowUnmarkedNotice`, preference key `unmarkedNotice` | |
| Stop everything | `agent.StopAllAsync()`, `FadenstackAgent.StopAllAsync(agents)` (a `StopAllResult`), `ChatSession.Stop()`, `StopEverythingViewModel`, `StopEverythingButton` in each skin | `agent.stopAll()`, `stopAll(agents)` (a `StopAllResult`), `chat.stop()`, `StopEverythingViewModel`, `StopEverythingButton` in each skin | |
| The off-switch | `ErrorCodes.AgentDisabled`, `IsAgentDisabled`, `AgentProfile.Enabled`, `agent.TurnedOff`, `TurnedOffChanged`, `CheckAgainAsync()`, `ChatOutcome.AgentDisabled` | `errorCodes.agentDisabled`, `isAgentDisabled(error)`, `AgentProfile.enabled`, `agent.turnedOff`, `onTurnedOffChange`, `checkAgain()`, outcome `"agent_disabled"` | |

## Tests and samples

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| A fake server | — | `@fadenstack/testing`: `FakeGateway` | |
| The live check | `Fadenstack.Samples.LiveCheck` | `samples/livecheck.mjs` | See [Samples](samples.md). |
| Console and panel samples | `Fadenstack.Samples.Console`, `Fadenstack.Samples.Wpf` | — | |

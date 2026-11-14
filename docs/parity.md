# Parity with the TypeScript SDK

This page lists each feature of the SDK with its name in the .NET SDK and in the [TypeScript SDK](../typescript/index.md), and where a browser does it differently. The two SDKs follow one design: the same concepts, the same tool classes and modes, the same transcript format and the same wire. TypeScript uses camelCase and drops C#'s `Async` suffix; durations in the grant API are milliseconds there and `TimeSpan` here.

A dash means the SDK has nothing of that name. The last column is empty where the two work alike.

## Packages

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| Server connection | `Fadenstack.Client` | `@fadenstack/client` | |
| The agent | `Fadenstack.Agents` | `@fadenstack/agents` | |
| View models of the chat | `Fadenstack.UI` | `@fadenstack/ui` | |
| The skin of the chat | `Fadenstack.UI.Wpf`, `Fadenstack.UI.Avalonia` | `@fadenstack/ui-web` (the look), `@fadenstack/ui-react`, `@fadenstack/ui-vue` | |
| Local MCP servers | `Fadenstack.Mcp` | — | A browser has no local MCP process. |
| Delegation between agents on one device | `Fadenstack.Agents.Delegation` (for applications, not on nuget.org: [Delegating to agents](delegation.md)) | — | A browser has no local pipe. A TypeScript A2A client comes later. |
| Content provenance | `Fadenstack.Provenance` | `@fadenstack/provenance` | |
| Encryption at rest on macOS and Linux | `Fadenstack.Secrets` | — | A browser encrypts its stores with a WebCrypto key ([Stores](#stores-and-data-rights)). |
| Test helpers | Seams and the provenance kit ([Testing](testing.md)) | `@fadenstack/testing` (`FakeGateway`) | |

## Signing in

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| A client for one agent | `FadenstackClient.ForAgent(serverUrl, agent, out tokens)` | `FadenstackClient.forAgent({ serverUrl, agent })` | |
| Sign in with a login token | `tokens.SignInAsync(loginToken, deviceLabel)` | `tokens.signIn(loginToken, deviceLabel)` | |
| A login token from a password | — | `loginWithPassword({ apiUrl, email, password })` | |
| Sign in by pairing | `AgentPairing`: `StartAsync`, `WaitAsync`, `RedeemAsync` | `AgentPairing`: `start`, `wait`, `redeem` | |
| Several agents with one approval | `StartAsync(..., agents)`; at most `AgentPairing.MaxAgents` (8); agents an old server left out in `PairingStart.AgentsLeft` | `start(..., { agents })`; agents an old server left out in `PairingStart.ignoredAgents` | |
| A server too old for pairing | `FadenstackPairingUnsupportedException` (404 and 405) | `FadenstackPairingUnsupportedError` (404 and 405) | |
| Denied, expired, agent not redeemable | `FadenstackPairingDeniedException`, `FadenstackPairingExpiredException`, `FadenstackPairingAgentException` | `FadenstackPairingDeniedError`, `FadenstackPairingExpiredError`, `FadenstackPairingAgentError` | |
| The sign-in page must be the server's own | `StartAsync` throws `FadenstackException` | `start` throws `FadenstackError` | |
| Where the sign-in is kept | `ITokenStore`: `FileTokenStore`, `MemoryTokenStore` | `TokenStore`: `MemoryTokenStore` | A browser extension keeps it in `chrome.storage` with a store of its own ([Build an agent](../typescript/agent.md)). |

## The agent

| Feature | .NET | TypeScript | Where a browser differs |
|---|---|---|---|
| The builder | `FadenstackAgent.CreateBuilder()...Build()` | `FadenstackAgent.createBuilder()...build()` | |
| Host tools | `[AgentTool(ToolClass.Read)]` methods, `AddTools`, `AddTool` | `defineTool({...})`, `addTools` | |
| Approvals | `UseApprovals`, `ApprovalDecision.Approve()`, `ApproveForSession()`, `ApproveAlways()`, `Deny()` | `useApprovals`, `ApprovalDecision` | |
| A turn | `session.StreamAsync(message)`, `session.SendAsync(message)` | `session.stream(message)`, `session.send(message)` | |
| Events of a turn | `TextDelta`, `ToolCallStarted`, `TurnCompleted`... | `{ type: "text" }`, `{ type: "tool_started" }`, `{ type: "turn_completed" }`... | |
| SDK ids for empty or repeated call ids | `fs-<turn id>-<round>-<index>` | the same | |
| How long the agent thinks | `UseReasoning(ReasoningEffort)`, `agent.ReasoningChoices`, `agent.SetReasoningAsync(ReasoningLevel)` | `useReasoning(effort)`, `agent.reasoningChoices`, `agent.setReasoning(level)` | |
| The server's cap, `ui.reasoning.max` | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`; `minimal` caps at `none`, `max` sets no cap | the same scale and the same rules | `ReasoningEffort` has no `minimal`; TypeScript's `useReasoning` accepts it. |
| Skills | `agent.ListSkillsAsync(query)`, `SendOptions.Skills` | `agent.listSkills(query)`, `skills` in the send options | |
| The user's language | `agent.SetUserLanguageAsync(tag)` | `agent.setUserLanguage(tag)` | |
| The organisation's tools | `agent.ListServerToolsAsync()` returns `AgentToolInfo`, or `null` on an old server | `client.agent.listTools()` | |
| Tool sources that change | `IChangingToolSource` and its `Changed` event | `ChangingToolSource` and `onChanged` | |
| Tools in scope per conversation | `AgentTool.Scope`, `IToolScope` | `ToolScope` (`offers`, `beforeCall`) | |
| Tools on request, `find_tool` | `LocalMcpModule` (`LocalMcpModule.FindToolName`, `MaxFound` 5, `MaxToolsInScope` 10) | `OnRequestTools` (`FIND_TOOL_NAME`, `MAX_FOUND`, `MAX_TOOLS_IN_SCOPE`) over the host's own tools | A browser has no local MCP process; the same seam works over the host's tools. |
| Host commands | `session.InvokeToolAsync`, `session.StreamToolAsync` | `session.invokeTool`, `session.streamTool` | |
| Fresh policy for host calls | `RequireHostToolPolicyChecks()` | `requireHostToolPolicyChecks()` | |
| Stages and modules | `AddChatStage`, `AddToolStage`, `Use(module)` | `addChatStage`, `addToolStage`, `use(module)` | |
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
| Encryption | `ISecretProtector`: DPAPI, or the OS key store with `FadenstackSecrets.UseOsKeyStore()`; without either, transcripts stay in memory | `SecretProtector`, `useSecretProtector`: a non-extractable WebCrypto key per user | There is no OS key store in a browser. |
| Retention | `UseSessionRetention(SessionRetention)`; the server's `local_retention` can only lower it | `useSessionRetention({ maxSessions, maxAgeDays })`; the same rule | |
| Export my data | `agent.ExportMyDataAsync(stream)` writes a ZIP | `agent.exportMyData()` returns a JSON document | A page hands the person a document to download. |
| Delete my data | `agent.DeleteMyDataAsync()` returns `DataDeletion` (`FoldersRemoved`, `HostSessionStoreLeft`, `HostApprovalStoreLeft`, `HostStoresLeft`) | `agent.deleteMyData()` returns `storesCleared`, `hostSessionStoreLeft`, `hostApprovalStoreLeft`, `hostStoresLeft` | It deletes the IndexedDB databases and their key. |
| Allow always | `IApprovalStore`: `FileApprovalStore`, `MemoryApprovalStore`; `agent.ListApprovalsAsync()`, `ResetApprovalsAsync()` | `agent.listApprovals()`, `resetApprovals()` | |

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
| Nested Markdown, three levels | `MarkdownParser.MaxNestingDepth` | `MAX_NESTING_DEPTH` | |
| A `markdown` fence as a preview | `PreviewBlock` (`Source`, `Blocks`) | a block of kind `preview` (`source`, `blocks`) | |
| A `mermaid` fence | `CodeBlock.IsDiagram`, shown as text | a code block with `diagram: true`, shown as text | |
| Details on an approval card | `ApprovalPart.TitleInlines`, `SourceLine`, `HasSourceLine`, `Reason` | `ApprovalPart.titleParts`, `sourceLine`, `hasSourceLine`, `reason` | |
| A gate on running turns | `AgentChatOptions.TurnGate`, a lease with `Dispose()` | `turnGate`, a lease with `dispose()` | |
| Answers without an AI mark | hidden by default; `chat.TellUnmarked`, preference key `tell-unmarked` | hidden by default; `chat.showUnmarkedNotice`, `setShowUnmarkedNotice`, preference key `unmarkedNotice` | |

# Changelog

All notable changes to the Fadenstack .NET SDK. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the packages follow [Semantic Versioning](https://semver.org/): before 1.0, a minor version may break the API, and
this file says where it does.

## [0.3.0] - Unreleased

### Added

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

- **Breaking (unlikely to matter):** `find_tool` is the SDK's own name, `AgentTool.ToolSearchName`: `AddTool` and `AddTools`
  refuse an app's tool of that name, and an app's tool source that brings one has it left out (log event 2016). A
  Fadenstack gateway from 0.7.1 takes `find_tool` as read, so the local MCP module's search no longer asks under a server;
  the tools it finds still ask on every call. Only the SDK's own sources bring it.

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

### Removed

- **Breaking:** `UiStrings.AiGenerated` and `UiStrings.AiGeneratedTitle` (now `ProvenanceTexts`).

### Fixed

- Two processes that keep the same agent sign-in no longer sign each other out by renewing it at the same time.
- A kept sign-in, stored approvals or a saved chat that cannot be read no longer throws: it counts as nothing kept, and a
  chat line that cannot be read costs only that line.
- Pairing works on macOS and Linux when the server names its sign-in page as a path.
- Where `libc` cannot be loaded, a grant's path check asks the person instead of throwing.

### Security

- No request follows a redirect, so a 307 or 308 can no longer send a request's body (chat text, a refresh token, a
  pairing code) to another address (OWASP ASVS 5.0 V12).
- Files that hold secrets are written owner-only (0600) before anything goes into them, replaced atomically, and not
  written at all when the mode cannot be set (OWASP ASVS 5.0 V14).

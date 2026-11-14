# Standalone mode: the person's own model

An agent built with the SDK talks either to an organisation's Fadenstack server (governed mode) or straight to a model
endpoint the person chose (standalone mode): Ollama or LM Studio on their own computer, a vLLM server in their network,
an OpenAI-compatible cloud. The mode is chosen in one builder line; everything above it (the loop, tool classes,
approvals, sessions, the chat panel) is the same in both.

```csharp
var agent = FadenstackAgent.CreateBuilder()
    .UseEndpoint(new ModelEndpoint
    {
        BaseUrl = new Uri("http://localhost:11434/v1"), // the base includes /v1
        Model = "qwen3:14b",
        Dialect = EndpointDialect.Ollama,                 // from a preset or the endpoint check
        // Key = keyProvider,                              // an ITokenProvider; none for a keyless local server
    })
    .UseLocalProfile(AgentDefinition.FromJson(File.ReadAllText("contracts/word-assistant.json")),
        ownInstructions: settings.OwnInstructions)
    .AddTools(new WordTools())
    .UseApprovals(card)
    .Build();
```

`Build()` takes a server (`UseServer`, `UseClient`) or a model (`UseEndpoint`, or `UseChatClient` with any
`IChatClient`), never both. A model needs `UseLocalProfile`, and the agent's slug comes from the definition.

## The agent definition

`AgentDefinition` reads the agent's definition as your app ships it: the body a Fadenstack server's `POST /api/agents`
takes, the same file you give a server. It reads `slug`, `name`, `version.instructions`, `version.host_tool_contract`,
`local_mcp_mode`, `local_skills_mode` and `version.ui` (greeting, suggestions, languages). Unknown fields are kept
(`Json`, `ToJson()`); `model_alias` is ignored.

It fails closed. A contract entry whose `risk` is missing or unknown is unclassified, so the tool asks every time; a mode
other than `allow` is `disable`. Only a body without a valid slug throws (`FormatException`).

**Instructions.** The definition's instructions, then the person's own (`ownInstructions`), are the request's system
message. The session sets them above the chat stages, so a stage sees them and the history budget leaves room for them.

## What standalone has and has not

| | Governed | Standalone |
|---|---|---|
| Profile and policy | the server's | the definition, the endpoint check and the person's settings; a machine policy narrows them |
| Tool modes | the server's default and ceiling | Ask to start, Auto at most; no Bypass |
| Grants, Bypass, step-up, server checks | yes | no |
| Events and the app manifest | reported to the server | nowhere |
| The organisation's skills and MCP tools | yes | no; local skills and MCP come from the device |
| Pictures from an image model | where the server has one | not yet |
| Data folder | `users/<user>/` | `users/standalone/`, apart from any governed person's |

`agent.Connection` is `AgentConnection.Standalone`, `agent.ServerClient` is null and `agent.Client` throws; a tool source
reads `context.Connection` and `context.ServerClient`. Use the connection to show or hide the server's parts, never to
decide what may run: that comes from the profile and the tool classes, the same in both modes.

The first standalone start never takes the chats an older SDK kept in the shared data folder; a governed person who
signs in later still gets them.

## Endpoints and presets

| Preset (`EndpointPreset`) | Base | Dialect | Key |
|---|---|---|---|
| `OpenAI` | `https://api.openai.com/v1` | `OpenAI` | yes |
| `AzureOpenAI` | `https://{resource}.openai.azure.com/openai/v1` | `AzureOpenAI` (`api-key` header) | yes |
| `Ollama` | `http://localhost:11434/v1` | `Ollama` | no |
| `LMStudio` | `http://localhost:1234/v1` | `LMStudio` | no |
| `Vllm` | `http://localhost:8000/v1` | `Vllm` | optional |
| `Sglang` | `http://localhost:30000/v1` | `Sglang` | optional |
| `LlamaCpp` | `http://localhost:8080/v1` | `LlamaCpp` | no |
| `LiteLlm` | your proxy | `LiteLlm` | yes |
| `OpenRouter` | `https://openrouter.ai/api/v1` | `OpenRouter` | yes |
| `Mistral` | `https://api.mistral.ai/v1` | `Mistral` | yes |

A dialect says what a server expects where servers differ: `max_tokens` or `max_completion_tokens`; how the request
switches reasoning (`reasoning_effort`, the chat template's `enable_thinking`, or not at all); whether a stream may ask for
usage figures; the key's header; whether a request asks for the server's text watermark (SGLang). Keep its `Name` with the
endpoint and get it back with `EndpointDialect.Named(name)`.

`DirectChatClient` is the endpoint as an `IChatClient`, for use outside an agent too. It sends nothing of a server's (no
`X-Faden-*` header, no session or skills field, not the person's name) and passes through only `top_k`, `min_p`,
`repetition_penalty` and `parallel_tool_calls` from `ChatOptions.AdditionalProperties`. It reads nothing of a server's
from an answer either: an endpoint cannot hand the agent a refusal, an approval or a provenance receipt.

## The endpoint check

`EndpointCheck` tells you, and the person, whether an endpoint works for an agent. Its `EndpointReport` is serialisable
(`ToJson`, `FromJson`): keep it with the connection.

```csharp
var report = await new EndpointCheck(endpoint).RunAsync(new EndpointCheckOptions { RequiredContextTokens = 8000 });
if (report.ChatOnly) { /* the model calls no tools: the agent can only talk */ }
```

1. `GET {base}/models`: reachable, the key, the models.
2. A one-token streamed chat: does it answer, do usage figures come back?
3. The tool-call probe: one dummy function with `tool_choice: "required"`.
4. A picture, only with `ProbeVision`.
5. The context length: Ollama's `/api/show`, vLLM's `max_model_len`, LM Studio's own API, or the person's setting. Ollama
   runs a model with its default context unless the model sets `num_ctx`, and cuts longer prompts without a word: the
   report says so (`OllamaDefaultContext`).
6. The text watermark status (below).

`EndpointCheck.FindLocalAsync(personConsented, ...)` looks for Ollama, LM Studio, vLLM and llama.cpp on this computer
(loopback ports 11434, 1234, 8000, 8080, short timeouts). Without the person's consent it sends nothing.

`Problems` lists what the person should hear about: `Unreachable`, `CertificateNotTrusted`, `PlainHttpRefused`,
`KeyRejected`, `OriginRefused`, `NotCompatible`, `RateLimited`, `ModelNotFound`, `NoToolCalls`, `NoUsage`,
`ContextTooShort`, `OllamaDefaultContext`, `WatermarkRequestRefused`, `Failed`.

### The text watermark status

Some servers mark the text a model writes with an invisible watermark; the agent cannot add one itself. The report's
`Watermark` says, from a table of each server's own documentation (read 14 November 2026), whether this connection
marks text:

| Server | `TextWatermark` |
|---|---|
| Anthropic | `Marks` (by default) |
| OpenAI, xAI | `IfAccountEnabled` |
| vLLM 0.30 and later, SGLang 0.5.22 and later | `IfOperatorConfigured` (an older version: `Never`) |
| Ollama, LM Studio, llama.cpp, LocalAI | `Never` |
| Azure OpenAI, Google, Mistral, OpenRouter, LiteLLM, any other | `Unknown` |

Each entry carries its `Source`. xAI's `x-watermark` header, the only per-answer confirmation, is read into `Confirmed`.
On SGLang every request asks for the mark; a server that refuses the request gets requests without it, and the report says
`WatermarkRequestRefused`. Every answer of `DirectChatClient` carries an `EndpointMarking`
(`AdditionalProperties[FadenstackChatOptions.EndpointMarking]`): requested, confirmed, and whether the sampling was
deterministic (temperature 0, top-k 1), which defeats a watermark.

## Keys

| Where | How the key is kept |
|---|---|
| A .NET 8 app | the OS key store (`Fadenstack.Secrets`) |
| Office (.NET Framework) | DPAPI (`SecretProtection`) |

The SDK takes the key through an `ITokenProvider` and never stores it. Never put a key into a settings file, a log, an
export or a crash report. A key never goes into the endpoint's address either: an address with a user name or password is
refused.

## Transport rules

OWASP ASVS 5.0 V12 (secure communication) and V13 (secrets):

- **https,** or plain http to this computer (loopback).
- **Plain http to the local network** (RFC 1918, link-local, `fc00::/7`) only with `AllowPlainHttpInLan`, set when the
  person opted in for this endpoint after a warning. The addresses a name resolves to decide, and on .NET 8 the address
  actually connected to is checked too, so a name cannot be pointed elsewhere between the check and the connection.
- **A key never travels over plain http.** Such a request is refused before anything is sent
  (`EndpointPlainHttpRefusedException`, `Reason = KeyOverPlainHttp`).
- **No automatic redirect.** A redirect to the same origin is followed (at most three); any other is refused, so the key
  and the request never leave the origin they were meant for (`FadenstackApiException`, code `redirect`).
- **A CA of the endpoint's own** (`TrustedCaCertificate`), for this connection only.
- **Limits on what an endpoint may send:** a line of 4 Mi characters, an event of 8 Mi, a whole answer of 64 Mi, a tool
  call's arguments of 8 Mi; past one, the answer breaks off (`FadenstackStreamException`, code `stream_too_large`). A
  stream that sends nothing for `StreamIdleTimeout` is given up.

## Errors

| What happened | Exception |
|---|---|
| The key was refused (401, or 403 with a key) | `EndpointKeyRejectedException` |
| The origin was refused (403 without a key) | `EndpointOriginRefusedException` |
| No such model | `EndpointModelNotFoundException` |
| The request is longer than the model's context | `EndpointContextLengthException` |
| No OpenAI-compatible API at that address | `EndpointNotCompatibleException` |
| The rate limit (429), with `RetryAfter` | `FadenstackRateLimitedException`: the chat ends `RateLimited`, as with a server |
| Nothing answered | `EndpointUnreachableException` |
| The certificate is not trusted | `ServerCertificateException` |
| Plain http refused | `EndpointPlainHttpRefusedException` |
| What arrived is not an answer | `FadenstackStreamException` (`invalid_stream`) |

All but the last three are `FadenstackApiException`s with the status.

## Marks and records on the device

With `Fadenstack.Provenance` added (`Use(new ProvenanceModule())`), content placed in documents is marked as with a server,
and each record is signed on the device with an RFC 3161 time-stamp when the device is online: see
[Content provenance](provenance.md#without-a-server-records-signed-on-the-device).

## What a server adds

A Fadenstack server governs the agent centrally: the organisation's policy, PII masking and intent checks before the
model, the audit, grants and Bypass, the organisation's skills, knowledge and MCP tools, budgets, sign-in with the
organisation's accounts, and content marking with records the server signs. Standalone keeps the floor that lives on the
device (tool classes, approvals, the session store), and nothing leaves the device except to the endpoint.

## Try it

```text
dotnet run --project samples/Fadenstack.Samples.Console -- --endpoint http://localhost:11434/v1 --model qwen3:14b --dialect ollama --check
```

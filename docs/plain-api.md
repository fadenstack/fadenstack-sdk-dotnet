# Using the server without an agent

This page covers `Fadenstack.Client` on its own: the gateway's `/v1/chat/completions` route as an `IChatClient`, and the server's REST resources.

!!! note "Server version"
    Tool modes, governance notices, the gateway's events and `GetFeaturesAsync` need a Fadenstack server 0.4.2 or
    later. With 0.4.1 and older, chat and the server resources work without them.

## A plain client

Without an agent, a `FadenstackClient` uses the plain `/v1` routes with an API token and a model you choose:

```csharp
using Fadenstack;
using Fadenstack.Auth;

using var client = new FadenstackClient(new FadenstackClientOptions
{
    ServerUrl = new Uri("https://ai.example.internal"),
    Credentials = new StaticTokenProvider(apiToken),
    Model = "my-model",
});
```

`StaticTokenProvider` supplies one fixed token. `Model` is required for the plain routes, unless each request sets `ChatOptions.ModelId`. The other options are the same as for an agent's client; see [Build an agent](agent.md#the-client).

[Chat completions](../../api/chat-completions.md) documents the route itself.

## Chat

`client.Chat` is the gateway as a Microsoft.Extensions.AI `IChatClient`, so it works with anything built on that abstraction:

```csharp
using Microsoft.Extensions.AI;

var response = await client.Chat.GetResponseAsync("Summarise the travel policy in three points.");
Console.WriteLine(response.Text);

await foreach (var update in client.Chat.GetStreamingResponseAsync("And the expenses policy?"))
{
    Console.Write(update.Text);
}
```

It is built on `HttpClient` and speaks the OpenAI chat completions format. Tools in `ChatOptions.Tools` are sent as function tools, and settings such as `Temperature`, `TopP`, `MaxOutputTokens`, `StopSequences`, `ResponseFormat` and `Reasoning` are passed on as the matching fields. Other entries of `ChatOptions.AdditionalProperties` are added to the request body, except the keys that start with `faden.`.

## A refused prompt is a response

When the gateway refuses a prompt, `GetResponseAsync` does not throw. It returns a response whose `FinishReason` is `ContentFilter`, with the governance notice attached:

```csharp
using Fadenstack.Chat;

var response = await client.Chat.GetResponseAsync(question);
if (response.GetGovernanceNotice() is { } notice)
{
    Console.WriteLine($"Not answered: {notice.Message}");
}
else
{
    Console.WriteLine(response.Text);
}
```

The notice is a `GovernanceNoticeContent` in the response's contents. An answer stopped by an output check carries its notice the same way; in a stream, the notice arrives as a `GovernanceNoticeContent` in an update. Other errors throw the exceptions listed in [Outcomes and errors](outcomes.md#exceptions); a rate limit throws `FadenstackRateLimitedException`.

## Fadenstack options for a request

`FadenstackChatOptions` holds the keys and extension methods for Fadenstack's own request settings:

```csharp
using Fadenstack.Tools;

var options = new ChatOptions()
    .WithSession(session.Id.ToString())
    .WithToolMode(ToolMode.ReadOnly);

var response = await client.Chat.GetResponseAsync(question, options);
```

| Setting | Meaning |
|---|---|
| `WithSession(sessionId)` | Uses a server session (see below) for this request. |
| `WithToolMode(mode)` | The tool mode for this request. Without it, the client's `ToolMode` option applies, which is `Ask` by default. |
| `WithConversation(conversationId, turnId)` | Names the conversation and turn. Only an agent's route reads them, to group usage per conversation without keeping the conversation; the plain routes ignore them. |
| `FadenstackChatOptions.Skills` | A key for `AdditionalProperties`: the skills to use for this message, by slug. `options.WithSkills(slugs)` sets it; `client.Agent.ListSkillsAsync(query, limit)` lists the ones the user can pick. |

With `GatewayEvents` on (the default), the response also carries the gateway's events as `FadenstackEventContent` items, such as citations.

## Server resources

The client also exposes the server's REST resources. They belong to the server's own sessions, as used by the web chat and the API. An agent's sessions are kept on the device instead.

| Resource | Methods |
|---|---|
| `client.Sessions` | `CreateAsync`, `ListAsync`, `GetAsync`, `UpdateAsync`, `DeleteAsync`, `MessagesAsync`, `SummaryAsync` |
| `client.Projects` | `CreateAsync`, `ListAsync`, `GetAsync`, `UpdateAsync`, `DeleteAsync` |
| `client.Tools` | `GetAvailableAsync` (a session's tools, with their classes and what the session's tool mode does with them), `GetPolicyAsync`, `UpdatePolicyAsync` (including the session's tool mode) |
| `client.Memory` | `ListAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync` for memory facts |
| `client.Attachments` | `UploadAsync`, `UploadToProjectAsync`, `ListAsync`, `ListProjectAsync`, `DeleteAsync`, `StatsAsync` |
| `client.Services` | `HealthAsync`, `ListAsync` |

`client.GetFeaturesAsync()` returns the server's edition and features.

```csharp
var session = await client.Sessions.CreateAsync(title: "Travel questions");
await client.Tools.UpdatePolicyAsync(session.Id, toolMode: ToolMode.ReadOnly);

var answer = await client.Chat.GetResponseAsync("Which hotels may I book?",
    new ChatOptions().WithSession(session.Id.ToString()));

foreach (var message in await client.Sessions.MessagesAsync(session.Id))
{
    Console.WriteLine($"{message.Role}: {(message.Status == "ok" ? message.Content : message.Notice)}");
}
```

A stored message that was refused has an empty `Content`, a `Status` of `blocked_input` or `blocked_output`, and the `Notice` the user was told.

The records (`Session`, `Project`, `SessionMessage`, `MemoryFact`, `Attachment` and others) are in the `Fadenstack.Models` namespace. The resources throw the exceptions listed in [Outcomes and errors](outcomes.md#exceptions).

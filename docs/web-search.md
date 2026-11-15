# Pictures and web search

## Pictures

In governed mode the agent makes pictures through the organisation's server (`GenerateImagesAsync`), which marks them: signed metadata and a watermark. In standalone mode the SDK offers no picture tools in 0.3.0. The device cannot yet add both kinds of mark on its own, and the AI Act's marking rules expect them. Pictures the person attaches for the model to see are not affected.

## The web search tool

`web_search` is one of the SDK's own tools. Add its module, and the person picks a provider:

```csharp
var webSearch = new WebSearchModule();
var agent = FadenstackAgent.CreateBuilder()
    .UseEndpoint(endpoint).UseLocalProfile(definition)
    .Use(webSearch)
    .Build();

var settings = WebSearchStore.ForUser("standalone");        // the person's own folder
settings.Save(new WebSearchSettings { Provider = WebSearchProvider.Brave });
settings.SetBraveKey(keyThePersonTyped);                     // encrypted, never shown back
```

**Providers:**

| Provider | What the person gives | How it is asked |
|---|---|---|
| Brave Search API | Their own key (`SetBraveKey`) | `GET https://api.search.brave.com/res/v1/web/search`, the key in `X-Subscription-Token` |
| SearXNG | The instance's address, with its JSON format turned on | `GET <address>/search?q=...&format=json` |

A SearXNG address must be https, or plain http on this computer only. An address with a user or a password is refused.

**When it is offered.** The tool is offered only when all of these hold:
- the person picked a provider, with its key or address;
- this computer's policy allows it (`MachinePolicy.WebSearch`; see [Machine policy](machine-policy.md));
- in governed mode, the administrator allows local tools for this agent (the profile's `local_mcp.mode` is `allow`), until the server has a switch of its own.

The agent asks for its tools again at its next turn after the person changes the settings.

**How it asks.** `web_search` is a read tool that is open-world: what it is given leaves the organisation. Below Auto a call asks, and the person can allow it for this conversation at most ([Host tools](tools.md#approvals)). Auto and Bypass run it. A Fadenstack gateway knows the name and keeps it open-world under any rule.

**The result** is up to 10 items, each with a title, an address and a snippet. A search that takes longer than 10 seconds ends with no results. The result is untrusted text: web pages wrote it. The SDK cleans each line, removing markup, control characters and invisible or direction marks, and keeps only http and https addresses. It then fences the result with a tag that changes on every search, and tells the model to treat it as data, never as instructions (OWASP Top 10 for LLM Applications 2025, LLM01).

**What is kept and logged.**
- The provider and the SearXNG address go in `users/<user>/preferences/web-search.json`.
- The Brave key goes in `users/<user>/keys/web-search-brave.key`, encrypted. Without a key store it stays in memory for the run.
- Deleting the person's data removes both. The export holds the settings, never the key ([Data on the device](data-on-device.md)).
- The log holds the provider, the number of results, the duration and the kind of a failure (events 2080 to 2082), never the query, a result or the key (OWASP ASVS 5.0 V13, V16).

`WebSearchOptions` sets the timeout (`Timeout`), the number of results (`MaxResults`, at most 10), the person's store (`Store`), and an HTTP handler for tests (`Handler`).

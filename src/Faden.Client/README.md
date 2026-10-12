# Fadenstack.Client

The connection to a [Fadenstack](https://fadenstack.com) server: the gateway as a Microsoft.Extensions.AI
`IChatClient`, agent sign-in, the agent's profile, the server's REST resources and typed errors. The code is in the
`Faden` namespace.

Most applications want an agent: add [Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents), which
brings this package. Use this package on its own to call the gateway without an agent.

```bash
dotnet add package Fadenstack.Client
```

```csharp
using Faden;
using Faden.Auth;

using var client = new FadenClient(new FadenClientOptions
{
    ServerUrl = new Uri("https://ai.example.internal"),
    Credentials = new StaticTokenProvider(apiToken),
    Model = "my-model",
});
```

`client.Chat` is an `IChatClient`, so it works with anything built on Microsoft.Extensions.AI. The server's
policies apply to every call.

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`, and needs a Fadenstack server 0.4.2 or
later.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)

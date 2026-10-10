# Dependency injection

This page covers registering an agent with the standard .NET host and its service container.

## Register the agent

`AddFadenAgent` registers a `FadenAgent` as a singleton. The agent is built the first time it is requested, and the configuration callback gets the service provider, so tools, approval handlers and stores can come from the container.

```csharp
using Faden.Agents;
using Faden.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var serverUrl = new Uri("https://ai.example.internal");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new AgentTokenProvider(serverUrl, "my-assistant", new FileTokenStore()));
builder.Services.AddSingleton<PageTools>();
builder.Services.AddFadenAgent((agent, services) => agent
    .UseServer(serverUrl, services.GetRequiredService<AgentTokenProvider>())
    .UseAgent("my-assistant")
    .AddTools(services.GetRequiredService<PageTools>()));

using var host = builder.Build();
```

`AddFadenAgent` is in the `Microsoft.Extensions.DependencyInjection` namespace. It has two forms:

| Form | Use it when |
|---|---|
| `AddFadenAgent((AgentBuilder agent, IServiceProvider services) => ...)` | The configuration needs services from the container. |
| `AddFadenAgent((AgentBuilder agent) => ...)` | It does not. |

The registration uses `TryAdd`: if a `FadenAgent` is already registered, a second call adds nothing. The container disposes the agent when it is disposed.

## Use the agent

Request `FadenAgent` like any other service:

```csharp
public sealed class AssistantService(FadenAgent agent)
{
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        var session = await agent.StartSessionAsync(cancellationToken: cancellationToken);
        var result = await session.SendAsync(question, cancellationToken);
        return result.Text;
    }
}
```

## Sign-in in a hosted application

`UseServer` takes any `ITokenProvider`. Register the `AgentTokenProvider` as a singleton, as above, so the part of your application that signs the user in and the agent share it. The provider is created with the backend's URL (usually the server URL), the agent's slug and a token store.

An `AgentTokenProvider` created this way uses its own `HttpClient` for sign-in. If the server's certificate is issued by a CA the machine does not trust, pass an `HttpClient` that trusts it, for example one built on `FadenTransport.CreateHandler(options)` with `TrustedCaCertificate` set, and set the same certificate in `UseServer`'s `configure` callback.

```csharp
using Faden;
using Faden.Http;

var ca = File.ReadAllBytes("company-ca.crt");
var http = new HttpClient(FadenTransport.CreateHandler(new FadenClientOptions { TrustedCaCertificate = ca }));
var tokens = new AgentTokenProvider(serverUrl, "my-assistant", new FileTokenStore(), http);

builder.Services.AddSingleton(tokens);
builder.Services.AddFadenAgent((agent, services) => agent
    .UseServer(serverUrl, services.GetRequiredService<AgentTokenProvider>(),
        options => options.TrustedCaCertificate = ca)
    .UseAgent("my-assistant"));
```

For an application without a user at the keyboard, also register an approval handler that fits your case, or keep the default, which declines every call that needs approval. For trusted automation, `UsePermissionMode(PermissionMode.Bypass)` approves those calls instead, destructive ones too; an administrator's "Ask first" rule still goes to the handler. See [Host tools](tools.md#bypass-mode).

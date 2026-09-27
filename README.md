# Fadenstack .NET SDK

Official .NET client for [Fadenstack](https://fadenstack.github.io) — wraps the OpenAI .NET SDK with session management, tool routing, memory, and attachment APIs.

## Installation

```bash
dotnet add package Faden.Client
```

## Quick Start

```csharp
using Faden.Client;
using OpenAI.Chat;

var client = new FadenClient(new FadenClientOptions
{
    BaseUrl = "https://your-faden-instance.com",
    ApiKey = "your-jwt-token",
});

// OpenAI-compatible — works out of the box
ChatClient chat = client.GetChatClient("gpt-4o");
ChatCompletion response = await chat.CompleteChatAsync("Hello");

// Fadenstack extensions
var session = await client.Sessions.CreateAsync(title: "My Chat");
var tools = await client.Tools.GetAvailableAsync(session.Id);
var facts = await client.Memory.ListAsync(scope: "user");
```

## License

Apache-2.0

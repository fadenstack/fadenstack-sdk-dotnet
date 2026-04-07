# LLM.port .NET SDK

Official .NET client for [LLM.port](https://llm-port.com) — wraps the OpenAI .NET SDK with session management, tool routing, memory, and attachment APIs.

## Installation

```bash
dotnet add package LLMPort.Client
```

## Quick Start

```csharp
using LLMPort.Client;
using OpenAI.Chat;

var client = new LLMPortClient(new LLMPortClientOptions
{
    BaseUrl = "https://your-llmport-instance.com",
    ApiKey = "your-jwt-token",
});

// OpenAI-compatible — works out of the box
ChatClient chat = client.GetChatClient("gpt-4o");
ChatCompletion response = await chat.CompleteChatAsync("Hello");

// LLM.port extensions
var session = await client.Sessions.CreateAsync(title: "My Chat");
var tools = await client.Tools.GetAvailableAsync(session.Id);
var facts = await client.Memory.ListAsync(scope: "user");
```

## License

Apache-2.0

using System.Net.Http.Headers;
using Faden.Client.Resources;
using OpenAI;
using OpenAI.Chat;

namespace Faden.Client;

/// <summary>
/// Fadenstack client — wraps the OpenAI .NET SDK with Fadenstack-specific resources
/// (sessions, projects, tools, memory, attachments, services).
/// </summary>
public sealed class FadenClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly OpenAIClient _openAiClient;

    /// <summary>Sessions CRUD, messages, summary, capacity.</summary>
    public SessionsResource Sessions { get; }

    /// <summary>Projects CRUD.</summary>
    public ProjectsResource Projects { get; }

    /// <summary>Tool availability and session tool policy.</summary>
    public ToolsResource Tools { get; }

    /// <summary>Memory facts CRUD.</summary>
    public MemoryResource Memory { get; }

    /// <summary>Attachment upload, list, delete.</summary>
    public AttachmentsResource Attachments { get; }

    /// <summary>Health check and service discovery.</summary>
    public ServicesResource Services { get; }

    public FadenClient(FadenClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseUrl = options.BaseUrl.TrimEnd('/');

        // HttpClient for Fadenstack extension endpoints
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        // OpenAI client pointed at Fadenstack gateway
        var openAiOptions = new OpenAIClientOptions { Endpoint = new Uri($"{baseUrl}/v1") };
        var credential = new System.ClientModel.ApiKeyCredential(options.ApiKey);
        _openAiClient = new OpenAIClient(credential, openAiOptions);

        // Wire up resources
        Sessions = new SessionsResource(_http);
        Projects = new ProjectsResource(_http);
        Tools = new ToolsResource(_http);
        Memory = new MemoryResource(_http);
        Attachments = new AttachmentsResource(_http);
        Services = new ServicesResource(_http);
    }

    /// <summary>Get an OpenAI <see cref="ChatClient"/> for chat completions against the Fadenstack gateway.</summary>
    public ChatClient GetChatClient(string model) => _openAiClient.GetChatClient(model);

    public void Dispose()
    {
        _http.Dispose();
    }
}

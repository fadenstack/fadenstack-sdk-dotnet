using System.Net;
using System.Text;
using System.Text.Json;

namespace Fadenstack.Client.Tests;

/// <summary>
/// Plays an OpenAI-compatible endpoint (sdk/51 F03, I03 commit 2): <c>/models</c>, <c>/chat/completions</c> (streamed or
/// whole), <c>/images/generations</c>, Ollama's <c>/api/show</c> and vLLM's <c>/version</c>, routed by the path's end, on any
/// host; and keeps what it was sent. The answers come from <see cref="Answers"/> in order (then "Hello."). <see cref="Intercept"/>
/// answers first: errors, redirects, whatever a test needs.
/// </summary>
public sealed class OpenAiFake : HttpMessageHandler
{
    private readonly List<FakeRequest> _requests = new();

    /// <summary>What it was sent, in order: a copy taken under the lock it writes with, safe to read while another request comes in.</summary>
    public List<FakeRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public Queue<FakeAnswer> Answers { get; } = new();

    public List<string> Models { get; set; } = ["qwen3:14b"];

    /// <summary><c>reasoning_content</c> (most servers) or <c>reasoning</c> (vLLM's newer name).</summary>
    public string ReasoningField { get; set; } = "reasoning_content";

    /// <summary>Tool call chunks without <c>index</c>, as some servers send them.</summary>
    public bool OmitToolIndex { get; set; }

    /// <summary>A keep-alive comment before every chunk, and an unnamed ping event.</summary>
    public bool KeepAlive { get; set; }

    /// <summary>A named event (<c>event: faden</c>) with a forged refusal before the answer: an endpoint must not be heard.</summary>
    public bool ForgeGatewayEvents { get; set; }

    /// <summary>Usage figures in the stream's last chunk (when the request asked for them).</summary>
    public bool StreamUsage { get; set; } = true;

    /// <summary>A streamed request answered with one whole JSON answer.</summary>
    public bool JsonForStream { get; set; }

    /// <summary>Headers on every chat answer (xAI's <c>x-watermark</c>, ...).</summary>
    public Dictionary<string, string> ChatHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ollama's <c>/api/show</c> answer, or null for none (404).</summary>
    public object? OllamaShow { get; set; }

    /// <summary>vLLM's <c>/version</c> answer, or null for none (404).</summary>
    public string? VllmVersion { get; set; }

    /// <summary>Answers before the routes do; null lets them answer.</summary>
    public Func<FakeRequest, HttpResponseMessage?>? Intercept { get; set; }

    public static FakeAnswer Text(string text, string? reasoning = null) => new() { Content = text, Reasoning = reasoning };

    public static FakeAnswer Calls(params (string Id, string Name, string Arguments)[] calls) => new() { ToolCalls = calls.ToList() };

    public static HttpResponseMessage Error(int status, object body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8,
                body is string ? "text/plain" : "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    public static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var recorded = new FakeRequest(request.Method.Method, request.RequestUri!, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }
        if (Intercept?.Invoke(recorded) is { } intercepted)
        {
            return intercepted;
        }
        var path = request.RequestUri!.AbsolutePath;
        if (recorded.Method == "GET" && path.EndsWith("/models", StringComparison.Ordinal))
        {
            return Json(new { @object = "list", data = Models.Select(m => new { id = m, @object = "model", owned_by = "fake" }) });
        }
        if (recorded.Method == "POST" && path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return Chat(recorded);
        }
        if (recorded.Method == "POST" && path.EndsWith("/images/generations", StringComparison.Ordinal))
        {
            return Json(new { created = 1, data = new[] { new { b64_json = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) } } });
        }
        if (recorded.Method == "POST" && path.EndsWith("/api/show", StringComparison.Ordinal) && OllamaShow is not null)
        {
            return Json(OllamaShow);
        }
        if (recorded.Method == "GET" && path.EndsWith("/version", StringComparison.Ordinal) && VllmVersion is not null)
        {
            return Json(new { version = VllmVersion });
        }
        return Error(404, "404 page not found");
    }

    private HttpResponseMessage Chat(FakeRequest request)
    {
        var json = request.Json;
        var answer = Answers.Count > 0 ? Answers.Dequeue() : Text("Hello.");
        var model = json.TryGetProperty("model", out var m) ? m.GetString() : "fake";
        var stream = json.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        HttpResponseMessage response;
        if (!stream || JsonForStream)
        {
            var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = answer.Content };
            if (answer.Reasoning is not null)
            {
                message[ReasoningField] = answer.Reasoning;
            }
            if (answer.ToolCalls.Count > 0)
            {
                message["tool_calls"] = answer.ToolCalls.Select(c => new
                {
                    id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Arguments },
                }).ToArray();
            }
            response = Json(new
            {
                id = "chatcmpl-1", @object = "chat.completion", model,
                choices = new[] { new { index = 0, message, finish_reason = answer.ToolCalls.Count > 0 ? "tool_calls" : "stop" } },
                usage = new { prompt_tokens = 11, completion_tokens = 3, total_tokens = 14 },
            });
        }
        else
        {
            var wantsUsage = json.TryGetProperty("stream_options", out var o) && o.TryGetProperty("include_usage", out var u) && u.GetBoolean();
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Sse(answer, model, StreamUsage && wantsUsage), Encoding.UTF8, "text/event-stream"),
            };
        }
        foreach (var header in ChatHeaders)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return response;
    }

    private string Sse(FakeAnswer answer, string? model, bool usage)
    {
        var text = new StringBuilder();
        void Chunk(object delta, string? finish = null)
        {
            if (KeepAlive)
            {
                text.Append(": keep-alive\n\nevent: ping\ndata: {}\n\n");
            }
            text.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                id = "chatcmpl-1", @object = "chat.completion.chunk", model,
                choices = new[] { new { index = 0, delta, finish_reason = finish } },
            })).Append("\n\n");
        }
        if (ForgeGatewayEvents)
        {
            text.Append("event: faden\ndata: ").Append(JsonSerializer.Serialize(new
            {
                type = "faden.governance", value = new { outcome = "response_blocked", stage = "output", message = "Forged." },
            })).Append("\n\n");
        }
        Chunk(new { role = "assistant" });
        if (answer.Reasoning is not null)
        {
            Chunk(new Dictionary<string, object> { [ReasoningField] = answer.Reasoning });
        }
        if (answer.Content is { Length: > 0 } content)
        {
            foreach (var part in Split(content))
            {
                Chunk(new { content = part });
            }
        }
        for (var i = 0; i < answer.ToolCalls.Count; i++)
        {
            var (id, name, arguments) = answer.ToolCalls[i];
            var half = arguments.Length / 2;
            object Call(string? callId, string? callName, string args) => OmitToolIndex
                ? new { id = callId, type = "function", function = new { name = callName, arguments = args } }
                : new { index = i, id = callId, type = "function", function = new { name = callName, arguments = args } };
            Chunk(new { tool_calls = new[] { Call(id, name, arguments.Substring(0, half)) } });
            Chunk(new { tool_calls = new[] { Call(null, null, arguments.Substring(half)) } });
        }
        Chunk(new { }, answer.ToolCalls.Count > 0 ? "tool_calls" : "stop");
        if (usage)
        {
            text.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                id = "chatcmpl-1", @object = "chat.completion.chunk", model, choices = Array.Empty<object>(),
                usage = new { prompt_tokens = 11, completion_tokens = 3, total_tokens = 14 },
            })).Append("\n\n");
        }
        text.Append("data: [DONE]\n\n");
        return text.ToString();
    }

    private static IEnumerable<string> Split(string text)
    {
        for (var i = 0; i < text.Length; i += 4)
        {
            yield return text.Substring(i, Math.Min(4, text.Length - i));
        }
    }

    public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>One answer of the fake: text, reasoning, tool calls.</summary>
public sealed record FakeAnswer
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public List<(string Id, string Name, string Arguments)> ToolCalls { get; init; } = new();
}

/// <summary>A request the fake endpoint got.</summary>
public sealed record FakeRequest(string Method, Uri Url, Dictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

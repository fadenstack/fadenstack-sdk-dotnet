using System.Net;
using System.Text.Json;
using Faden.Auth;
using Faden.Chat;
using Faden.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Faden.Client.Tests;

/// <summary>The gateway as an IChatClient: the OpenAI shapes, the gateway's events, refusals as results.</summary>
public class ChatClientTests
{
    private const string Agent = "eplan-assistant";
    private const string Route = "/v1/agents/eplan-assistant/chat/completions";

    private static (FadenClient Client, FakeServer Server) Make(ToolMode mode = ToolMode.Ask)
    {
        var server = new FakeServer();
        var client = new FadenClient(new FadenClientOptions
        {
            ServerUrl = new Uri("https://faden.test"),
            Agent = Agent,
            Credentials = new StaticTokenProvider("agent-token"),
            HttpClient = new HttpClient(server),
            ToolMode = mode,
        });
        return (client, server);
    }

    private static object Whole(object message, string finish = "stop", object? faden = null) => new Dictionary<string, object?>
    {
        ["id"] = "chatcmpl-1",
        ["object"] = "chat.completion",
        ["model"] = "qwen3-32b",
        ["choices"] = new[] { new { index = 0, message, finish_reason = finish } },
        ["usage"] = new { prompt_tokens = 12, completion_tokens = 5, total_tokens = 17 },
        ["faden"] = faden,
    };

    private static object Chunk(object delta, string? finish = null) => new
    {
        id = "chatcmpl-2",
        @object = "chat.completion.chunk",
        model = "qwen3-32b",
        choices = new[] { new { index = 0, delta, finish_reason = finish } },
    };

    private static readonly AIFunction ReadPage = AIFunctionFactory.Create(
        (int page) => $"Page {page}", "read_page", "Reads a page of the project.");

    [Fact]
    public async Task A_whole_answer_goes_to_the_agent_route_in_the_openai_shape()
    {
        var (client, server) = Make();
        server.Json("POST", Route, Whole(new
        {
            role = "assistant",
            content = (string?)null,
            tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "read_page", arguments = "{\"page\":3}" } } },
        }, "tool_calls", new { events = new object[] { new { type = "CUSTOM", name = "faden.pii", value = new { entities = new { PERSON = 1 } } } } }));

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "What is on page 2?"),
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call_0", "read_page", new Dictionary<string, object?> { ["page"] = 2 }) }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call_0", "Page 2: motor circuit") }),
            new(ChatRole.User, "And page 3?"),
        };
        var options = new ChatOptions { Tools = [ReadPage], ToolMode = ChatToolMode.Auto }.WithConversation("conv-1", "turn-4");
        var response = await client.Chat.GetResponseAsync(history, options);

        var sent = server.Requests.Single();
        Assert.Equal("Bearer agent-token", sent.Header("Authorization"));
        Assert.Equal("ag-ui", sent.Header("X-Faden-Events"));
        Assert.Equal("ask", sent.Header("X-Faden-Tool-Mode"));
        Assert.Equal("conv-1", sent.Header("X-Faden-Conversation-Id"));
        Assert.Equal("turn-4", sent.Header("X-Faden-Turn-Id"));
        var body = sent.Json;
        Assert.Equal("agent", body.GetProperty("model").GetString());
        Assert.False(body.TryGetProperty("stream", out _));
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(new[] { "user", "assistant", "tool", "user" }, messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("content").ValueKind);
        var call = messages[1].GetProperty("tool_calls")[0];
        Assert.Equal("call_0", call.GetProperty("id").GetString());
        Assert.Equal("{\"page\":2}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_0", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("Page 2: motor circuit", messages[2].GetProperty("content").GetString());
        var tool = body.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("read_page", tool.GetProperty("name").GetString());
        Assert.Equal("integer", tool.GetProperty("parameters").GetProperty("properties").GetProperty("page").GetProperty("type").GetString());
        Assert.Equal("auto", body.GetProperty("tool_choice").GetString());

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(17, response.Usage!.TotalTokenCount);
        var contents = response.Messages.Single().Contents;
        var fc = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal(("call_1", "read_page"), (fc.CallId, fc.Name));
        Assert.Equal(3, ((JsonElement)fc.Arguments!["page"]!).GetInt32());
        Assert.Equal("faden.pii", Assert.Single(contents.OfType<FadenEventContent>()).Kind);
    }

    [Fact]
    public async Task A_stream_brings_text_events_whole_tool_calls_and_usage()
    {
        var (client, server) = Make(ToolMode.ReadOnly);
        server.On("POST", Route, _ => FakeServer.Sse(
            FakeServer.Faden(new { type = "CUSTOM", name = "faden.tool_mode", value = new { effective_tool_mode = "read_only", hidden = new[] { "delete_page" } } }),
            FakeServer.Data(Chunk(new { role = "assistant", content = "Let me " })),
            FakeServer.Data(Chunk(new { content = "look." })),
            FakeServer.Data(Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "read_page", arguments = "{\"pa" } } } })),
            FakeServer.Data(Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "ge\": 3}" } } } })),
            FakeServer.Data(Chunk(new { }, "tool_calls")),
            FakeServer.Data(new { id = "chatcmpl-2", choices = Array.Empty<object>(), usage = new { prompt_tokens = 9, completion_tokens = 4, total_tokens = 13 } }),
            FakeServer.Done));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "page 3?")]))
        {
            updates.Add(u);
        }

        var sent = server.Requests.Single();
        Assert.Equal("read_only", sent.Header("X-Faden-Tool-Mode"));
        Assert.True(sent.Json.GetProperty("stream").GetBoolean());
        Assert.True(sent.Json.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        var response = updates.ToChatResponse();
        Assert.Equal("Let me look.", response.Text);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal(3, ((JsonElement)call.Arguments!["page"]!).GetInt32());
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(13, response.Usage!.TotalTokenCount);
        Assert.Equal("faden.tool_mode", updates.SelectMany(u => u.Contents).OfType<FadenEventContent>().Single().Kind);
    }

    [Fact]
    public async Task A_stream_that_breaks_off_says_so()
    {
        var (client, server) = Make();
        server.On("POST", Route, _ => FakeServer.Sse(
            FakeServer.Data(Chunk(new { content = "Half" })),
            FakeServer.Data(new { error = new { message = "Upstream connection reset.", type = "server_error", code = "upstream_stream_failed" } }),
            FakeServer.Done));
        var seen = new List<string>();
        var error = await Assert.ThrowsAsync<FadenStreamException>(async () =>
        {
            await foreach (var u in client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                seen.Add(u.Text);
            }
        });
        Assert.Equal("upstream_stream_failed", error.Code);
        Assert.Equal(new[] { "Half" }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_prompt_is_a_result_with_the_notice(bool stream)
    {
        var (client, server) = Make();
        server.Json("POST", Route, new
        {
            error = new { type = "governance_error", code = "policy_blocked", message = "Project names stay out.", param = (string?)null },
            faden = new { governance = new { outcome = "prompt_blocked", stage = "input", category = "confidential", message = "Project names stay out.", decision_id = "d-1", request_id = "r-1" } },
        }, (HttpStatusCode)422);

        ChatResponse response = stream
            ? await client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "BLUEBIRD?")]).ToChatResponseAsync()
            : await client.Chat.GetResponseAsync([new ChatMessage(ChatRole.User, "BLUEBIRD?")]);

        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        var notice = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>()).Notice;
        Assert.Equal(("prompt_blocked", "input", "Project names stay out.", "d-1"),
            (notice.Outcome, notice.Stage, notice.Message, notice.DecisionId));
        Assert.Equal("", response.Text);
        if (!stream)
        {
            Assert.Equal(notice, response.GetGovernanceNotice());
        }
    }

    [Fact]
    public async Task A_refused_answer_ends_in_content_filter_with_the_notice()
    {
        var (client, server) = Make();
        server.On("POST", Route, _ => FakeServer.Sse(
            FakeServer.Data(Chunk(new { content = "The code name is " })),
            FakeServer.Faden(new { type = "CUSTOM", name = "faden.governance", value = new { outcome = "response_blocked", stage = "output", message = "Withheld.", decision_id = "d-2" } }),
            FakeServer.Data(Chunk(new { }, "content_filter")),
            FakeServer.Done));
        var response = await client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "code?")]).ToChatResponseAsync();
        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        var notice = response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>().Single().Notice;
        Assert.Equal(("response_blocked", "Withheld."), (notice.Outcome, notice.Message));
    }

    [Fact]
    public async Task Errors_are_typed_whichever_service_sent_them()
    {
        var (client, server) = Make();
        var hi = new[] { new ChatMessage(ChatRole.User, "hi") };

        server.Json("POST", Route, new { error = new { type = "permission_error", code = "agent_not_granted", message = "No grant." } }, HttpStatusCode.Forbidden);
        var forbidden = await Assert.ThrowsAsync<FadenPermissionException>(() => client.Chat.GetResponseAsync(hi));
        Assert.Equal("agent_not_granted", forbidden.Code);

        server.On("POST", Route, _ =>
        {
            var r = FakeServer.Respond((HttpStatusCode)429, new { error = new { type = "rate_limit_error", code = "rate_limit_rpm", message = "Slow down." } });
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return r;
        });
        var limited = await Assert.ThrowsAsync<FadenRateLimitedException>(() => client.Chat.GetResponseAsync(hi));
        Assert.Equal((TimeSpan.FromSeconds(17), false), (limited.RetryAfter!.Value, limited.IsBudget));

        server.Json("POST", Route, new { error = new { type = "invalid_request_error", code = "ee_required", message = "Enterprise only." } }, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<FadenFeatureNotAvailableException>(() => client.Chat.GetResponseAsync(hi));

        server.Json("POST", Route, new { detail = new { code = "no_grant", message = "You may not use this agent." } }, HttpStatusCode.Forbidden);
        var backend = await Assert.ThrowsAsync<FadenPermissionException>(() => client.Chat.GetResponseAsync(hi));
        Assert.Equal(("no_grant", "You may not use this agent."), (backend.Code, backend.Message));
    }

    [Fact]
    public void Plain_http_is_refused_unless_asked_for()
    {
        var error = Assert.Throws<ArgumentException>(() => new FadenClient(new FadenClientOptions { ServerUrl = new Uri("http://faden.local") }));
        Assert.Contains("https", error.Message);
        using var dev = new FadenClient(new FadenClientOptions { ServerUrl = new Uri("http://localhost:8002"), AllowInsecureHttp = true });
        Assert.NotNull(dev.Chat);
    }

    [Fact]
    public async Task Without_an_agent_the_plain_route_and_a_server_session()
    {
        var server = new FakeServer();
        server.Json("POST", "/v1/chat/completions", Whole(new { role = "assistant", content = "Hi." }));
        using var client = new FadenClient(new FadenClientOptions
        {
            ServerUrl = new Uri("https://faden.test/"),
            Credentials = new StaticTokenProvider("api-token"),
            Model = "qwen3-32b",
            HttpClient = new HttpClient(server),
        });
        Assert.Null(client.Agent);
        var response = await client.Chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions().WithSession("5f0c7a3e-0000-0000-0000-000000000001"));
        Assert.Equal("Hi.", response.Text);
        var body = server.Requests.Single().Json;
        Assert.Equal("qwen3-32b", body.GetProperty("model").GetString());
        Assert.Equal("5f0c7a3e-0000-0000-0000-000000000001", body.GetProperty("session_id").GetString());
    }
}

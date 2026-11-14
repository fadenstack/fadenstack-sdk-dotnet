using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Endpoints;
using Fadenstack.Governance;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>sdk/51 F03, I03 commits 3 to 6: the person's own endpoint as an IChatClient, and its transport rules.</summary>
public class EndpointClientTests
{
    private static (DirectChatClient Client, OpenAiFake Fake) Make(string baseUrl = "http://localhost:11434/v1",
        EndpointDialect? dialect = null, ITokenProvider? key = null, bool lan = false, string model = "qwen3:14b")
    {
        var fake = new OpenAiFake();
        var client = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri(baseUrl), Model = model, Key = key, Dialect = dialect ?? EndpointDialect.OpenAICompatible,
            AllowPlainHttpInLan = lan, Handler = fake,
        });
        return (client, fake);
    }

    private static async Task<(string Text, List<ChatResponseUpdate> Updates)> StreamAsync(DirectChatClient client,
        ChatOptions? options = null, params ChatMessage[] messages)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            messages.Length > 0 ? messages : [new ChatMessage(ChatRole.User, "Hi")], options))
        {
            updates.Add(update);
        }
        return (string.Concat(updates.Select(u => u.Text)), updates);
    }

    // ---- commit 3: the URLs ----

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions", "http://localhost:11434/api/show")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions", "http://localhost:11434/api/show")]
    [InlineData("https://res.openai.azure.com/openai/v1", "https://res.openai.azure.com/openai/v1/chat/completions", "https://res.openai.azure.com/openai/api/show")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/chat/completions", "https://openrouter.ai/api/api/show")]
    [InlineData("http://[::1]:8000/v1", "http://[::1]:8000/v1/chat/completions", "http://[::1]:8000/api/show")]
    [InlineData("https://llm.example/proxy", "https://llm.example/proxy/chat/completions", "https://llm.example/proxy/api/show")]
    public void A_base_resolves_with_or_without_a_slash(string baseUrl, string chat, string root)
    {
        Assert.Equal(chat, EndpointUrl.ChatCompletions(new Uri(baseUrl)).AbsoluteUri);
        Assert.Equal(chat.Replace("chat/completions", "models"), EndpointUrl.Models(new Uri(baseUrl)).AbsoluteUri);
        Assert.Equal(root, EndpointUrl.Root(new Uri(baseUrl), "api/show").AbsoluteUri);
    }

    [Theory]
    [InlineData("https://host/v1?api-version=1")]
    [InlineData("https://host/v1#x")]
    [InlineData("https://user:secret@host/v1")]
    [InlineData("ftp://host/v1")]
    public void A_base_with_a_query_fragment_user_info_or_another_scheme_is_refused(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => EndpointUrl.Base(new Uri(baseUrl)));
    }

    // ---- commit 4: the request ----

    [Fact]
    public async Task The_request_carries_nothing_of_the_gateway_and_the_endpoints_model()
    {
        var (client, fake) = Make();
        var options = new ChatOptions { Instructions = "Ours first.\n\nThen the person's." }
            .WithConversation("conv-1", "turn-1").WithToolMode(Tools.ToolMode.Auto).WithSession("s-1").WithSkills(["house-style"]);
        options.AdditionalProperties!["faden.something"] = "x";
        options.AdditionalProperties["user"] = "someone@example.com";
        options.AdditionalProperties["top_k"] = 40;

        var (text, _) = await StreamAsync(client, options, new ChatMessage(ChatRole.User, "Hi") { AuthorName = "Ana" });

        Assert.Equal("Hello.", text);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.Url.AbsoluteUri);
        Assert.DoesNotContain(request.Headers.Keys, h => h.StartsWith("x-faden", StringComparison.Ordinal));
        Assert.Null(request.Header("authorization"));
        var body = request.Json;
        Assert.Equal("qwen3:14b", body.GetProperty("model").GetString());
        foreach (var field in new[] { "session_id", "skills", "user", "faden.something", "watermark", "watermarking" })
        {
            Assert.False(body.TryGetProperty(field, out _), field);
        }
        Assert.Equal(40, body.GetProperty("top_k").GetInt32());
        var messages = body.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Ours first.\n\nThen the person's.", messages[0].GetProperty("content").GetString());
        Assert.False(messages[1].TryGetProperty("name", out _)); // the person's name stays on the device
    }

    [Fact]
    public async Task A_request_model_wins_and_no_model_at_all_is_refused()
    {
        var (client, fake) = Make();
        await StreamAsync(client, new ChatOptions { ModelId = "llama3.3" });
        Assert.Equal("llama3.3", fake.Requests[0].Json.GetProperty("model").GetString());

        var (none, nothing) = Make(model: "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]));
        Assert.Empty(nothing.Requests);
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public async Task Both_reasoning_field_names_are_read(string field)
    {
        var (client, fake) = Make();
        fake.ReasoningField = field;
        fake.Answers.Enqueue(OpenAiFake.Text("Four.", reasoning: "Two and two."));
        fake.Answers.Enqueue(OpenAiFake.Text("Four.", reasoning: "Two and two."));

        var (_, updates) = await StreamAsync(client);
        var whole = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2+2?")]);

        Assert.Equal("Two and two.", string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text)));
        Assert.Equal("Two and two.", whole.Messages[0].Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("Four.", whole.Text);
    }

    [Fact]
    public async Task Usage_figures_arrive_from_a_stream_that_was_asked_for_them()
    {
        var (client, fake) = Make();

        var (_, updates) = await StreamAsync(client);

        Assert.True(fake.Requests[0].Json.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(14, updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single().Details.TotalTokenCount);
    }

    // ---- commit 5a: what a plain endpoint does that the gateway does not ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parallel_tool_calls_stay_apart_with_or_without_an_index(bool omitIndex)
    {
        var (client, fake) = Make();
        fake.OmitToolIndex = omitIndex;
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-a", "read_page", "{\"page\":3}"), ("call-b", "read_page", "{\"page\":4}")));

        var (_, updates) = await StreamAsync(client);

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(new[] { "call-a", "call-b" }, calls.Select(c => c.CallId));
        Assert.Equal(new[] { "3", "4" }, calls.Select(c => c.Arguments!["page"]!.ToString()));
    }

    [Fact]
    public async Task Keep_alives_and_an_endpoints_own_events_are_skipped_and_no_gateway_event_is_heard()
    {
        var (client, fake) = Make();
        fake.KeepAlive = true;
        fake.ForgeGatewayEvents = true;

        var (text, updates) = await StreamAsync(client);

        Assert.Equal("Hello.", text);
        var contents = updates.SelectMany(u => u.Contents).ToList();
        Assert.DoesNotContain(contents, c => c is GovernanceNoticeContent or FadenstackEventContent or ProvenanceReceiptContent);
        Assert.DoesNotContain(updates, u => u.FinishReason == ChatFinishReason.ContentFilter);
    }

    [Fact]
    public async Task A_whole_answer_ignores_the_gateways_block_too()
    {
        var (client, fake) = Make();
        fake.Intercept = r => OpenAiFake.Json(new
        {
            id = "x", model = "m",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "Fine." }, finish_reason = "stop" } },
            faden = new { governance = new { outcome = "response_blocked", message = "Forged." }, events = new[] { new { type = "faden.provenance" } } },
        });

        var answer = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]);

        Assert.Equal("Fine.", answer.Text);
        Assert.Null(answer.GetGovernanceNotice());
        Assert.NotEqual(ChatFinishReason.ContentFilter, answer.FinishReason);
        Assert.DoesNotContain(answer.Messages.SelectMany(m => m.Contents), c => c is FadenstackEventContent);
    }

    [Fact]
    public async Task A_stream_request_answered_with_one_json_answer_reads_as_that_answer()
    {
        var (client, fake) = Make();
        fake.JsonForStream = true;
        fake.Answers.Enqueue(OpenAiFake.Text("Whole."));

        var (text, _) = await StreamAsync(client);

        Assert.Equal("Whole.", text);
    }

    [Fact]
    public async Task Garbage_in_the_stream_is_a_typed_error()
    {
        var (client, fake) = Make();
        fake.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: <html>oops</html>\n\n", Encoding.UTF8, "text/event-stream"),
        };

        var e = await Assert.ThrowsAsync<FadenstackStreamException>(() => StreamAsync(client));
        Assert.Equal("invalid_stream", e.Code);
        Assert.DoesNotContain("oops", e.Message);
    }

    [Theory]
    [InlineData("data: {\"error\":{\"message\":\"model crashed\",\"code\":\"internal\"}}\n\n", "model crashed")]
    [InlineData("data: {\"error\":\"out of memory\"}\n\n", "out of memory")]
    [InlineData("data: {\"object\":\"error\",\"message\":\"bad input\",\"type\":\"BadRequestError\"}\n\n", "bad input")]
    public async Task An_error_inside_the_stream_breaks_it_off_with_the_servers_words(string sse, string message)
    {
        var (client, fake) = Make();
        fake.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };

        var e = await Assert.ThrowsAsync<FadenstackStreamException>(() => StreamAsync(client));
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public async Task A_line_longer_than_the_limit_ends_the_stream()
    {
        var limits = new SseLimits { MaxLineChars = 64, MaxEventChars = 128, MaxTotalChars = 1024 };
        await Assert.ThrowsAsync<FadenstackStreamException>(() => ReadAll("data: " + new string('x', 100) + "\n\n", limits));
        await Assert.ThrowsAsync<FadenstackStreamException>(() => ReadAll("data: " + new string('x', 50) + "\ndata: " + new string('y', 50)
            + "\ndata: " + new string('z', 50) + "\n\n", limits));
        await Assert.ThrowsAsync<FadenstackStreamException>(() => ReadAll(string.Concat(Enumerable.Repeat("data: {}\n\n", 200)), limits));
        Assert.Equal(2, (await ReadAll("data: a\r\n\r\ndata: b\r\r", limits)).Count); // CRLF and CR end lines too
    }

    private static async Task<List<SseEvent>> ReadAll(string text, SseLimits limits)
    {
        var events = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), limits, default))
        {
            events.Add(e);
        }
        return events;
    }

    [Fact]
    public async Task Tool_arguments_past_the_limit_end_the_stream()
    {
        var (client, fake) = Make();
        var huge = new string('a', StreamAssembler.MaxArgumentChars / 2 + 10);
        fake.Answers.Enqueue(OpenAiFake.Calls(("c", "t", "{\"x\":\"" + huge + huge + "\"}")));

        var e = await Assert.ThrowsAsync<FadenstackStreamException>(() => StreamAsync(client));
        Assert.Equal("stream_too_large", e.Code);
    }

    // ---- commit 5: the dialects ----

    [Fact]
    public async Task Each_dialect_shapes_the_request_its_way()
    {
        var options = new ChatOptions { MaxOutputTokens = 100, Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        async Task<JsonElement> BodyOf(EndpointDialect dialect, ChatOptions o)
        {
            var (client, fake) = Make("https://api.example.test/v1", dialect, new StaticTokenProvider("k-123"));
            await StreamAsync(client, o);
            return fake.Requests[0].Json;
        }

        var openai = await BodyOf(EndpointDialect.OpenAI, options);
        Assert.Equal(100, openai.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(openai.TryGetProperty("max_tokens", out _));
        Assert.Equal("xhigh", openai.GetProperty("reasoning_effort").GetString());

        var ollama = await BodyOf(EndpointDialect.Ollama, options);
        Assert.Equal(100, ollama.GetProperty("max_tokens").GetInt32());
        Assert.Equal("high", ollama.GetProperty("reasoning_effort").GetString()); // xhigh only where it is known

        var vllm = await BodyOf(EndpointDialect.Vllm, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } });
        Assert.False(vllm.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.False(vllm.TryGetProperty("reasoning_effort", out _));
        var vllmOn = await BodyOf(EndpointDialect.Vllm, options);
        Assert.True(vllmOn.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());

        var lmstudio = await BodyOf(EndpointDialect.LMStudio, options);
        Assert.False(lmstudio.TryGetProperty("reasoning_effort", out _));
        Assert.False(lmstudio.TryGetProperty("chat_template_kwargs", out _));

        var mistral = await BodyOf(EndpointDialect.Mistral, options);
        Assert.False(mistral.TryGetProperty("stream_options", out _));
        Assert.False(mistral.TryGetProperty("reasoning_effort", out _));

        var sglang = await BodyOf(EndpointDialect.Sglang, options);
        Assert.True(sglang.GetProperty("watermark").GetProperty("enabled").GetBoolean());
        Assert.False(openai.TryGetProperty("watermark", out _));

        var plain = await BodyOf(EndpointDialect.Ollama, new ChatOptions());
        Assert.False(plain.TryGetProperty("reasoning_effort", out _)); // nothing asked, nothing sent
    }

    [Fact]
    public async Task The_key_goes_in_the_dialects_header()
    {
        var (bearer, b) = Make("https://api.example.test/v1", EndpointDialect.OpenAI, new StaticTokenProvider("k-1"));
        var (azure, a) = Make("https://res.openai.azure.com/openai/v1", EndpointDialect.AzureOpenAI, new StaticTokenProvider("k-2"));

        await StreamAsync(bearer);
        await StreamAsync(azure);

        Assert.Equal("Bearer k-1", b.Requests[0].Header("authorization"));
        Assert.Null(b.Requests[0].Header("api-key"));
        Assert.Equal("k-2", a.Requests[0].Header("api-key"));
        Assert.Null(a.Requests[0].Header("authorization"));
    }

    [Fact]
    public void Every_dialect_is_found_by_its_name_and_an_unknown_name_is_the_plain_one()
    {
        foreach (var dialect in EndpointDialect.All)
        {
            Assert.Same(dialect, EndpointDialect.Named(dialect.Name));
        }
        Assert.Same(EndpointDialect.OpenAICompatible, EndpointDialect.Named("something-new"));
        var json = JsonSerializer.Serialize(EndpointDialect.AzureOpenAI);
        Assert.Contains("\"ApiKey\"", json);
        Assert.Equal(EndpointDialect.AzureOpenAI, JsonSerializer.Deserialize<EndpointDialect>(json));
    }

    // ---- commit 6: the transport rules ----

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", "127.0.0.1")]
    [InlineData("http://[::1]:8000/v1", "[::1]")]
    public async Task A_key_goes_over_plain_http_to_this_computers_address(string baseUrl, string host)
    {
        var (client, fake) = Make(baseUrl, key: new StaticTokenProvider("k-123"));

        await StreamAsync(client);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(host, request.Url.Host);
        Assert.Equal("Bearer k-123", request.Header("authorization"));
    }

    [Fact]
    public async Task A_key_to_localhost_goes_to_the_address_it_resolves_to_and_the_name_stays_in_the_host_header()
    {
        var fake = new OpenAiFake();
        var client = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("http://localhost:4000/v1"), Model = "m", Key = new StaticTokenProvider("k"), Handler = fake,
            Resolve = _ => Task.FromResult(new[] { IPAddress.Loopback }),
        });

        await StreamAsync(client);

        Assert.Equal("127.0.0.1", fake.Requests[0].Url.Host);
        Assert.Equal("localhost:4000", fake.Requests[0].Header("host"));
    }

    [Theory]
    [InlineData("http://192.168.1.20:8000/v1", true)]
    [InlineData("http://10.0.0.5/v1", true)]
    [InlineData("http://203.0.113.5/v1", false)]
    public async Task A_key_never_goes_over_plain_http_beyond_this_computer_opt_in_or_not(string baseUrl, bool lan)
    {
        var (client, fake) = Make(baseUrl, key: new StaticTokenProvider("k-123"), lan: lan);

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => StreamAsync(client));

        Assert.Equal(PlainHttpRefusal.KeyOverPlainHttp, e.Reason);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1,192.168.1.9")]
    public async Task A_key_to_a_name_that_resolves_beyond_this_computer_is_refused(string resolvesTo)
    {
        var fake = new OpenAiFake();
        var client = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("http://llm.lan:8000/v1"), Model = "m", Key = new StaticTokenProvider("k"), Handler = fake, AllowPlainHttpInLan = true,
            Resolve = _ => Task.FromResult(resolvesTo.Split(',').Select(IPAddress.Parse).ToArray()),
        });

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => StreamAsync(client));

        Assert.Equal(PlainHttpRefusal.KeyOverPlainHttp, e.Reason);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_second_dns_answer_cannot_move_a_keyed_connection()
    {
        var fake = new OpenAiFake();
        var answers = new Queue<IPAddress[]>([[IPAddress.Loopback], [IPAddress.Parse("10.0.0.5")], [IPAddress.Parse("10.0.0.5")]]);
        var client = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("http://model-box:8000/v1"), Model = "m", Key = new StaticTokenProvider("k"), Handler = fake,
            Resolve = _ => Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek()),
        });

        await StreamAsync(client);
        var second = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => StreamAsync(client));

        Assert.Single(fake.Requests); // the first went to 127.0.0.1; the moved name is refused, never connected to
        Assert.Equal("127.0.0.1", fake.Requests[0].Url.Host);
        Assert.Equal(PlainHttpRefusal.KeyOverPlainHttp, second.Reason);
    }

    [Fact]
    public async Task A_name_that_keeps_resolving_to_this_computer_stays_on_the_first_address()
    {
        var fake = new OpenAiFake();
        var answers = new Queue<IPAddress[]>([[IPAddress.Parse("127.0.0.1")], [IPAddress.Parse("127.0.0.2")]]);
        var client = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("http://model-box:8000/v1"), Model = "m", Key = new StaticTokenProvider("k"), Handler = fake,
            Resolve = _ => Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek()),
        });

        await StreamAsync(client);
        await StreamAsync(client);

        Assert.All(fake.Requests, r => Assert.Equal("127.0.0.1", r.Url.Host)); // resolved once
    }

    [Theory]
    [InlineData("http://192.168.1.20:8000/v1", false, PlainHttpRefusal.NoOptIn)]
    [InlineData("http://10.0.0.5/v1", false, PlainHttpRefusal.NoOptIn)]
    [InlineData("http://[fd00::5]:8000/v1", false, PlainHttpRefusal.NoOptIn)]
    [InlineData("http://203.0.113.5/v1", true, PlainHttpRefusal.NotLocal)]
    [InlineData("http://100.64.0.1/v1", true, PlainHttpRefusal.NotLocal)]
    public async Task Plain_http_beyond_this_device_needs_the_opt_in_and_a_private_address(string baseUrl, bool lan,
        PlainHttpRefusal reason)
    {
        var (client, fake) = Make(baseUrl, lan: lan);

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => StreamAsync(client));

        Assert.Equal(reason, e.Reason);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://[::1]:8000/v1", false)]
    [InlineData("http://192.168.1.20:8000/v1", true)]
    [InlineData("http://169.254.10.1:8000/v1", true)]
    public async Task Plain_http_to_this_device_or_with_the_opt_in_to_the_lan_is_sent(string baseUrl, bool lan)
    {
        var (client, fake) = Make(baseUrl, lan: lan);

        var (text, _) = await StreamAsync(client);

        Assert.Equal("Hello.", text);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_refused_and_the_key_never_follows()
    {
        var (client, fake) = Make("https://api.example.test/v1", key: new StaticTokenProvider("k-123"));
        fake.Intercept = r => r.Url.Host == "api.example.test" ? OpenAiFake.Redirect(307, "https://evil.example/v1/chat/completions") : null;

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => StreamAsync(client));

        Assert.Equal("redirect", e.Code);
        Assert.Contains("evil.example", e.Message);
        Assert.DoesNotContain(fake.Requests, r => r.Url.Host == "evil.example");
    }

    [Theory]
    [InlineData("http://api.example.test/v1/chat/completions")] // https to http is another origin
    [InlineData("https://api.example.test:8443/v1/chat/completions")]
    public async Task A_redirect_to_another_scheme_or_port_is_another_origin(string location)
    {
        var (client, fake) = Make("https://api.example.test/v1", key: new StaticTokenProvider("k-123"));
        fake.Intercept = r => r.Url.AbsoluteUri == "https://api.example.test/v1/chat/completions" ? OpenAiFake.Redirect(308, location) : null;

        await Assert.ThrowsAsync<FadenstackApiException>(() => StreamAsync(client));

        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task A_redirect_on_the_same_origin_is_followed_with_the_body_and_the_key()
    {
        var (client, fake) = Make("https://api.example.test/v1", key: new StaticTokenProvider("k-123"));
        fake.Intercept = r => r.Url.AbsolutePath == "/v1/chat/completions" ? OpenAiFake.Redirect(307, "/v2/chat/completions") : null;

        var (text, _) = await StreamAsync(client);

        Assert.Equal("Hello.", text);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("Bearer k-123", fake.Requests[1].Header("authorization"));
        Assert.Equal(fake.Requests[0].Body, fake.Requests[1].Body);
    }

    [Fact]
    public async Task Redirects_go_round_at_most_three_times()
    {
        var (client, fake) = Make("https://api.example.test/v1");
        var n = 0;
        fake.Intercept = r => OpenAiFake.Redirect(307, $"/hop{++n}/chat/completions");

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => StreamAsync(client));

        Assert.Equal("redirect", e.Code);
        Assert.Equal(4, fake.Requests.Count);
    }

    // ---- commit 6: the errors ----

    public static TheoryData<int, object, Type> Errors() => new()
    {
        { 401, new { error = new { message = "Incorrect API key provided", code = "invalid_api_key" } }, typeof(EndpointKeyRejectedException) },
        { 403, new { error = new { message = "Key not allowed", code = "forbidden" } }, typeof(EndpointKeyRejectedException) },
        { 404, new { error = new { message = "The model `gpt-9` does not exist", code = "model_not_found" } }, typeof(EndpointModelNotFoundException) },
        { 404, new { error = "model \"qwen9\" not found, try pulling it first" }, typeof(EndpointModelNotFoundException) },
        { 404, "404 page not found", typeof(EndpointNotCompatibleException) },
        { 405, "Method Not Allowed", typeof(EndpointNotCompatibleException) },
        { 400, new { error = new { message = "This model's maximum context length is 4096 tokens.", code = "context_length_exceeded" } }, typeof(EndpointContextLengthException) },
        { 400, new { @object = "error", message = "the request exceeds the available context size", type = "exceed_context_size_error" }, typeof(EndpointContextLengthException) },
        { 500, new { error = new { message = "boom" } }, typeof(FadenstackApiException) },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Each_error_maps_to_its_type(int status, object body, Type type)
    {
        var (client, fake) = Make("https://api.example.test/v1", key: new StaticTokenProvider("k-123"));
        fake.Intercept = _ => OpenAiFake.Error(status, body);

        var e = await Assert.ThrowsAnyAsync<FadenstackApiException>(() => StreamAsync(client));

        Assert.IsType(type, e);
        Assert.Equal((HttpStatusCode)status, e.Status);
        Assert.DoesNotContain("k-123", e.Message);
    }

    [Fact]
    public async Task A_403_without_a_key_is_the_origin_refused()
    {
        var (client, fake) = Make();
        fake.Intercept = _ => OpenAiFake.Error(403, "");

        await Assert.ThrowsAsync<EndpointOriginRefusedException>(() => StreamAsync(client));
    }

    [Fact]
    public async Task A_429_is_the_rate_limit_with_its_retry_after()
    {
        var (client, fake) = Make("https://api.example.test/v1", key: new StaticTokenProvider("k"));
        fake.Intercept = _ => OpenAiFake.Error(429, new { error = new { message = "slow down", code = "rate_limit_exceeded" } }, ("Retry-After", "7"));

        var e = await Assert.ThrowsAsync<FadenstackRateLimitedException>(() => StreamAsync(client));

        Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
        Assert.Equal("rate_limit_exceeded", e.Code);
    }

    [Fact]
    public async Task A_401_is_tried_once_more_only_with_a_new_key()
    {
        var keys = new RotatingKey();
        var (client, fake) = Make("https://api.example.test/v1", key: keys);
        fake.Intercept = r => r.Header("authorization") == "Bearer old" ? OpenAiFake.Error(401, new { error = new { message = "expired" } }) : null;

        var (text, _) = await StreamAsync(client);
        Assert.Equal("Hello.", text);
        Assert.Equal(2, fake.Requests.Count);

        var (fixedKey, again) = Make("https://api.example.test/v1", key: new StaticTokenProvider("same"));
        again.Intercept = _ => OpenAiFake.Error(401, new { error = new { message = "bad key" } });
        await Assert.ThrowsAsync<EndpointKeyRejectedException>(() => StreamAsync(fixedKey));
        Assert.Single(again.Requests);
    }

    private sealed class RotatingKey : ITokenProvider
    {
        private string _key = "old";

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(_key);

        public Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken = default)
        {
            _key = "new";
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Nothing_answering_is_unreachable_and_a_bad_certificate_says_so()
    {
        var down = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("https://api.example.test/v1"), Model = "m",
            Handler = new Throwing(new HttpRequestException("No connection could be made")),
        });
        var tls = new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("https://api.example.test/v1"), Model = "m",
            Handler = new Throwing(new HttpRequestException("SSL", new AuthenticationException("remote certificate is invalid"))),
        });

        await Assert.ThrowsAsync<EndpointUnreachableException>(() => StreamAsync(down));
        await Assert.ThrowsAsync<ServerCertificateException>(() => StreamAsync(tls));
    }

    private sealed class Throwing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    [Fact]
    public async Task An_answer_that_is_no_chat_completion_is_not_compatible()
    {
        var (client, fake) = Make();
        fake.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router login</html>", Encoding.UTF8, "text/html") };

        await Assert.ThrowsAsync<EndpointNotCompatibleException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]));
    }

    // ---- the watermark as an answer reports it (F03 item 8, F10) ----

    [Theory]
    [InlineData("vllm")]
    [InlineData("sglang")]
    [InlineData("ollama")]
    public async Task A_request_for_text_people_read_never_switches_the_watermark_off(string dialect)
    {
        var (client, fake) = Make("https://api.example.test/v1", EndpointDialect.Named(dialect), new StaticTokenProvider("k"));
        var options = new ChatOptions { AdditionalProperties = new() { ["watermarking"] = false, ["watermark"] = new { enabled = false } } };

        await StreamAsync(client, options);

        var body = fake.Requests[0].Json;
        Assert.False(body.TryGetProperty("watermarking", out _));
        Assert.Equal(dialect == "sglang", body.TryGetProperty("watermark", out var w) && w.GetProperty("enabled").GetBoolean());
        if (dialect != "sglang")
        {
            Assert.False(body.TryGetProperty("watermark", out _));
        }
    }

    [Theory]
    [InlineData("vllm", "{\"watermarking\":false}")]
    [InlineData("sglang", "{\"watermark\":{\"enabled\":false}}")]
    [InlineData("ollama", "{}")]
    public async Task An_answer_nobody_reads_may_go_without_the_watermark_where_the_server_has_the_switch(string dialect, string expected)
    {
        var (client, fake) = Make("https://api.example.test/v1", EndpointDialect.Named(dialect), new StaticTokenProvider("k"));
        var options = new ChatOptions { AdditionalProperties = new() { [FadenstackChatOptions.Unread] = true } };

        var (_, updates) = await StreamAsync(client, options);

        var body = fake.Requests[0].Json;
        using var want = JsonDocument.Parse(expected);
        foreach (var field in want.RootElement.EnumerateObject())
        {
            Assert.Equal(field.Value.GetRawText(), body.GetProperty(field.Name).GetRawText());
        }
        if (dialect == "ollama")
        {
            Assert.False(body.TryGetProperty("watermarking", out _));
            Assert.False(body.TryGetProperty("watermark", out _));
        }
        Assert.False(body.TryGetProperty(FadenstackChatOptions.Unread, out _));
        var marking = (EndpointMarking)updates.Last().AdditionalProperties![FadenstackChatOptions.EndpointMarking]!;
        Assert.False(marking.Requested);
    }


    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, null)]
    public async Task The_x_watermark_header_is_read_per_answer(string? header, bool? confirmed)
    {
        var (client, fake) = Make("https://api.example.test/v1");
        if (header is not null)
        {
            fake.ChatHeaders["x-watermark"] = header;
        }

        var (_, updates) = await StreamAsync(client, new ChatOptions { Temperature = 0 });
        var marking = (EndpointMarking)updates.Last().AdditionalProperties![FadenstackChatOptions.EndpointMarking]!;

        Assert.Equal(confirmed, marking.Confirmed);
        Assert.True(marking.Deterministic);
        Assert.False(marking.Requested);
    }
}

using System.Net;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>What the client logs, traces and counts: names, routes, statuses and durations -- never content.</summary>
public class LoggingTests
{
    private const string Sentinel = "SENTINEL-4f1c9";

    private static FadenstackClient Make(FakeServer server, ILoggerFactory? logs = null, ITokenProvider? tokens = null) =>
        new(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"),
            Agent = "docs-assistant",
            Credentials = tokens ?? new StaticTokenProvider("agent-token"),
            HttpClient = new HttpClient(server),
            LoggerFactory = logs!,
        });

    [Fact]
    public async Task A_request_is_logged_by_method_route_template_status_and_duration()
    {
        var server = new FakeServer().Json("GET", "/v1/sessions/s-123/messages", new { ok = true });
        var logs = new CapturingLoggerFactory();
        using var telemetry = new TelemetryCapture();
        using var client = Make(server, logs);

        await client.Transport.GetAsync<JsonElement>(client.Transport.Gateway($"v1/sessions/s-123/messages?q={Sentinel}"));

        var entry = Assert.Single(logs.Entries, e => e.EventId == 1000);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Fadenstack.Client", entry.Category);
        Assert.Matches(@"^GET /v1/sessions/\{id\}/messages answered 200 in \d+ ms \(attempt 0\)$", entry.Message);
        Assert.DoesNotContain(logs.Texts, t => t.Contains("s-123") || t.Contains(Sentinel) || t.Contains("agent-token"));
        Assert.Contains(telemetry.Spans, s => s.StartsWith("HTTP GET") && s.Contains("http.route=/v1/sessions/{id}/messages")
            && s.Contains("http.response.status_code=200"));
        Assert.DoesNotContain(telemetry.Texts, t => t.Contains("s-123") || t.Contains(Sentinel));
    }

    [Fact]
    public async Task An_error_with_a_body_is_logged_as_its_status_and_never_its_body()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/docs-assistant/chat/completions",
            new { error = new { message = $"The prompt {Sentinel} broke it.", code = "boom" } }, HttpStatusCode.InternalServerError);
        var logs = new CapturingLoggerFactory();
        using var telemetry = new TelemetryCapture();
        using var client = Make(server, logs);

        var error = await Assert.ThrowsAsync<FadenstackApiException>(() => client.Chat.GetResponseAsync([new ChatMessage(ChatRole.User, $"my {Sentinel}")]));

        Assert.Contains(Sentinel, error.Message); // the caller still gets the server's word
        Assert.Contains(logs.Entries, e => e.EventId == 1000 && e.Message.Contains("answered 500"));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(Sentinel));
        Assert.DoesNotContain(telemetry.Texts, t => t.Contains(Sentinel));
    }

    [Fact]
    public async Task A_request_that_got_no_answer_is_logged_by_the_kind_of_failure_only()
    {
        var logs = new CapturingLoggerFactory();
        var server = new FakeServer().On("GET", "/v1/features", _ => throw new HttpRequestException($"no route to {Sentinel}"));
        using var client = Make(server, logs);

        await Assert.ThrowsAsync<FadenstackException>(() => client.GetFeaturesAsync());

        Assert.Contains(logs.Entries, e => e.EventId == 1001 && e.Level == LogLevel.Warning && e.Message.Contains("HttpRequestException"));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(Sentinel));
    }

    [Fact]
    public async Task A_skipped_malformed_faden_event_is_logged_without_what_it_held()
    {
        var server = new FakeServer().On("POST", "/v1/agents/docs-assistant/chat/completions", _ => FakeServer.Sse(
            $"event: faden\ndata: {{\"leak\": {Sentinel} nope\n\n",
            $"event: faden\ndata: [\"{Sentinel}\"]\n\n",
            FakeServer.Data(new { id = "c", choices = new[] { new { index = 0, delta = new { content = "hello" } } } }),
            FakeServer.Done));
        var logs = new CapturingLoggerFactory();
        using var client = Make(server, logs);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Contains(updates, u => u.Text == "hello");
        Assert.Equal(new[] { "A faden event was skipped: it is not valid JSON", "A faden event was skipped: it is not a JSON object" },
            logs.Entries.Where(e => e.EventId == 1020).Select(e => e.Message));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(Sentinel));
    }

    [Fact]
    public async Task A_renewed_sign_in_is_logged_without_any_token_and_a_refused_one_with_its_status()
    {
        var now = DateTimeOffset.UtcNow;
        object Issued(string access, string refresh) => new
        {
            access_token = access, token_type = "bearer", expires_in = 0, refresh_token = refresh, refresh_expires_in = 86400,
            device_id = "dev-1", agent_id = "a-1", agent = "docs-assistant",
        };
        var server = new FakeServer()
            .Json("POST", "/api/agent-tokens", Issued("access-secret-1", "refresh-secret-1"), HttpStatusCode.Created)
            .Json("POST", "/api/agent-tokens/refresh", Issued("access-secret-2", "refresh-secret-2"));
        var logs = new CapturingLoggerFactory();
        var tokens = new AgentTokenProvider(new Uri("https://faden.test"), "docs-assistant", null, new HttpClient(server), () => now,
            logger: logs.CreateLogger("test"));
        await tokens.SignInAsync("login-jwt");

        await tokens.GetAccessTokenAsync();

        Assert.Equal("The sign-in to agent docs-assistant was renewed", Assert.Single(logs.Entries, e => e.EventId == 1010).Message);
        Assert.DoesNotContain(logs.Texts, t => t.Contains("secret") || t.Contains("login-jwt"));

        server.On("POST", "/api/agent-tokens/refresh", _ => FakeServer.Respond(HttpStatusCode.Unauthorized, new { detail = new { message = $"{Sentinel}" } }));
        await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => tokens.GetAccessTokenAsync());
        Assert.Contains(logs.Entries, e => e.EventId == 1011 && e.Message.Contains("status 401"));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(Sentinel));
    }

    [Fact]
    public async Task Without_a_logger_factory_nothing_is_logged_and_nothing_fails()
    {
        var options = new FadenstackClientOptions { LoggerFactory = null! };
        Assert.Same(NullLoggerFactory.Instance, options.LoggerFactory);
        var server = new FakeServer().Json("GET", "/v1/features", new { edition = "community", features = Array.Empty<string>() });
        using var client = Make(server);

        await client.GetFeaturesAsync();

        Assert.Same(NullLoggerFactory.Instance, client.Options.LoggerFactory);
    }

    [Theory]
    [InlineData("https://x.test/v1/agents/docs-assistant/chat/completions?stream=1", "/v1/agents/docs-assistant/chat/completions")]
    [InlineData("https://x.test/v1/sessions/0f3a/messages/42", "/v1/sessions/{id}/messages/{id}")]
    [InlineData("https://x.test/api/agent-tokens/refresh", "/api/agent-tokens/refresh")]
    [InlineData("https://x.test/v1/files/Quarterly%20Report.docx", "/v1/files/{id}")]
    [InlineData("https://x.test/", "/")]
    public void A_route_keeps_plain_words_and_hides_the_rest(string url, string template) =>
        Assert.Equal(template, RouteTemplate.Of(new Uri(url)));
}

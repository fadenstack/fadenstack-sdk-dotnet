using System.Net;
using System.Runtime.InteropServices;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>Agent sign-in: tokens for one agent on one device, renewed, and given up when the server says so.</summary>
public class AuthTests
{
    private static object Issued(string access, string refresh, int expiresIn = 600) => new
    {
        access_token = access,
        token_type = "bearer",
        expires_in = expiresIn,
        refresh_token = refresh,
        refresh_expires_in = 86400,
        device_id = "11111111-2222-3333-4444-555555555555",
        agent_id = "a-1",
        agent = "docs-assistant",
    };

    [Fact]
    public async Task Sign_in_then_the_access_token_is_renewed_and_the_refresh_token_replaced()
    {
        var now = DateTimeOffset.Parse("2026-09-30T08:00:00Z");
        var server = new FakeServer()
            .Json("POST", "/api/agent-tokens", Issued("access-1", "refresh-1"), HttpStatusCode.Created)
            .Json("POST", "/api/agent-tokens/refresh", Issued("access-2", "refresh-2"));
        var store = new MemoryTokenStore();
        var tokens = new AgentTokenProvider(new Uri("https://faden.test"), "docs-assistant", store, new HttpClient(server), () => now);

        await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => tokens.GetAccessTokenAsync());
        var signedIn = await tokens.SignInAsync("login-jwt", "Viewer on PC-7");
        Assert.Equal("11111111-2222-3333-4444-555555555555", signedIn.DeviceId);
        var issue = server.Requests.Single();
        Assert.Equal("Bearer login-jwt", issue.Header("Authorization"));
        Assert.Equal(("docs-assistant", "Viewer on PC-7"),
            (issue.Json.GetProperty("agent").GetString(), issue.Json.GetProperty("device_label").GetString()));

        Assert.Equal("access-1", await tokens.GetAccessTokenAsync());
        now = now.AddMinutes(10); // expired
        Assert.Equal("access-2", await tokens.GetAccessTokenAsync());
        var refresh = server.Requests.Last();
        Assert.Equal("refresh-1", refresh.Json.GetProperty("refresh_token").GetString());
        Assert.Null(refresh.Header("Authorization"));
        Assert.Equal("refresh-2", (await store.LoadAsync("https://faden.test/|docs-assistant"))!.RefreshToken);
    }

    [Fact]
    public async Task A_refused_refresh_ends_the_sign_in()
    {
        var now = DateTimeOffset.UtcNow;
        var server = new FakeServer()
            .Json("POST", "/api/agent-tokens", Issued("access-1", "refresh-1", expiresIn: 0), HttpStatusCode.Created)
            .Json("POST", "/api/agent-tokens/refresh", new { detail = new { code = "refresh_reused", message = "Signed out everywhere." } }, HttpStatusCode.Unauthorized);
        var store = new MemoryTokenStore();
        var tokens = new AgentTokenProvider(new Uri("https://faden.test"), "docs-assistant", store, new HttpClient(server), () => now);
        await tokens.SignInAsync("login-jwt");
        var ended = await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => tokens.GetAccessTokenAsync());
        Assert.Contains("Signed out everywhere.", ended.Message);
        Assert.Null(await store.LoadAsync("https://faden.test/|docs-assistant"));
        Assert.False(await tokens.IsSignedInAsync());
    }

    [Fact]
    public async Task Many_callers_at_once_refresh_once()
    {
        var now = DateTimeOffset.UtcNow;
        var server = new FakeServer()
            .Json("POST", "/api/agent-tokens", Issued("access-1", "refresh-1", expiresIn: 0), HttpStatusCode.Created)
            .Json("POST", "/api/agent-tokens/refresh", Issued("access-2", "refresh-2"));
        var tokens = new AgentTokenProvider(new Uri("https://faden.test"), "docs-assistant", null, new HttpClient(server), () => now);
        await tokens.SignInAsync("login-jwt");
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tokens.GetAccessTokenAsync()));
        Assert.All(all, t => Assert.Equal("access-2", t));
        Assert.Single(server.Requests, r => r.Path == "/api/agent-tokens/refresh");
    }

    [Fact]
    public async Task A_rejected_token_is_renewed_and_the_request_tried_once_more()
    {
        var now = DateTimeOffset.UtcNow;
        var server = new FakeServer()
            .Json("POST", "/api/agent-tokens", Issued("access-1", "refresh-1"), HttpStatusCode.Created)
            .Json("POST", "/api/agent-tokens/refresh", Issued("access-2", "refresh-2"))
            .On("POST", "/v1/agents/docs-assistant/chat/completions", r => r.Header("Authorization") == "Bearer access-2"
                ? FakeServer.Respond(HttpStatusCode.OK, new { id = "c", choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } } })
                : FakeServer.Respond(HttpStatusCode.Unauthorized, new { error = new { code = "invalid_token", message = "Invalid JWT token." } }));
        var tokens = new AgentTokenProvider(new Uri("https://faden.test"), "docs-assistant", null, new HttpClient(server), () => now);
        await tokens.SignInAsync("login-jwt");
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "docs-assistant", Credentials = tokens, HttpClient = new HttpClient(server),
        });
        var response = await client.Chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(2, server.Requests.Count(r => r.Path.EndsWith("chat/completions")));
    }

    [Theory]
    [InlineData("http://ai.example.internal", false, false)]
    [InlineData("http://192.0.2.10:8000", false, false)]
    [InlineData("ftp://faden.test", true, false)]
    [InlineData("http://ai.example.internal", true, true)]
    [InlineData("http://localhost:8000", false, true)]
    [InlineData("http://127.0.0.1:8000", false, true)]
    [InlineData("http://127.4.5.6", false, true)]
    [InlineData("http://[::1]:8000", false, true)]
    [InlineData("https://ai.example.internal", false, true)]
    public void Sign_ins_go_over_https_or_plain_http_only_to_this_machine_unless_allowed(string url, bool allowInsecureHttp, bool accepted)
    {
        AgentTokenProvider Create() => new(new Uri(url), "docs-assistant", allowInsecureHttp: allowInsecureHttp);
        if (accepted)
        {
            Create();
        }
        else
        {
            var refused = Assert.Throws<ArgumentException>(Create);
            Assert.Contains("must be https unless allowInsecureHttp is set", refused.Message);
        }
    }

    [Fact]
    public void A_client_for_an_agent_passes_its_http_setting_to_the_sign_in()
    {
        using var client = FadenstackClient.ForAgent(new Uri("http://ai.example.internal"), "docs-assistant", out var tokens,
            new MemoryTokenStore(), o => o.AllowInsecureHttp = true);
        Assert.NotNull(tokens);
        Assert.Throws<ArgumentException>(() => FadenstackClient.ForAgent(new Uri("http://ai.example.internal"), "docs-assistant", out _,
            new MemoryTokenStore()));
    }

    [Fact]
    public async Task The_file_store_keeps_the_sign_in_for_its_owner_only()
    {
        var directory = Path.Combine(Path.GetTempPath(), "faden-tokens-" + Guid.NewGuid());
        var store = new FileTokenStore(directory);
        var tokens = new TokenSet { AccessToken = "a", RefreshToken = "r", DeviceId = "d", RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        await store.SaveAsync("https://faden.test/|docs", tokens);
        Assert.Equal(tokens, await new FileTokenStore(directory).LoadAsync("https://faden.test/|docs"));
        var file = Directory.GetFiles(directory).Single();
        Assert.DoesNotContain("docs", Path.GetFileName(file));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        await store.DeleteAsync("https://faden.test/|docs");
        Assert.Null(await store.LoadAsync("https://faden.test/|docs"));
        Directory.Delete(directory, true);
    }
}

/// <summary>The agent's profile, reports, and the tool-mode table the gateway applies.</summary>
public class AgentResourceTests
{
    [Fact]
    public async Task The_profile_reads_as_the_gateway_sends_it()
    {
        var server = new FakeServer().Json("GET", "/v1/agents/docs-assistant/profile", """
            {"object":"agent.profile","id":"a-1","slug":"docs-assistant","name":"Docs","version":3,"draft":false,
             "model":"qwen3-32b","instructions_sha256":"abc",
             "host_tool_contract":[{"name":"read_page","risk":"read"},{"name":"delete_page","risk":"destructive"},{"name":"stamp"}],
             "server_tools":null,"knowledge":[],"local_mcp":{"mode":"allow","developer_mode":true},
             "tool_modes":{"default":"ask","ceiling":"ask","open_world_enforced":false},
             "ui":{"greeting":"Hi"},"extensions":{},"required_client_modules":[],"edition":"community","features":["agents"]}
            """);
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "docs-assistant",
            Credentials = new StaticTokenProvider("t"), HttpClient = new HttpClient(server),
        });
        var profile = await client.Agent!.GetProfileAsync();
        Assert.Equal(("a-1", 3, "qwen3-32b"), (profile.Id, profile.Version, profile.Model));
        Assert.Equal(new[] { ToolClass.Read, ToolClass.Destructive, ToolClass.Unknown },
            profile.HostToolContract.Select(t => ToolModes.ParseClass(t.Risk)));
        Assert.Equal(("allow", true), (profile.LocalMcp.Mode, profile.LocalMcp.DeveloperMode));
        Assert.Equal("ask", profile.ToolModes.Ceiling);
        Assert.Null(profile.ServerTools);
    }

    [Fact]
    public async Task Reports_carry_names_and_counts_in_the_servers_shape()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/docs-assistant/events", new { accepted = 2 }, HttpStatusCode.Accepted);
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "docs-assistant",
            Credentials = new StaticTokenProvider("t"), HttpClient = new HttpClient(server),
        });
        await client.Agent!.ReportAsync([
            new ToolExecutedEvent { Tool = "read_page", Source = "host", DurationMs = 12, Outcome = "ok", ConversationId = "c-1" },
            new ApprovalDecidedEvent { Tool = "delete_page", Decision = "denied", WaitMs = 4000 },
            new ApprovalDecidedEvent { Tool = "add_comment", Decision = "approved", Scope = "always" },
        ]);
        var events = server.Requests.Single().Json.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal("tool.executed", events[0].GetProperty("type").GetString());
        Assert.Equal(12, events[0].GetProperty("duration_ms").GetInt32());
        Assert.False(events[0].TryGetProperty("server", out _), "no nulls: the server refuses unknown and empty fields alike");
        Assert.Equal(("approval.decided", "denied"), (events[1].GetProperty("type").GetString(), events[1].GetProperty("decision").GetString()));
        Assert.False(events[1].TryGetProperty("scope", out _));
        Assert.Equal("always", events[2].GetProperty("scope").GetString());
        Assert.False(events[2].TryGetProperty("wait_ms", out _)); // nobody was asked
    }

    [Theory]
    [InlineData(ToolMode.Off, "hidden,hidden,hidden,hidden")]
    [InlineData(ToolMode.ReadOnly, "run,hidden,hidden,hidden")]
    [InlineData(ToolMode.Ask, "run,ask,ask,ask")]
    [InlineData(ToolMode.Auto, "run,run,ask,ask")]
    public void The_table_is_the_gateways(ToolMode mode, string expected)
    {
        var classes = new[] { ToolClass.Read, ToolClass.Write, ToolClass.Destructive, ToolClass.Unknown };
        Assert.Equal(expected, string.Join(",", classes.Select(c => ToolModes.Decide(mode, c).ToString().ToLowerInvariant())));
        Assert.Equal(ToolMode.ReadOnly, ToolModes.Narrowest(ToolMode.Auto, ToolMode.ReadOnly));
        Assert.Equal(ToolMode.Ask, ToolModes.Narrowest(ToolMode.Ask, ToolMode.Auto));
        Assert.Equal(mode, ToolModes.ParseMode(mode.ToWire()));
    }
}

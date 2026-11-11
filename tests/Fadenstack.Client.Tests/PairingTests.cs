using System.Net;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>Sign-in by pairing: the code, the polling rules, the saved sign-in, and signing out.</summary>
public class PairingTests
{
    private const string Pairing = "PAIRING-SECRET-9f3a";
    private const string Agent = "docs-assistant";
    private static readonly Uri Server = new("https://faden.test");
    private static readonly string Key = "https://faden.test/|" + Agent;
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static readonly PairingClient App = new("Desktop assistant", "1.2.3", "Windows 11");

    private static object StartAnswer(int interval = 5) => new
    {
        pairing_code = Pairing,
        user_code = "BDWP-HTQX",
        verification_uri = "https://faden.test/pair",
        verification_uri_complete = "https://faden.test/pair?code=BDWP-HTQX",
        expires_in = 600,
        interval,
    };

    private static object Tokens(bool withUser = true) => new
    {
        access_token = "access-SECRET-1",
        token_type = "bearer",
        expires_in = 600,
        refresh_token = "refresh-SECRET-1",
        refresh_expires_in = 86400,
        device_id = "11111111-2222-3333-4444-555555555555",
        agent_id = "a-1",
        agent = Agent,
        user = withUser ? new { id = UserId, email = "anna@example.com", display_name = "Anna Weber" } : null,
    };

    private static HttpResponseMessage Pending(string code) => FakeServer.Respond(HttpStatusCode.BadRequest, new { code });

    /// <summary>A server that answers the polls from a script, and a clock the delays move.</summary>
    private sealed class Rig
    {
        public FakeServer Server { get; } = new();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-30T10:00:00Z");
        public List<TimeSpan> Waits { get; } = new();
        public CapturingLoggerFactory Logs { get; } = new();
        public MemoryTokenStore Store { get; } = new();
        public Queue<Func<HttpResponseMessage>> Polls { get; } = new();

        public Rig()
        {
            Server.Json("POST", "/api/agent-tokens/pairings", StartAnswer(), HttpStatusCode.Created);
            Server.On("POST", "/api/agent-tokens/pairings/token", _ => Polls.Dequeue()());
        }

        public AgentPairing Pairing() => new(PairingTests.Server, new HttpClient(Server), Store, () => Now,
            (span, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Waits.Add(span);
                Now += span;
                return Task.CompletedTask;
            }, logger: Logs.CreateLogger("Fadenstack.Client"));

        public Rig Poll(params Func<HttpResponseMessage>[] answers)
        {
            foreach (var a in answers)
            {
                Polls.Enqueue(a);
            }
            return this;
        }

        public int PollCount => Server.Requests.Count(r => r.Path.EndsWith("/token"));
    }

    private sealed class Progress : IProgress<PairingProgress>
    {
        public List<PairingProgress> Seen { get; } = new();

        public void Report(PairingProgress value) => Seen.Add(value);
    }

    [Fact]
    public async Task Start_sends_the_agent_device_and_app_and_returns_what_to_show()
    {
        var rig = new Rig();
        var start = await rig.Pairing().StartAsync(Agent, "Desktop agent on PC-7", App);

        var sent = rig.Server.Requests.Single();
        Assert.Null(sent.Header("Authorization"));
        Assert.Equal((Agent, "Desktop agent on PC-7"),
            (sent.Json.GetProperty("agent").GetString(), sent.Json.GetProperty("device_label").GetString()));
        var client = sent.Json.GetProperty("client");
        Assert.Equal(("Desktop assistant", "1.2.3", "Windows 11"),
            (client.GetProperty("name").GetString(), client.GetProperty("version").GetString(), client.GetProperty("os").GetString()));
        Assert.Equal("BDWP-HTQX", start.UserCode);
        Assert.Equal("https://faden.test/pair?code=BDWP-HTQX", start.VerificationUriComplete.AbsoluteUri);
        Assert.Equal("https://faden.test/pair", start.VerificationUri.AbsoluteUri);
        Assert.Equal(rig.Now.AddSeconds(600), start.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(5), start.Interval);
    }

    [Fact]
    public async Task The_pairing_code_is_not_in_the_start_object_the_text_or_the_log()
    {
        var rig = new Rig().Poll(() => Pending("authorization_pending"), () => FakeServer.Respond(HttpStatusCode.Created, Tokens()));
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Agent, "PC", App);
        await pairing.WaitAsync(start);

        Assert.DoesNotContain(Pairing, start.ToString());
        Assert.DoesNotContain(start.GetType().GetProperties(), p => p.GetValue(start)?.ToString()?.Contains(Pairing) == true);
        Assert.NotEmpty(rig.Logs.Texts);
        Assert.DoesNotContain(rig.Logs.Texts, t => t.Contains(Pairing) || t.Contains("SECRET") || t.Contains("BDWP") || t.Contains("anna@"));
        // and what polls sends is the code, in the body only
        Assert.All(rig.Server.Requests.Where(r => r.Path.EndsWith("/token")), r =>
        {
            Assert.Equal(Pairing, r.Json.GetProperty("pairing_code").GetString());
            Assert.DoesNotContain(Pairing, r.Query);
        });
    }

    [Fact]
    public async Task Pending_polls_then_approval_saves_the_tokens_and_the_user_where_a_provider_finds_them()
    {
        var rig = new Rig().Poll(() => Pending("authorization_pending"), () => Pending("authorization_pending"),
            () => Pending("authorization_pending"), () => FakeServer.Respond(HttpStatusCode.Created, Tokens()));
        var progress = new Progress();
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Agent, "PC", App);
        var result = await pairing.WaitAsync(start, progress);

        Assert.Equal(4, rig.PollCount);
        Assert.All(rig.Waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
        Assert.Equal(new SignedInUser(UserId, "anna@example.com", "Anna Weber"), result.SignedInUser);
        Assert.Equal((Agent, Guid.Parse("11111111-2222-3333-4444-555555555555")), (result.AgentSlug, result.DeviceId));
        var saved = (await rig.Store.LoadAsync(Key))!;
        Assert.Equal(("access-SECRET-1", "refresh-SECRET-1"), (saved.AccessToken, saved.RefreshToken));
        Assert.Equal(result.SignedInUser, saved.User);
        Assert.Equal(rig.Now.AddSeconds(600), saved.AccessExpiresAt);

        Assert.All(progress.Seen, p => Assert.Equal(PairingState.Waiting, p.State));
        Assert.True(progress.Seen[0].TimeLeft > progress.Seen[^1].TimeLeft);

        // a provider for the same server, agent and store is signed in, and knows who
        var provider = new AgentTokenProvider(Server, Agent, rig.Store, new HttpClient(rig.Server), () => rig.Now);
        Assert.True(await provider.IsSignedInAsync());
        Assert.Equal("access-SECRET-1", await provider.GetAccessTokenAsync());
        Assert.Equal("Anna Weber", provider.SignedInUser!.DisplayName);
        Assert.Equal("anna@example.com", (await provider.GetSignedInUserAsync())!.Email);
    }

    [Fact]
    public async Task The_server_may_leave_out_the_user()
    {
        var rig = new Rig().Poll(() => FakeServer.Respond(HttpStatusCode.Created, Tokens(withUser: false)));
        var pairing = rig.Pairing();
        var result = await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App));
        Assert.Null(result.SignedInUser);
        Assert.Null((await rig.Store.LoadAsync(Key))!.User);
    }

    [Fact]
    public async Task Slow_down_adds_five_seconds_for_every_later_poll()
    {
        var rig = new Rig().Poll(() => Pending("authorization_pending"), () => Pending("slow_down"),
            () => Pending("authorization_pending"), () => Pending("slow_down"), () => FakeServer.Respond(HttpStatusCode.Created, Tokens()));
        var progress = new Progress();
        var pairing = rig.Pairing();
        await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App), progress);

        Assert.Equal(new[] { 5, 5, 10, 10, 15 }, rig.Waits.Select(w => (int)w.TotalSeconds));
        Assert.Equal(new[] { PairingState.Waiting, PairingState.Waiting, PairingState.SlowedDown, PairingState.Waiting, PairingState.SlowedDown },
            progress.Seen.Select(p => p.State));
        Assert.Equal(15, (int)progress.Seen[^1].Interval.TotalSeconds);
    }

    [Fact]
    public async Task Network_errors_back_off_5_10_20_30_30_and_then_recover()
    {
        HttpResponseMessage Down() => throw new HttpRequestException("connection refused to 10.0.0.1");
        var rig = new Rig().Poll(Down, Down, Down, Down, Down, () => FakeServer.Respond(HttpStatusCode.Created, Tokens()));
        var progress = new Progress();
        var pairing = rig.Pairing();
        var result = await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App), progress);

        Assert.Equal(new[] { 5, 5, 10, 20, 30, 30 }, rig.Waits.Select(w => (int)w.TotalSeconds));
        Assert.Equal(5, progress.Seen.Count(p => p.State == PairingState.Retrying));
        Assert.Equal(Agent, result.AgentSlug);
        Assert.DoesNotContain(rig.Logs.Texts, t => t.Contains("10.0.0.1") || t.Contains(Pairing));
        Assert.Contains(rig.Logs.Texts, t => t.Contains("could not be reached"));
    }

    [Fact]
    public async Task A_server_error_is_retried_like_a_network_error_and_the_backoff_resets_after_an_answer()
    {
        var rig = new Rig().Poll(() => FakeServer.Respond(HttpStatusCode.BadGateway, "bad gateway"), () => Pending("authorization_pending"),
            () => FakeServer.Respond(HttpStatusCode.ServiceUnavailable, "down"), () => FakeServer.Respond(HttpStatusCode.Created, Tokens()));
        var pairing = rig.Pairing();
        await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App));
        Assert.Equal(new[] { 5, 5, 5, 5 }, rig.Waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task Network_errors_do_not_outlive_the_pairing()
    {
        HttpResponseMessage Down() => throw new HttpRequestException("down");
        var rig = new Rig();
        for (var i = 0; i < 100; i++)
        {
            rig.Poll(Down);
        }
        var pairing = rig.Pairing();
        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(async () => await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App)));
        Assert.Null(await rig.Store.LoadAsync(Key));
    }

    [Fact]
    public async Task An_expired_code_throws_expired()
    {
        var rig = new Rig().Poll(() => Pending("authorization_pending"), () => Pending("expired_token"));
        var pairing = rig.Pairing();
        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(async () => await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App)));
        Assert.Null(await rig.Store.LoadAsync(Key));
    }

    [Fact]
    public async Task A_code_the_server_no_longer_knows_throws_expired_too()
    {
        var rig = new Rig().Poll(() => Pending("invalid_grant"));
        var pairing = rig.Pairing();
        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(async () => await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App)));
    }

    [Fact]
    public async Task A_local_clock_past_the_expiry_throws_expired_without_asking()
    {
        var rig = new Rig();
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Agent, "PC", App);
        rig.Now = start.ExpiresAt.AddSeconds(1);
        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(() => pairing.WaitAsync(start));
        Assert.Equal(0, rig.PollCount);
    }

    [Fact]
    public async Task A_denial_throws_denied_and_saves_nothing()
    {
        var rig = new Rig().Poll(() => Pending("authorization_pending"), () => Pending("access_denied"));
        var pairing = rig.Pairing();
        var denied = await Assert.ThrowsAsync<FadenstackPairingDeniedException>(async () => await pairing.WaitAsync(await pairing.StartAsync(Agent, "PC", App)));
        Assert.DoesNotContain(Pairing, denied.Message);
        Assert.Null(await rig.Store.LoadAsync(Key));
    }

    [Fact]
    public async Task A_server_without_the_routes_is_reported_as_unsupported()
    {
        var server = new FakeServer(); // 404 for everything
        var pairing = new AgentPairing(Server, new HttpClient(server), new MemoryTokenStore());
        await Assert.ThrowsAsync<FadenstackPairingUnsupportedException>(() => pairing.StartAsync(Agent, "PC", App));
    }

    [Fact]
    public async Task Start_failures_are_typed()
    {
        var limited = new FakeServer().On("POST", "/api/agent-tokens/pairings", _ => FakeServer.Respond((HttpStatusCode)429, new { detail = "slow" }));
        await Assert.ThrowsAsync<FadenstackRateLimitedException>(() =>
            new AgentPairing(Server, new HttpClient(limited), new MemoryTokenStore()).StartAsync(Agent, "PC", App));
        var down = new FakeServer().On("POST", "/api/agent-tokens/pairings", _ => throw new HttpRequestException("no route"));
        await Assert.ThrowsAsync<FadenstackException>(() =>
            new AgentPairing(Server, new HttpClient(down), new MemoryTokenStore()).StartAsync(Agent, "PC", App));
    }

    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        var pairing = new AgentPairing(Server, new HttpClient(rig.Server), rig.Store, () => rig.Now, (_, ct) =>
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var start = await pairing.StartAsync(Agent, "PC", App);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing.WaitAsync(start, null, cancel.Token));
        Assert.Equal(0, rig.PollCount);
    }

    [Fact]
    public async Task Cancelling_during_a_poll_is_not_mistaken_for_a_network_error()
    {
        var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        rig.Poll(() =>
        {
            cancel.Cancel();
            throw new TaskCanceledException();
        });
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Agent, "PC", App);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing.WaitAsync(start, null, cancel.Token));
        Assert.Equal(1, rig.PollCount);
    }

    [Fact]
    public void Plain_http_to_another_machine_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new AgentPairing(new Uri("http://ai.example.internal"), new HttpClient(), new MemoryTokenStore()));
        _ = new AgentPairing(new Uri("http://localhost:8000"), new HttpClient(), new MemoryTokenStore());
    }

    // ---- sign out -------------------------------------------------------------------------------------------------

    private static async Task<(AgentTokenProvider Provider, FakeServer Server, MemoryTokenStore Store)> SignedIn(
        Func<Recorded, HttpResponseMessage> revoke)
    {
        var server = new FakeServer().On("POST", "/api/agent-tokens/revoke", revoke);
        var store = new MemoryTokenStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(Key, AgentTokenProvider.FromWire(System.Text.Json.JsonSerializer.Serialize(Tokens()), now));
        var provider = new AgentTokenProvider(Server, Agent, store, new HttpClient(server), () => now);
        Assert.True(await provider.IsSignedInAsync());
        return (provider, server, store);
    }

    [Fact]
    public async Task Sign_out_revokes_the_refresh_token_and_deletes_the_tokens()
    {
        var (provider, server, store) = await SignedIn(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.NotNull(provider.SignedInUser);
        await provider.SignOutAsync();

        var revoke = server.Requests.Single();
        Assert.Equal("refresh-SECRET-1", revoke.Json.GetProperty("refresh_token").GetString());
        Assert.Null(revoke.Header("Authorization"));
        Assert.Null(await store.LoadAsync(Key));
        Assert.Null(provider.SignedInUser);
        Assert.False(await provider.IsSignedInAsync());
        await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => provider.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData("network")]
    [InlineData("refused")]
    public async Task Sign_out_deletes_the_tokens_even_when_the_revoke_fails(string how)
    {
        var (provider, server, store) = await SignedIn(_ => how == "network"
            ? throw new HttpRequestException("unreachable")
            : FakeServer.Respond(HttpStatusCode.InternalServerError, "boom"));
        await provider.SignOutAsync();
        Assert.Single(server.Requests);
        Assert.Null(await store.LoadAsync(Key));
        Assert.False(await provider.IsSignedInAsync());
    }

    [Fact]
    public async Task Sign_out_without_a_sign_in_does_nothing_and_asks_nobody()
    {
        var server = new FakeServer();
        var provider = new AgentTokenProvider(Server, Agent, new MemoryTokenStore(), new HttpClient(server));
        await provider.SignOutAsync();
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_renewal_keeps_who_signed_in()
    {
        var now = DateTimeOffset.UtcNow;
        var server = new FakeServer().Json("POST", "/api/agent-tokens/refresh", Tokens(withUser: false));
        var store = new MemoryTokenStore();
        await store.SaveAsync(Key, AgentTokenProvider.FromWire(System.Text.Json.JsonSerializer.Serialize(Tokens()), now.AddHours(-1)));
        var provider = new AgentTokenProvider(Server, Agent, store, new HttpClient(server), () => now);
        await provider.GetAccessTokenAsync();
        Assert.Single(server.Requests);
        Assert.Equal("Anna Weber", (await store.LoadAsync(Key))!.User!.DisplayName);
    }

    // ---- the store -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_token_set_without_a_user_still_loads_and_one_with_a_user_round_trips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "faden-tokens-" + Guid.NewGuid());
        try
        {
            var protector = SecretProtection.Default;
            var store = new FileTokenStore(directory, protector);
            // exactly what an earlier version wrote: no "user" at all
            const string old = """
                {"access_token":"a","access_expires_at":"2026-10-30T10:00:00+00:00","refresh_token":"r",
                 "refresh_expires_at":"2026-11-30T10:00:00+00:00","device_id":"d","agent_id":"a-1","agent":"docs"}
                """;
            var probe = new FileTokenStore(directory, protector);
            await probe.SaveAsync("k", new TokenSet());
            var file = Directory.GetFiles(directory).Single();
            File.WriteAllBytes(file, protector.Protect(System.Text.Encoding.UTF8.GetBytes(old)));
            var loaded = (await store.LoadAsync("k"))!;
            Assert.Equal(("a", "r", "docs"), (loaded.AccessToken, loaded.RefreshToken, loaded.Agent));
            Assert.Null(loaded.User);

            var withUser = loaded with { User = new SignedInUser(UserId, "anna@example.com", null) };
            await store.SaveAsync("k", withUser);
            Assert.Equal(withUser, await new FileTokenStore(directory, protector).LoadAsync("k"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}

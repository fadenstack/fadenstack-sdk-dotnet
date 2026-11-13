using System.Net;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>One pairing for several agents (fadenstack-dev sdk/47): the wire, the local limits and the old-server fallback.</summary>
public class PairingSeveralAgentsTests
{
    private const string PairingCode = "PAIRING-SECRET-9f3a";
    private static readonly Uri Server = new("https://faden.test");
    private static readonly PairingClient App = new("Office", "1.0", "Windows 11");
    private static readonly string[] Suite = ["word-assistant", "excel-assistant", "outlook-assistant"];

    private static string KeyOf(string agent) => "https://faden.test/|" + agent;

    private static object Tokens(string agent) => new
    {
        access_token = "access-SECRET-" + agent,
        token_type = "bearer",
        expires_in = 600,
        refresh_token = "refresh-SECRET-" + agent,
        refresh_expires_in = 86400,
        device_id = "11111111-2222-3333-4444-555555555555",
        agent_id = "id-" + agent,
        agent,
        user = new { id = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"), email = "anna@example.com", display_name = "Anna Weber" },
    };

    private sealed class Rig
    {
        public FakeServer Server { get; } = new();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-12T10:00:00Z");
        public MemoryTokenStore Store { get; } = new();
        public CapturingLoggerFactory Logs { get; } = new();

        /// <summary>What the token route answers for the agent in the request (the first agent when it names none).</summary>
        public Func<string, HttpResponseMessage> Token { get; set; } = agent => FakeServer.Respond(HttpStatusCode.Created, Tokens(agent));

        /// <param name="storedAgents">What the start answer says the server stored; null for a server that does not know <c>agents</c>.</param>
        public Rig(string[]? storedAgents)
        {
            Server.On("POST", "/api/agent-tokens/pairings", _ =>
            {
                var body = new Dictionary<string, object>
                {
                    ["pairing_code"] = PairingCode, ["user_code"] = "BDWP-HTQX", ["verification_uri"] = "https://faden.test/pair",
                    ["verification_uri_complete"] = "https://faden.test/pair?code=BDWP-HTQX", ["expires_in"] = 600, ["interval"] = 5,
                };
                if (storedAgents is not null)
                {
                    body["agents"] = storedAgents; // an old server leaves the field out altogether
                }
                return FakeServer.Respond(HttpStatusCode.Created, body);
            });
            Server.On("POST", "/api/agent-tokens/pairings/token", r =>
                Token(r.Json.TryGetProperty("agent", out var a) ? a.GetString()! : Suite[0]));
        }

        public AgentPairing Pairing() => new(PairingSeveralAgentsTests.Server, new HttpClient(Server), Store, () => Now,
            (span, ct) =>
            {
                Now += span;
                return Task.CompletedTask;
            }, logger: Logs.CreateLogger("Fadenstack.Client"));

        public IEnumerable<Recorded> Starts => Server.Requests.Where(r => r.Path.EndsWith("/pairings"));

        public IEnumerable<Recorded> TokenRequests => Server.Requests.Where(r => r.Path.EndsWith("/token"));
    }

    private static HttpResponseMessage InvalidGrant() => FakeServer.Respond(HttpStatusCode.BadRequest, new { code = "invalid_grant" });

    // ---- start --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_agent_sends_no_agents_field_and_the_first_agents_token_request_has_no_agent_field()
    {
        var rig = new Rig(null);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App);
        await pairing.WaitAsync(start);

        Assert.False(rig.Starts.Single().Json.TryGetProperty("agents", out _));
        Assert.False(rig.TokenRequests.Single().Json.TryGetProperty("agent", out _));
        // an explicit empty list of further agents is the same one-agent pairing
        var rig2 = new Rig(null);
        await rig2.Pairing().StartAsync(Suite[0], "PC", App, []);
        Assert.False(rig2.Starts.Single().Json.TryGetProperty("agents", out _));
    }

    [Fact]
    public async Task Several_agents_are_sent_in_order_with_the_first_as_agent_and_the_servers_list_is_kept()
    {
        var rig = new Rig(Suite);
        var start = await rig.Pairing().StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        var sent = rig.Starts.Single().Json;
        Assert.Equal(Suite[0], sent.GetProperty("agent").GetString());
        Assert.Equal(Suite, sent.GetProperty("agents").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Suite, start.Agents);
        Assert.Equal(Suite, start.RequestedAgents);
        Assert.Equal(Suite, start.AgentsLeft);
    }

    [Fact]
    public async Task Eight_agents_are_allowed_and_nine_are_refused_before_any_request()
    {
        var eight = Enumerable.Range(1, 8).Select(i => "agent-" + i).ToArray();
        var rig = new Rig(eight);
        await rig.Pairing().StartAsync(eight[0], "PC", App, eight.Skip(1));
        Assert.Single(rig.Starts);

        var nine = Enumerable.Range(1, 9).Select(i => "agent-" + i).ToArray();
        var refused = new Rig(nine);
        await Assert.ThrowsAsync<ArgumentException>(() => refused.Pairing().StartAsync(nine[0], "PC", App, nine.Skip(1)));
        Assert.Empty(refused.Server.Requests);
    }

    [Theory]
    [InlineData("word-assistant")]
    [InlineData("WORD-assistant")]
    [InlineData("excel-assistant,excel-assistant")]
    [InlineData("")]
    public async Task A_repeated_or_empty_agent_is_refused_before_any_request(string further)
    {
        var rig = new Rig(Suite);
        var list = further.Split(',');
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Pairing().StartAsync(Suite[0], "PC", App, list));
        Assert.Empty(rig.Server.Requests);
    }

    // ---- per-agent tokens ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Wait_returns_the_first_agents_tokens_and_redeem_collects_each_other_agent_under_its_own_key()
    {
        var rig = new Rig(Suite);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        var first = await pairing.WaitAsync(start);
        Assert.Equal(Suite[0], first.AgentSlug);
        Assert.Equal(Suite.Skip(1), first.AgentsLeft);
        var second = await pairing.RedeemAsync(start, Suite[1]);
        var third = await pairing.WaitAsync(start, Suite[2]);

        Assert.Equal((Suite[1], Suite[2]), (second.AgentSlug, third.AgentSlug));
        Assert.Equal(new[] { Suite[2] }, second.AgentsLeft);
        Assert.Empty(third.AgentsLeft!);
        Assert.Empty(start.AgentsLeft);
        foreach (var agent in Suite)
        {
            Assert.Equal("access-SECRET-" + agent, (await rig.Store.LoadAsync(KeyOf(agent)))!.AccessToken);
        }
        // the first agent's request names no agent; the others name theirs, and carry the pairing code in the body
        var sent = rig.TokenRequests.ToList();
        Assert.False(sent[0].Json.TryGetProperty("agent", out _));
        Assert.Equal(new[] { Suite[1], Suite[2] }, sent.Skip(1).Select(r => r.Json.GetProperty("agent").GetString()));
        Assert.All(sent, r => Assert.Equal(PairingCode, r.Json.GetProperty("pairing_code").GetString()));
        var provider = new AgentTokenProvider(Server, Suite[2], rig.Store, new HttpClient(rig.Server), () => rig.Now);
        Assert.Equal("access-SECRET-" + Suite[2], await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Redeem_is_one_call_without_waiting()
    {
        var rig = new Rig(Suite);
        var waits = 0;
        var pairing = new AgentPairing(Server, new HttpClient(rig.Server), rig.Store, () => rig.Now, (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        await pairing.RedeemAsync(start, Suite[1]);
        Assert.Equal(0, waits);
        Assert.Single(rig.TokenRequests);
    }

    [Fact]
    public async Task Invalid_grant_on_redeem_is_a_typed_exception_naming_the_agent_and_saves_nothing()
    {
        var rig = new Rig(Suite) { Token = _ => InvalidGrant() };
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        var e = await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.RedeemAsync(start, Suite[1]));
        Assert.Equal(Suite[1], e.Agent);
        Assert.Contains("approve", e.Message);
        Assert.DoesNotContain(PairingCode, e.Message);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[1])));
        Assert.Equal(Suite, start.AgentsLeft);
    }

    [Fact]
    public async Task Redeeming_twice_is_the_server_saying_invalid_grant_the_second_time()
    {
        var rig = new Rig(Suite);
        var redeemed = new HashSet<string>();
        rig.Token = agent => redeemed.Add(agent) ? FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) : InvalidGrant();
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        await pairing.RedeemAsync(start, Suite[1]);
        await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.RedeemAsync(start, Suite[1]));
    }

    [Fact]
    public async Task Waiting_for_another_agent_that_was_not_approved_is_the_same_typed_exception()
    {
        var rig = new Rig(Suite) { Token = agent => agent == Suite[0] ? FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) : InvalidGrant() };
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.WaitAsync(start, Suite[2]));
    }

    [Fact]
    public async Task Redeem_before_the_approval_says_so_and_a_denied_or_expired_pairing_keeps_its_own_exception()
    {
        var rig = new Rig(Suite);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        foreach (var (code, type) in new[]
        {
            ("authorization_pending", typeof(FadenstackException)),
            ("access_denied", typeof(FadenstackPairingDeniedException)),
            ("expired_token", typeof(FadenstackPairingExpiredException)),
        })
        {
            rig.Token = _ => FakeServer.Respond(HttpStatusCode.BadRequest, new { code });
            var e = await Assert.ThrowsAnyAsync<FadenstackException>(() => pairing.RedeemAsync(start, Suite[1]));
            Assert.Equal(type, e.GetType());
        }
    }

    [Fact]
    public async Task An_agent_that_is_not_in_the_pairing_is_refused_locally()
    {
        var rig = new Rig(Suite);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        var before = rig.Server.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.RedeemAsync(start, "other-assistant"));
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.WaitAsync(start, "other-assistant"));
        Assert.Equal(before, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task The_agent_of_the_wait_is_matched_without_regard_to_case()
    {
        var rig = new Rig(Suite);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        var result = await pairing.WaitAsync(start, "WORD-assistant");
        Assert.False(rig.TokenRequests.Single().Json.TryGetProperty("agent", out _));
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf("WORD-assistant")));
        Assert.NotNull(result);
    }

    // ---- a server that ignores `agents` ---------------------------------------------------------------------------

    [Fact]
    public async Task An_old_server_gives_no_agents_list_signs_in_only_the_first_and_names_the_agents_left()
    {
        var rig = new Rig(null);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        Assert.Empty(start.Agents);

        var result = await pairing.WaitAsync(start);
        Assert.Equal(Suite[0], result.AgentSlug);
        Assert.Equal(Suite.Skip(1), result.AgentsLeft);
        Assert.Equal(Suite.Skip(1), start.AgentsLeft);
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[0])));
    }

    [Fact]
    public async Task An_old_server_is_never_asked_for_another_agent_whose_tokens_it_would_give_to_the_first()
    {
        var rig = new Rig(null);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        var e = await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.RedeemAsync(start, Suite[1]));
        Assert.Equal(Suite[1], e.Agent);
        await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.WaitAsync(start, Suite[2]));
        Assert.Empty(rig.TokenRequests);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[1])));
    }

    [Fact]
    public async Task Pairing_secrets_stay_out_of_the_log_for_several_agents()
    {
        var rig = new Rig(Suite);
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));
        await pairing.WaitAsync(start);
        await pairing.RedeemAsync(start, Suite[1]);
        Assert.NotEmpty(rig.Logs.Texts);
        Assert.DoesNotContain(rig.Logs.Texts, t => t.Contains(PairingCode) || t.Contains("SECRET") || t.Contains("BDWP"));
        Assert.DoesNotContain(PairingCode, start.ToString());
    }

    // ---- the first agent refused (sdk/51 I00 #8, I-11) -----------------------------------------------------------

    [Fact]
    public async Task The_first_agent_refused_while_another_was_approved_is_reported_refused_not_expired()
    {
        // The person approved only the second and third agents. The server answers invalid_grant for the first, as it
        // would for a code it does not know; another agent of the pairing tells the two apart.
        var rig = new Rig(Suite) { Token = agent => agent == Suite[0] ? InvalidGrant() : FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) };
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        var refused = await Assert.ThrowsAsync<FadenstackPairingAgentException>(() => pairing.WaitAsync(start));

        Assert.Equal(Suite[0], refused.Agent);
        Assert.Contains("approve", refused.Message);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[0])));
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[1]))); // the approved agent that told them apart is signed in
        Assert.Equal(new[] { Suite[0], Suite[2] }, start.AgentsLeft);
        Assert.NotNull(await pairing.RedeemAsync(start, Suite[2])); // and the rest are collected as usual
        Assert.DoesNotContain(rig.Logs.Texts, t => t.Contains(PairingCode));
    }

    [Fact]
    public async Task The_first_agent_and_every_other_answering_invalid_grant_is_a_code_the_server_does_not_know()
    {
        var rig = new Rig(Suite) { Token = _ => InvalidGrant() };
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(() => pairing.WaitAsync(start));

        Assert.Equal(Suite, start.AgentsLeft);
        foreach (var agent in Suite)
        {
            Assert.Null(await rig.Store.LoadAsync(KeyOf(agent)));
        }
    }

    [Fact]
    public async Task An_older_server_that_stores_one_agent_reads_invalid_grant_as_before_and_asks_no_other_agent()
    {
        var rig = new Rig(null) { Token = _ => InvalidGrant() };
        var pairing = rig.Pairing();
        var start = await pairing.StartAsync(Suite[0], "PC", App, Suite.Skip(1));

        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(() => pairing.WaitAsync(start));

        Assert.Single(rig.TokenRequests);
    }
}

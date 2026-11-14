using System.Net;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The wizard's default server side (sdk/51 F15): <see cref="ServerSignIn"/> over the SDK's health check, pairing and password
/// login, against a fake server. The pairing fakes follow the Client tests' (<c>PairingSeveralAgentsTests</c>); the first poll
/// waits the server's shortest interval (one second), so the tests that wait are few.
/// </summary>
public class ServerSignInTests
{
    private const string PairingCode = "PAIRING-SECRET-9f3a";
    private const string UserCode = "BDWP-HTQX";
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

    private static HttpResponseMessage InvalidGrant() => FakeServer.Respond(HttpStatusCode.BadRequest, new { code = "invalid_grant" });

    private sealed class Rig
    {
        public FakeServer Fake { get; } = new();

        public MemoryTokenStore Store { get; } = new();

        public ServerSignIn Sign { get; }

        /// <summary>What the token route answers for the agent in the request (the first agent when it names none).</summary>
        public Func<string, HttpResponseMessage> Token { get; set; } = agent => FakeServer.Respond(HttpStatusCode.Created, Tokens(agent));

        /// <param name="storedAgents">What the start answer says the server stored; null for a server that does not know <c>agents</c>.</param>
        /// <param name="agents">The app's agents, the main one first.</param>
        public Rig(string[]? storedAgents, string[]? agents = null, MachinePolicy? policy = null)
        {
            agents ??= Suite;
            Fake.On("POST", "/api/agent-tokens/pairings", _ =>
            {
                var body = new Dictionary<string, object>
                {
                    ["pairing_code"] = PairingCode, ["user_code"] = UserCode, ["verification_uri"] = "https://faden.test/pair",
                    ["verification_uri_complete"] = "https://faden.test/pair?code=" + UserCode, ["expires_in"] = 600, ["interval"] = 1,
                };
                if (storedAgents is not null)
                {
                    body["agents"] = storedAgents;
                }
                return FakeServer.Respond(HttpStatusCode.Created, body);
            });
            Fake.On("POST", "/api/agent-tokens/pairings/token", r =>
                Token(r.Json.TryGetProperty("agent", out var a) ? a.GetString()! : agents[0]));
            Sign = new ServerSignIn(Store, agents, "Word assistant on PC-7", App, policy, http: _ => new HttpClient(Fake, disposeHandler: false));
        }

        public IEnumerable<Recorded> Starts => Fake.Requests.Where(r => r.Path.EndsWith("/pairings", StringComparison.Ordinal));

        public IEnumerable<Recorded> TokenRequests => Fake.Requests.Where(r => r.Path.EndsWith("/pairings/token", StringComparison.Ordinal));
    }

    private sealed class Retries : IProgress<bool>
    {
        public List<bool> Seen { get; } = new();

        public Action<bool>? OnReport { get; set; }

        public void Report(bool value)
        {
            Seen.Add(value);
            OnReport?.Invoke(value);
        }
    }

    // ---- the constructor and the health check -------------------------------------------------------------------------

    [Fact]
    public void The_constructor_needs_a_store_agents_and_a_client()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerSignIn(null!, Suite, "PC", App));
        Assert.Throws<ArgumentException>(() => new ServerSignIn(new MemoryTokenStore(), Array.Empty<string>(), "PC", App));
        Assert.Throws<ArgumentNullException>(() => new ServerSignIn(new MemoryTokenStore(), Suite, "PC", null!));
    }

    [Fact]
    public async Task The_health_check_asks_the_server_through_the_given_client()
    {
        var rig = new Rig(Suite);
        rig.Fake.Json("GET", "/api/health", new { status = "ok" });

        await rig.Sign.CheckAsync(Server, CancellationToken.None);

        Assert.Equal("/api/health", Assert.Single(rig.Fake.Requests).Path);
    }

    [Fact]
    public async Task Plain_http_goes_only_to_the_http_server_the_machine_policy_names()
    {
        var corp = new Uri("http://corp.internal:8080");
        var named = new Rig(Suite, policy: new MachinePolicy { Server = corp, Standalone = PolicyAccess.Forbidden });
        named.Fake.Json("GET", "/api/health", new { status = "ok" });
        await named.Sign.CheckAsync(corp, CancellationToken.None);
        Assert.Single(named.Fake.Requests);
        // The request goes; the fake's sign-in page is on another address, which the SDK refuses after the answer.
        await Assert.ThrowsAsync<FadenstackPairingPageElsewhereException>(() => named.Sign.StartAsync(corp, CancellationToken.None));
        Assert.Single(named.Starts);

        var other = new Rig(Suite, policy: new MachinePolicy { Server = corp, Standalone = PolicyAccess.Forbidden });
        await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() => other.Sign.CheckAsync(new Uri("http://other.internal:8080"), CancellationToken.None));
        await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() => other.Sign.StartAsync(new Uri("http://other.internal:8080"), CancellationToken.None));
        Assert.Empty(other.Fake.Requests);

        var none = new Rig(Suite);
        await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() => none.Sign.CheckAsync(corp, CancellationToken.None));
        Assert.Empty(none.Fake.Requests);
    }

    // ---- start ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_gives_a_ticket_with_the_code_the_page_and_the_agents()
    {
        var rig = new Rig(Suite);

        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        Assert.Equal(UserCode, ticket.UserCode);
        Assert.Equal(new Uri("https://faden.test/pair"), ticket.VerificationUri);
        Assert.Equal(new Uri("https://faden.test/pair?code=" + UserCode), ticket.VerificationUriComplete);
        Assert.Equal(Suite, ticket.Agents);
        Assert.InRange(ticket.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.NotNull(ticket.Handle);
        var sent = Assert.Single(rig.Starts).Json;
        Assert.Equal(Suite[0], sent.GetProperty("agent").GetString());
        Assert.Equal(Suite, sent.GetProperty("agents").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Word assistant on PC-7", sent.GetProperty("device_label").GetString());
        Assert.Equal("Office", sent.GetProperty("client").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_ticket_never_exposes_the_pairing_code_and_its_text_has_no_user_code()
    {
        var rig = new Rig(Suite);

        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        Assert.DoesNotContain(UserCode, ticket.ToString());
        Assert.DoesNotContain(PairingCode, ticket.ToString());
        Assert.DoesNotContain(PairingCode, ticket.Handle!.ToString());
        Assert.DoesNotContain(UserCode, ticket.Handle.ToString());
        foreach (var property in typeof(PairingTicket).GetProperties())
        {
            var value = property.GetValue(ticket);
            if (value is not PairingTicket && property.Name != nameof(PairingTicket.UserCode) && value is not null)
            {
                Assert.DoesNotContain(PairingCode, value.ToString());
            }
        }
    }

    [Fact]
    public async Task A_server_error_at_the_start_is_a_typed_error_not_a_ticket()
    {
        var rig = new Rig(Suite);
        rig.Fake.Json("POST", "/api/agent-tokens/pairings", new { detail = "no pairing here" }, HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<FadenstackPairingUnsupportedException>(() => rig.Sign.StartAsync(Server, CancellationToken.None));
    }

    [Fact]
    public async Task A_ticket_that_is_not_this_sign_ins_is_refused()
    {
        var rig = new Rig(Suite);
        var strange = new PairingTicket("X", Server, Server, DateTimeOffset.UtcNow, Suite, Handle: new object());

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Sign.WaitAsync(strange, new Retries(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Sign.RedeemAsync(strange, Suite[1], CancellationToken.None));
    }

    // ---- wait -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wait_collects_the_main_agent_and_says_who_signed_in()
    {
        var rig = new Rig(Suite);
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        var approval = await rig.Sign.WaitAsync(ticket, new Retries(), CancellationToken.None);

        Assert.Equal(new[] { new AgentSignIn(Suite[0], AgentAccess.Approved) }, approval.Agents);
        Assert.Equal("anna@example.com", approval.Person!.Email);
        Assert.Equal("access-SECRET-" + Suite[0], (await rig.Store.LoadAsync(KeyOf(Suite[0])))!.AccessToken);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[1]))); // the others are collected by RedeemAsync
    }

    [Fact]
    public async Task Wait_reports_true_to_the_progress_on_a_retry()
    {
        var rig = new Rig(Suite);
        rig.Fake.On("POST", "/api/agent-tokens/pairings/token", _ => throw new HttpRequestException("connection refused"));
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var progress = new Retries { OnReport = retrying => { if (retrying) { stop.Cancel(); } } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Sign.WaitAsync(ticket, progress, stop.Token));

        Assert.Equal(new[] { false, true }, progress.Seen); // waiting at the start, then retrying after the failed poll
    }

    [Fact]
    public async Task A_main_agent_that_was_not_approved_among_several_is_Refused_and_the_collected_one_is_Approved()
    {
        // The person approved the second agent only. The first answers invalid_grant, as for a code the server does not know;
        // another agent of the pairing tells the two apart. The agents that are still to collect are left out.
        var rig = new Rig(Suite) { Token = agent => agent == Suite[0] ? InvalidGrant() : FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) };
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        var approval = await rig.Sign.WaitAsync(ticket, new Retries(), CancellationToken.None);

        Assert.Equal(new[]
        {
            new AgentSignIn(Suite[0], AgentAccess.Refused), new AgentSignIn(Suite[1], AgentAccess.Approved),
        }, approval.Agents);
        Assert.Null(approval.Person);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[0])));
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[1])));
    }

    [Fact]
    public async Task A_code_the_server_does_not_know_is_expired_and_not_a_refusal()
    {
        var rig = new Rig(Suite) { Token = _ => InvalidGrant() };
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        await Assert.ThrowsAsync<FadenstackPairingExpiredException>(() => rig.Sign.WaitAsync(ticket, new Retries(), CancellationToken.None));
    }

    [Fact]
    public async Task A_denied_pairing_is_a_denied_exception()
    {
        var rig = new Rig(Suite) { Token = _ => FakeServer.Respond(HttpStatusCode.BadRequest, new { code = "access_denied" }) };
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);

        await Assert.ThrowsAsync<FadenstackPairingDeniedException>(() => rig.Sign.WaitAsync(ticket, new Retries(), CancellationToken.None));
    }

    // ---- redeem ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Redeem_collects_another_agent_and_a_refused_one_is_Refused()
    {
        var rig = new Rig(Suite) { Token = agent => agent == Suite[2] ? InvalidGrant() : FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) };
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);
        await rig.Sign.WaitAsync(ticket, new Retries(), CancellationToken.None);

        var second = await rig.Sign.RedeemAsync(ticket, Suite[1], CancellationToken.None);
        var third = await rig.Sign.RedeemAsync(ticket, Suite[2], CancellationToken.None);

        Assert.Equal(new AgentSignIn(Suite[1], AgentAccess.Approved), second);
        Assert.Equal(new AgentSignIn(Suite[2], AgentAccess.Refused), third);
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[1])));
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[2])));
    }

    [Fact]
    public async Task Redeem_on_an_older_server_gives_Left_without_a_request()
    {
        var rig = new Rig(storedAgents: null); // the start answer has no `agents`: one agent per pairing
        var ticket = await rig.Sign.StartAsync(Server, CancellationToken.None);
        var before = rig.Fake.Requests.Count;

        var second = await rig.Sign.RedeemAsync(ticket, Suite[1], CancellationToken.None);

        Assert.Equal(new AgentSignIn(Suite[1], AgentAccess.Left), second);
        Assert.Equal(before, rig.Fake.Requests.Count);
        Assert.Empty(rig.TokenRequests);
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[1])));
    }

    // ---- the password sign-in -------------------------------------------------------------------------------------------

    private static void LoginRoutes(Rig rig, Func<string, HttpResponseMessage>? agentToken = null)
    {
        rig.Fake.Json("POST", "/api/auth/jwt/login", new { access_token = "login-jwt", token_type = "bearer" });
        rig.Fake.On("POST", "/api/agent-tokens", r =>
        {
            var agent = r.Json.GetProperty("agent").GetString()!;
            return agentToken is null ? FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)) : agentToken(agent);
        });
    }

    [Fact]
    public async Task The_password_sign_in_signs_in_each_agent_and_a_403_agent_is_Refused()
    {
        var rig = new Rig(Suite);
        LoginRoutes(rig, agent => agent == Suite[1]
            ? FakeServer.Respond(HttpStatusCode.Forbidden, new { code = "agent_not_granted", detail = "no grant" })
            : FakeServer.Respond(HttpStatusCode.Created, Tokens(agent)));

        var approval = await rig.Sign.SignInWithPasswordAsync(Server, "anna@example.com", "s3cret", CancellationToken.None);

        Assert.Equal(new[]
        {
            new AgentSignIn(Suite[0], AgentAccess.Approved), new AgentSignIn(Suite[1], AgentAccess.Refused),
            new AgentSignIn(Suite[2], AgentAccess.Approved),
        }, approval.Agents);
        Assert.Equal("anna@example.com", approval.Person!.Email);
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[0])));
        Assert.Null(await rig.Store.LoadAsync(KeyOf(Suite[1])));
        Assert.NotNull(await rig.Store.LoadAsync(KeyOf(Suite[2])));
        var login = Assert.Single(rig.Fake.Requests, r => r.Path == "/api/auth/jwt/login");
        Assert.Equal("username=anna%40example.com&password=s3cret", login.Body);
        var signIns = rig.Fake.Requests.Where(r => r.Path == "/api/agent-tokens").ToList();
        Assert.Equal(Suite, signIns.Select(r => r.Json.GetProperty("agent").GetString()));
        Assert.All(signIns, r => Assert.Equal("Bearer login-jwt", r.Header("Authorization")));
        Assert.All(signIns, r => Assert.Equal("Word assistant on PC-7", r.Json.GetProperty("device_label").GetString()));
    }

    [Fact]
    public async Task The_password_sign_in_with_every_agent_signed_in_gives_the_first_person()
    {
        var rig = new Rig(Suite);
        LoginRoutes(rig);

        var approval = await rig.Sign.SignInWithPasswordAsync(Server, "anna@example.com", "s3cret", CancellationToken.None);

        Assert.All(approval.Agents, a => Assert.Equal(AgentAccess.Approved, a.Access));
        Assert.Equal(Suite, approval.Agents.Select(a => a.Agent));
        Assert.Equal("Anna Weber", approval.Person!.DisplayName);
    }

    [Fact]
    public async Task A_wrong_password_is_a_bad_credentials_error_and_signs_no_agent_in()
    {
        var rig = new Rig(Suite);
        rig.Fake.Json("POST", "/api/auth/jwt/login", new { detail = "LOGIN_BAD_CREDENTIALS" }, HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<FadenstackBadCredentialsException>(() =>
            rig.Sign.SignInWithPasswordAsync(Server, "anna@example.com", "wrong-SECRET", CancellationToken.None));

        Assert.DoesNotContain("wrong-SECRET", e.Message);
        Assert.DoesNotContain(rig.Fake.Requests, r => r.Path == "/api/agent-tokens");
    }

    [Fact]
    public async Task The_password_goes_only_over_https_or_to_this_machine()
    {
        var rig = new Rig(Suite);
        LoginRoutes(rig);

        await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() =>
            rig.Sign.SignInWithPasswordAsync(new Uri("http://ai.example.test"), "anna@example.com", "s3cret", CancellationToken.None));

        Assert.Empty(rig.Fake.Requests);
    }
}

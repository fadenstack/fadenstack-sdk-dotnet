using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>sdk/51 F09: the machine policy's vocabulary, read fail-closed, and enforced in the SDK.</summary>
public class MachinePolicyTests
{
    private const string Definition = """{"slug":"docs-assistant","version":{"instructions":"Hi."}}""";

    private static AgentBuilder Standalone(MachinePolicy policy, string baseUrl = "http://localhost:11434/v1", bool lan = false,
        LocalProfileOptions? options = null, OpenAiFake? fake = null) => FadenstackAgent.CreateBuilder()
        .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri(baseUrl), Model = "m", Handler = fake ?? new OpenAiFake(), AllowPlainHttpInLan = lan })
        .UseLocalProfile(AgentDefinition.FromJson(Definition), options: options)
        .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
        .UseMachinePolicy(policy);

    // ---- reading ----

    [Fact]
    public void Each_key_is_read_and_locked()
    {
        var policy = MachinePolicy.FromJson("""
            {"server":"https://ai.example.test","standalone":"allowed","endpoints":["http://localhost:11434","https://llm.example.test/v1"],
             "plainHttp":"forbidden","mcp":["notes-db"],"skills":"allowed","webSearch":"forbidden","timestamping":"off",
             "chatRetention":{"autoDelete":false,"maxAgeDays":30,"maxSessions":100},"auditRetentionDays":90}
            """);

        Assert.Equal(new Uri("https://ai.example.test"), policy.Server);
        Assert.Equal(90, policy.AuditRetentionDays);
        Assert.Equal(PolicyAccess.Allowed, policy.Standalone);
        Assert.Equal(new[] { "http://localhost:11434", "https://llm.example.test/v1" }, policy.Endpoints);
        Assert.Equal(PolicyAccess.Forbidden, policy.PlainHttp);
        Assert.Equal(PolicyAccess.Listed, policy.Mcp.Access);
        Assert.True(policy.Mcp.Allows("NOTES-DB"));
        Assert.False(policy.Mcp.Allows("other"));
        Assert.Equal(PolicyRule.Allowed, policy.Skills);
        Assert.Equal(PolicyAccess.Forbidden, policy.WebSearch);
        Assert.Equal(TimestampingPolicy.Off, policy.Timestamping);
        Assert.Equal(new ChatRetentionChoice(false, 30, 100), policy.ChatRetention);
        Assert.Equal(MachinePolicy.Keys.OrderBy(k => k), policy.Locked.OrderBy(k => k));
        Assert.Empty(policy.Warnings);
    }

    [Fact]
    public void With_a_server_and_nothing_else_standalone_mcp_skills_and_search_are_forbidden()
    {
        var policy = MachinePolicy.FromJson("""{"server":"https://ai.example.test"}""");

        Assert.Equal(PolicyAccess.Forbidden, policy.Standalone);
        Assert.Equal(PolicyRule.Forbidden, policy.Mcp);
        Assert.Equal(PolicyRule.Forbidden, policy.Skills);
        Assert.Equal(PolicyAccess.Forbidden, policy.WebSearch);
        Assert.Equal(PolicyAccess.Allowed, policy.PlainHttp);
        Assert.Equal(TimestampingPolicy.On, policy.Timestamping); // DigiCert by default
        Assert.Equal(new[] { "server" }, policy.Locked);
    }

    [Fact]
    public void Without_a_server_everything_stays_allowed()
    {
        var policy = MachinePolicy.FromJson("{}");

        Assert.Equal(PolicyAccess.Allowed, policy.Standalone);
        Assert.Equal(PolicyRule.Allowed, policy.Mcp);
        Assert.Null(policy.Endpoints);
        Assert.Empty(policy.Locked);
    }

    [Theory]
    [InlineData("standalone")]
    [InlineData("plainHttp")]
    [InlineData("webSearch")]
    [InlineData("mcp")]
    [InlineData("skills")]
    public void An_unknown_value_narrows_to_forbidden(string key)
    {
        var policy = MachinePolicy.FromJson($$"""{"{{key}}":"sometimes"}""");

        var access = key switch
        {
            "standalone" => policy.Standalone,
            "plainHttp" => policy.PlainHttp,
            "webSearch" => policy.WebSearch,
            "mcp" => policy.Mcp.Access,
            _ => policy.Skills.Access,
        };
        Assert.Equal(PolicyAccess.Forbidden, access);
        Assert.Contains(policy.Warnings, w => w.Contains(key) && !w.Contains("sometimes")); // never the value itself
    }

    [Theory]
    [InlineData("\"on\"", TimestampingMode.On, null)]
    [InlineData("\"off\"", TimestampingMode.Off, null)]
    [InlineData("\"https://tsa.example.test/rfc3161\"", TimestampingMode.OwnServer, "https://tsa.example.test/rfc3161")]
    [InlineData("\"http://tsa.example.internal\"", TimestampingMode.OwnServer, "http://tsa.example.internal/")]
    [InlineData("\"sometimes\"", TimestampingMode.Off, null)]
    [InlineData("\"ftp://tsa.example.test\"", TimestampingMode.Off, null)]
    [InlineData("true", TimestampingMode.Off, null)]
    public void Timestamping_is_on_off_or_an_own_servers_address_and_anything_else_is_off(string value, TimestampingMode mode, string? server)
    {
        var policy = MachinePolicy.FromJson("{\"timestamping\":" + value + "}");

        Assert.Equal(mode, policy.Timestamping.Mode);
        Assert.Equal(server, policy.Timestamping.Server?.AbsoluteUri);
        Assert.Equal(mode == TimestampingMode.Off && value != "\"off\"", policy.Warnings.Any(w => w.Contains("timestamping")));
    }

    [Fact]
    public void An_unknown_key_is_ignored_and_reported()
    {
        var policy = MachinePolicy.FromJson("""{"telemetry":"on","standalone":"allowed"}""");

        Assert.Equal(PolicyAccess.Allowed, policy.Standalone);
        Assert.Contains(policy.Warnings, w => w.Contains("telemetry"));
        Assert.DoesNotContain("telemetry", policy.Locked);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"forbidden\"")]
    public void A_policy_the_sdk_cannot_read_forbids_what_it_could_have_allowed(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), "faden-policy-" + Guid.NewGuid() + ".json");
        File.WriteAllText(file, content);
        try
        {
            var policy = MachinePolicy.Load(file);

            Assert.Equal(PolicyAccess.Forbidden, policy.Standalone);
            Assert.Equal(PolicyRule.Forbidden, policy.Mcp);
            Assert.Equal(PolicyAccess.Forbidden, policy.PlainHttp);
            Assert.Equal(TimestampingPolicy.Off, policy.Timestamping);
            Assert.NotEmpty(policy.Warnings);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_policy_file_that_is_there_but_cannot_be_opened_forbids_rather_than_throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "faden-policy-" + Guid.NewGuid() + ".json");
        File.WriteAllText(file, """{"standalone":"allowed"}""");
        try
        {
            MachinePolicy policy;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) // another process holds it
            {
                policy = MachinePolicy.Load(file);
            }

            Assert.Equal(PolicyAccess.Forbidden, policy.Standalone);
            Assert.Contains(policy.Warnings, w => w.StartsWith("The policy file could not be read", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Unreadable_for_a_host_whose_policy_source_failed_forbids_what_none_allows_and_leaves_the_server_open()
    {
        var policy = MachinePolicy.Unreadable;

        Assert.NotSame(MachinePolicy.None, policy);
        Assert.Equal((PolicyAccess.Forbidden, PolicyAccess.Forbidden, PolicyAccess.Forbidden), (policy.Standalone, policy.PlainHttp, policy.WebSearch));
        Assert.Equal((PolicyRule.Forbidden, PolicyRule.Forbidden), (policy.Mcp, policy.Skills));
        Assert.Equal(TimestampingPolicy.Off, policy.Timestamping);
        Assert.Equal(ChatRetentionChoice.Default, policy.ChatRetention); // old chats go as by default; the person cannot turn it off
        Assert.False(policy.AllowsEndpoint(new Uri("http://localhost:11434/v1")));
        Assert.Null(policy.Server);
        Assert.Equal(MachinePolicy.Keys.Where(k => k != "server").OrderBy(k => k, StringComparer.Ordinal), policy.Locked.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "The machine policy could not be read: standalone, local MCP, local skills, web search and plain http are forbidden; time-stamps are off." },
            policy.Warnings);
    }

    [Fact]
    public void No_policy_file_is_no_policy()
    {
        Assert.Same(MachinePolicy.None, MachinePolicy.Load(Path.Combine(Path.GetTempPath(), "faden-none-" + Guid.NewGuid())));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://localhost:11434/v1/", true)]
    [InlineData("https://llm.example.test/v1", true)]
    [InlineData("https://llm.example.test/v2", false)]
    [InlineData("http://localhost:1234/v1", false)]
    public void An_endpoint_is_allowed_by_its_origin_or_its_exact_base(string baseUrl, bool allowed)
    {
        var policy = MachinePolicy.FromJson("""{"endpoints":["http://localhost:11434","https://llm.example.test/v1"]}""");

        Assert.Equal(allowed, policy.AllowsEndpoint(new Uri(baseUrl)));
    }

    // ---- enforced in the SDK ----

    [Fact]
    public void An_unreadable_policy_refuses_a_standalone_agent_at_build()
    {
        var e = Assert.Throws<MachinePolicyException>(() => Standalone(MachinePolicy.Unreadable).Build());

        Assert.Equal("standalone", e.Key);
    }

    [Fact]
    public void A_forbidden_standalone_is_refused_at_build()
    {
        var e = Assert.Throws<MachinePolicyException>(() => Standalone(MachinePolicy.FromJson("""{"server":"https://ai.example.test"}""")).Build());

        Assert.Equal("standalone", e.Key);
    }

    [Fact]
    public void An_endpoint_the_policy_does_not_list_is_refused_and_a_hosts_client_too()
    {
        var policy = MachinePolicy.FromJson("""{"endpoints":["https://llm.example.test"]}""");

        Assert.Equal("endpoints", Assert.Throws<MachinePolicyException>(() => Standalone(policy).Build()).Key);
        var host = FadenstackAgent.CreateBuilder().UseChatClient(new DirectChatClient(new ModelEndpoint { BaseUrl = new Uri("https://llm.example.test/v1"), Model = "m" }))
            .UseLocalProfile(AgentDefinition.FromJson(Definition)).UseMachinePolicy(policy);
        Assert.Equal("endpoints", Assert.Throws<MachinePolicyException>(() => host.Build()).Key);
        using var listed = Standalone(policy, "https://llm.example.test/v1").Build();
        Assert.NotNull(listed);
    }

    [Fact]
    public async Task Plain_http_in_the_lan_forbidden_takes_the_opt_in_away()
    {
        var fake = new OpenAiFake();
        using var agent = Standalone(MachinePolicy.FromJson("""{"plainHttp":"forbidden"}"""), "http://192.168.1.20:8000/v1", lan: true, fake: fake).Build();
        var session = await agent.StartSessionAsync();

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => session.SendAsync("Hi"));

        Assert.Equal(PlainHttpRefusal.ForbiddenByPolicy, e.Reason);
        Assert.Empty(fake.Requests);
        using var loopback = Standalone(MachinePolicy.FromJson("""{"plainHttp":"forbidden"}""")).Build(); // this computer stays
        Assert.Equal("Hello.", (await (await loopback.StartSessionAsync()).SendAsync("Hi")).Text);
    }

    [Fact]
    public async Task Plain_http_forbidden_also_forbids_a_key_to_this_computer()
    {
        var fake = new OpenAiFake();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://127.0.0.1:4000/v1"), Model = "m", Handler = fake, Key = new Fadenstack.Auth.StaticTokenProvider("k") })
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .UseMachinePolicy(MachinePolicy.FromJson("""{"plainHttp":"forbidden"}""")).Build();

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(async () => await (await agent.StartSessionAsync()).SendAsync("Hi"));

        Assert.Equal(PlainHttpRefusal.ForbiddenByPolicy, e.Reason);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Mcp_and_skills_the_policy_forbids_are_off_whatever_the_person_set()
    {
        using var agent = Standalone(MachinePolicy.FromJson("""{"mcp":"forbidden","skills":[]}"""),
            options: new LocalProfileOptions { LocalMcp = true, LocalSkills = true }).Build();
        using var open = Standalone(MachinePolicy.FromJson("""{"mcp":["notes-db"]}"""),
            options: new LocalProfileOptions { LocalMcp = true }).Build();

        await agent.StartAsync();
        await open.StartAsync();

        Assert.Equal("disable", agent.Profile.LocalMcp.Mode);
        Assert.Equal("disable", agent.Profile.LocalSkills.Mode);
        Assert.Equal("allow", open.Profile.LocalMcp.Mode);
        Assert.Equal(PolicyAccess.Listed, open.MachinePolicy.Mcp.Access); // the module enforces the list
    }

    [Fact]
    public async Task With_a_server_the_policy_narrows_what_the_server_allows_and_never_widens()
    {
        var gateway = new Gateway(localMcp: """{"mode":"allow","developer_mode":true}""");
        using var narrowed = gateway.Builder().UseMachinePolicy(MachinePolicy.FromJson("""{"server":"https://faden.test"}""")).Build();
        using var free = new Gateway(localMcp: """{"mode":"allow","developer_mode":false}""").Builder().UseMachinePolicy(MachinePolicy.FromJson("{}")).Build();
        using var off = new Gateway().Builder().UseMachinePolicy(MachinePolicy.FromJson("""{"mcp":"allowed"}""")).Build();

        await narrowed.StartAsync();
        await free.StartAsync();
        await off.StartAsync();

        Assert.Equal("disable", narrowed.Profile.LocalMcp.Mode);
        Assert.False(narrowed.Profile.LocalMcp.DeveloperMode);
        Assert.Equal("allow", free.Profile.LocalMcp.Mode);
        Assert.Equal("disable", off.Profile.LocalMcp.Mode); // the policy never turns on what the server left off
    }

    [Fact]
    public void Without_a_policy_nothing_changes()
    {
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Handler = new OpenAiFake() })
            .UseLocalProfile(AgentDefinition.FromJson(Definition)).Build();

        Assert.Same(MachinePolicy.None, agent.MachinePolicy);
    }
}

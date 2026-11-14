using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Governance;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// sdk/51 F02 §3 and F03: an agent on the person's own endpoint (I02 commits 13 and 14, I03 commits 4a, 8 and 9). Runs with
/// the paths collection: the data guard and the language file use the data folder.
/// </summary>
[Collection(PathsCollection.Name)]
public class StandaloneAgentTests : IDisposable
{
    private const string Definition = """
        {"slug":"docs-assistant","name":"Docs assistant","version":{
          "instructions":"You are the docs assistant.",
          "host_tool_contract":[{"name":"read_page","risk":"read"},{"name":"add_comment","risk":"write"},{"name":"stamp","risk":"mystery"}],
          "ui":{"greeting":"Hello there."}}}
        """;

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-standalone-" + Guid.NewGuid());

    public StandaloneAgentTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
        SecretProtection.Use(new FakeEncryptingProtector());
    }

    public void Dispose()
    {
        SecretProtection.ResetForTests();
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private static (AgentBuilder Builder, OpenAiFake Fake) Standalone(string baseUrl = "http://localhost:11434/v1",
        string? own = null, string definition = Definition)
    {
        var fake = new OpenAiFake();
        var builder = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri(baseUrl), Model = "qwen3:14b", Dialect = EndpointDialect.Ollama, Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson(definition), own)
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore());
        return (builder, fake);
    }

    // ---- I03 commit 8: the build rules ----

    [Fact]
    public void A_server_and_an_endpoint_together_are_refused()
    {
        var builder = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("t"))
            .UseAgent("docs-assistant")
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m" })
            .UseLocalProfile(AgentDefinition.FromJson(Definition));

        Assert.Contains("never both", Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);
    }

    [Fact]
    public void An_endpoint_needs_a_local_profile()
    {
        var builder = FadenstackAgent.CreateBuilder().UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m" });

        Assert.Contains("UseLocalProfile", Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);
    }

    [Fact]
    public void One_model_only_and_a_local_profile_only_with_an_endpoint()
    {
        var both = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m" })
            .UseChatClient(new EchoClient())
            .UseLocalProfile(AgentDefinition.FromJson(Definition));
        Assert.Throws<InvalidOperationException>(() => both.Build());

        var governed = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("t")).UseAgent("docs-assistant")
            .UseLocalProfile(AgentDefinition.FromJson(Definition));
        Assert.Throws<InvalidOperationException>(() => governed.Build());
    }

    [Fact]
    public void The_slug_comes_from_the_definition()
    {
        var (builder, _) = Standalone();
        using var agent = builder.Build();
        Assert.Equal("docs-assistant", agent.Slug);

        var (other, _) = Standalone();
        Assert.Throws<InvalidOperationException>(() => other.UseAgent("word-assistant").Build());
    }

    // ---- I03 commit 4a: the instructions, above the stages ----

    [Fact]
    public async Task The_definitions_instructions_then_the_persons_go_first_and_the_stages_see_them()
    {
        var (builder, fake) = Standalone(own: "  Answer in German.  ");
        string? seen = null;
        builder.AddChatStage(StageOrder.Custom, inner => new SeeingStage(inner, o => seen = o?.Instructions));
        using var agent = builder.Build();

        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");

        Assert.Equal("You are the docs assistant.\n\nAnswer in German.", seen);
        var messages = fake.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            .Json.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are the docs assistant.\n\nAnswer in German.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_history_budget_counts_the_instructions()
    {
        var longInstructions = new string('i', 4 * 900); // about 900 tokens
        var definition = Definition.Replace("You are the docs assistant.", longInstructions);
        var (builder, fake) = Standalone(definition: definition);
        using var agent = builder.UseHistoryBudget(1000).Build();
        var session = await agent.StartSessionAsync();
        fake.Answers.Enqueue(OpenAiFake.Text(new string('a', 4 * 200)));

        await session.SendAsync(new string('q', 4 * 50)); // turn 1: about 250 tokens in all
        await session.SendAsync("Second?");

        // 900 of the 1000 tokens go to the instructions, so turn 1 no longer fits beside them; without the reserve it would.
        var second = fake.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)).Last().Json;
        var contents = second.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Equal(longInstructions, contents[0]);
        Assert.Equal(new[] { longInstructions, "Second?" }, contents);
    }

    [Fact]
    public async Task With_a_server_no_instructions_are_set_on_the_device()
    {
        var gateway = new Gateway();
        string? seen = "unset";
        using var agent = gateway.Builder()
            .AddChatStage(StageOrder.Custom, inner => new SeeingStage(inner, o => seen = o?.Instructions))
            .Build();
        gateway.Says("Hi.");

        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");

        Assert.Null(seen);
        Assert.False(gateway.Server.Requests.Single(r => r.Path == Gateway.Chat).Json.GetProperty("messages")[0]
            .GetProperty("role").GetString() == "system");
    }

    // ---- I02 commit 14: the local profile ----

    [Fact]
    public async Task The_local_profile_comes_from_the_definition_with_no_server_parts()
    {
        var (builder, _) = Standalone();
        using var agent = builder.Build();

        await agent.StartAsync();

        Assert.Equal(AgentConnection.Standalone, agent.Connection);
        Assert.Null(agent.ServerClient);
        Assert.Throws<InvalidOperationException>(() => agent.Client);
        Assert.Equal(new Uri("http://localhost:11434/v1"), agent.EndpointUrl);
        var p = agent.Profile;
        Assert.Equal("docs-assistant", p.Slug);
        Assert.Equal("Docs assistant", p.Name);
        Assert.Equal("qwen3:14b", p.Model);
        Assert.Empty(p.Features);
        Assert.Null(p.Approvals);
        Assert.False(p.Images);
        Assert.False(p.Skills);
        Assert.Equal("standalone", p.Principal!.TenantId);
        Assert.Equal("standalone", p.Principal.UserId);
        Assert.Equal("ask", p.ToolModes.Default);
        Assert.Equal(ToolMode.Auto, agent.Ceiling);
        Assert.False(agent.BypassOffered);
        Assert.False(agent.Grants.Available);
        Assert.Equal("Hello there.", p.Ui!["greeting"].GetString());
        Assert.Equal("private", p.Ui["model_residency"].GetString());
        Assert.Equal("disable", p.LocalMcp.Mode);
        Assert.Equal("allow", p.LocalSkills.Mode);
    }

    [Theory]
    [InlineData("https://203.0.113.5/v1", "external")]
    [InlineData("http://192.168.1.20:8000/v1", "private")]
    public async Task The_model_residency_follows_the_endpoints_address(string baseUrl, string residency)
    {
        var fake = new OpenAiFake();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri(baseUrl), Model = "m", Handler = fake, AllowPlainHttpInLan = true })
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .Build();

        await agent.StartAsync();

        Assert.Equal(residency, agent.Profile.Ui!["model_residency"].GetString());
    }

    [Fact]
    public async Task A_hosts_own_chat_client_is_external_and_stays_the_hosts()
    {
        var echo = new EchoClient();
        var agent = FadenstackAgent.CreateBuilder().UseChatClient(echo).UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).Build();

        await agent.StartAsync();
        Assert.Equal("external", agent.Profile.Ui!["model_residency"].GetString());
        Assert.Null(agent.EndpointUrl);
        agent.Dispose();

        Assert.False(echo.Disposed);
    }

    [Fact]
    public async Task An_unknown_risk_asks_and_without_a_contract_every_tool_asks()
    {
        var (builder, _) = Standalone();
        using var agent = builder.AddTools(new LocalTools()).Build();
        await agent.StartAsync();

        Assert.Equal(ToolClass.Unknown, agent.Tools.Single(t => t.Name == "stamp").Class);
        Assert.Equal(ToolClass.Read, agent.Tools.Single(t => t.Name == "read_page").Class);

        var (bare, _) = Standalone(definition: """{"slug":"docs-assistant"}""");
        using var none = bare.AddTools(new LocalTools()).Build();
        await none.StartAsync();
        Assert.All(none.Tools, t => Assert.Equal(ToolClass.Unknown, t.Class));
    }

    [Fact]
    public async Task A_tool_source_sees_no_server_client()
    {
        var (builder, _) = Standalone();
        var source = new PeekingSource();
        using var agent = builder.AddToolSource(source).Build();

        await agent.StartAsync();

        Assert.Equal(AgentConnection.Standalone, source.Connection);
        Assert.Null(source.ServerClient);
        Assert.True(source.ClientThrew);
    }

    [Fact]
    public async Task The_language_stays_on_the_device()
    {
        var (builder, _) = Standalone();
        using (var agent = builder.Build())
        {
            await agent.StartAsync();
            Assert.Null(agent.Profile.User.Language);
            await agent.Backend.SetUserLanguageAsync("de", default);
        }
        var (again, fake) = Standalone();
        using var next = again.Build();
        await next.StartAsync();

        Assert.Equal("de", next.Profile.User.Language);
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.UserFolder("standalone"), "language")));
        Assert.Empty(fake.Requests);
    }

    // ---- I02 commit 13: the data guard ----

    [Fact]
    public async Task A_standalone_start_adopts_nothing_and_a_later_governed_sign_in_still_adopts()
    {
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions", Gateway.Slug), "s1.jsonl"), "x");
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("approvals"), "a.json"), "x");

        var fake = new OpenAiFake();
        using (var standalone = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson(Definition)).Build())
        {
            await standalone.StartSessionAsync(); // its own folder, users/standalone/
        }
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.DataRoot, "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.False(File.Exists(Path.Combine(FadenstackPaths.UserFolder("standalone"), "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.True(Directory.Exists(FadenstackPaths.UserFolder("standalone")));

        using var governed = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
                o => o.HttpClient = new HttpClient(new Gateway { Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1"}""" }.Server))
            .UseAgent(Gateway.Slug).Build();
        await governed.StartAsync();

        Assert.True(File.Exists(Path.Combine(FadenstackPaths.UserFolder("u-1"), "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.UserFolder("u-1"), "approvals", "a.json")));
    }

    [Fact]
    public void The_standalone_principal_never_adopts()
    {
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl"), "x");

        UserDataMigration.Adopt("standalone", NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(FadenstackPaths.DataRoot, "sessions", "s1.jsonl")));
        Assert.False(Directory.Exists(FadenstackPaths.UserFolder("standalone")));
    }

    // ---- a turn with a tool, and I03 commit 9: nothing to us ----

    [Fact]
    public async Task A_turn_with_a_tool_call_runs_against_the_endpoint_and_nothing_else()
    {
        var (builder, fake) = Standalone();
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "read_page", "{\"page\":3}")));
        fake.Answers.Enqueue(OpenAiFake.Text("Page 3 says hello."));
        var requests = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "System.Net.Http" or "Fadenstack.Client" || s.Name.StartsWith("Experimental.System.Net", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (requests) { requests.Add(a.Source.Name + " " + a.OperationName + " " + a.GetTagItem("http.route")); } },
        };
        ActivitySource.AddActivityListener(listener);
        using var agent = builder.AddTools(new LocalTools()).Build();

        var session = await agent.StartSessionAsync();
        var result = await session.SendAsync("What does page 3 say?");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Equal("Page 3 says hello.", result.Text);
        Assert.All(fake.Requests, r => Assert.Equal("localhost:11434", r.Url.Authority));
        Assert.Equal(2, fake.Requests.Count); // two model calls; no profile, events, manifest or grants anywhere
        var tool = fake.Requests[1].Json.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call-1", tool.GetProperty("tool_call_id").GetString());
        lock (requests)
        {
            // Every HTTP request the SDK made went through the endpoint's transport, to its two chat calls.
            Assert.Equal(2, requests.Count(r => r.StartsWith("Fadenstack.Client HTTP", StringComparison.Ordinal)));
            Assert.All(requests.Where(r => r.StartsWith("Fadenstack.Client HTTP", StringComparison.Ordinal)),
                r => Assert.EndsWith("/v1/chat/completions", r));
            Assert.DoesNotContain(requests, r => r.StartsWith("System.Net.Http", StringComparison.Ordinal)); // no real socket at all
        }
    }

    [Fact]
    public async Task A_write_tool_asks_and_a_decline_never_reaches_a_server()
    {
        var (builder, fake) = Standalone();
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "add_comment", "{\"text\":\"hi\"}")));
        fake.Answers.Enqueue(OpenAiFake.Text("I did not add it."));
        var asked = 0;
        using var agent = builder.AddTools(new LocalTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();

        var session = await agent.StartSessionAsync();
        var result = await session.SendAsync("Add a comment.");

        Assert.Equal(1, asked);
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        Assert.All(fake.Requests, r => Assert.EndsWith("/v1/chat/completions", r.Url.AbsolutePath));
    }

    [Fact]
    public async Task Each_answer_records_what_the_endpoint_did_about_its_watermark_and_a_servers_does_not()
    {
        var store = new MemorySessionStore();
        var fake = new OpenAiFake();
        fake.ChatHeaders["x-watermark"] = "false";
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Dialect = EndpointDialect.Ollama, Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson(Definition)).UseSessionStore(store).UseApprovalStore(new MemoryApprovalStore()).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");

        Assert.Equal(TextWatermark.Never, agent.EndpointWatermark!.Status);
        var answer = (await store.ReadAsync(agent.Slug, session.Id, default)).Single(e => e.Type == EntryTypes.AssistantMessage);
        var marking = answer.Data.GetProperty("endpoint_marking");
        Assert.Equal("Never", marking.GetProperty("status").GetString());
        Assert.False(marking.GetProperty("requested").GetBoolean());
        Assert.False(marking.GetProperty("confirmed").GetBoolean());
        Assert.False(marking.GetProperty("deterministic").GetBoolean());

        var gatewayStore = new MemorySessionStore();
        var gateway = new Gateway();
        using var governed = gateway.Builder(gatewayStore).Build();
        gateway.Says("Hi.");
        var g = await governed.StartSessionAsync();
        await g.SendAsync("Hi");
        Assert.Null(governed.EndpointWatermark);
        var governedAnswer = (await gatewayStore.ReadAsync(governed.Slug, g.Id, default)).Single(e => e.Type == EntryTypes.AssistantMessage);
        Assert.False(governedAnswer.Data.TryGetProperty("endpoint_marking", out _));
    }

    public sealed class LocalTools
    {
        [AgentTool(ToolClass.Read), Description("Reads a page.")]
        public string read_page(int page) => $"Page {page}: hello";

        [AgentTool(ToolClass.Write), Description("Adds a comment.")]
        public string add_comment(string text) => "added";

        [AgentTool(ToolClass.Read), Description("Stamps.")]
        public string stamp() => "stamped";
    }

    private sealed class PeekingSource : IToolSource
    {
        public AgentConnection Connection { get; private set; }
        public FadenstackClient? ServerClient { get; private set; }
        public bool ClientThrew { get; private set; }

        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken)
        {
            Connection = context.Connection;
            ServerClient = context.ServerClient;
            try
            {
                _ = context.Client;
            }
            catch (InvalidOperationException)
            {
                ClientThrew = true;
            }
            return Task.FromResult<IReadOnlyList<AgentTool>>(Array.Empty<AgentTool>());
        }
    }

    private sealed class SeeingStage(IChatClient inner, Action<ChatOptions?> see) : DelegatingChatClient(inner)
    {
        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            see(options);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }

    private sealed class EchoClient : IChatClient
    {
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "echo")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "echo");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }
}

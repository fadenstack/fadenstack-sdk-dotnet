using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Storage;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>The thinking menu of a standalone agent (F7: no levels without a switch; F10: Off for a model that thinks anyway).</summary>
[Collection(PathsCollection.Name)]
public class StandaloneThinkingTests : IDisposable
{
    private const string Definition = """
        {"slug":"docs-assistant","name":"Docs assistant","version":{"instructions":"Hi.","host_tool_contract":[],"ui":{}}}
        """;

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-thinking-" + Guid.NewGuid());

    public StandaloneThinkingTests()
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

    private static EndpointReport Report(bool? thinksWhenOff) => new()
    {
        BaseUrl = new Uri("http://localhost:11434/v1"), Model = "gpt-oss:20b", Server = EndpointServer.Ollama, Reachable = true,
        KeyAccepted = true, Models = ["gpt-oss:20b"], Chat = true, Usage = true, ToolCalls = true, ThinksWhenOff = thinksWhenOff,
    };

    private static (FadenstackAgent Agent, OpenAiFake Fake) With(EndpointDialect dialect, EndpointReport? report = null)
    {
        var fake = new OpenAiFake();
        var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "gpt-oss:20b", Dialect = dialect, Handler = fake }, report)
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .Build();
        return (agent, fake);
    }

    private static JsonElement LastChat(OpenAiFake fake) =>
        fake.Requests.Last(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)).Json;

    public static IEnumerable<object[]> NoSwitch() =>
        [[EndpointDialect.OpenAICompatible], [EndpointDialect.LMStudio], [EndpointDialect.Mistral]];

    public static IEnumerable<object[]> WithSwitch() =>
        [[EndpointDialect.Ollama], [EndpointDialect.OpenRouter], [EndpointDialect.Vllm], [EndpointDialect.OpenAI]];

    [Theory]
    [MemberData(nameof(NoSwitch))]
    public async Task A_dialect_with_no_thinking_switch_offers_no_level(EndpointDialect dialect)
    {
        var (agent, _) = With(dialect);
        await agent.StartAsync();

        Assert.Empty(agent.ReasoningChoices.Levels);
    }

    [Theory]
    [MemberData(nameof(WithSwitch))]
    public async Task A_dialect_with_a_switch_keeps_its_five_levels(EndpointDialect dialect)
    {
        var (agent, _) = With(dialect);
        await agent.StartAsync();

        Assert.Equal(ReasoningLevels.All, agent.ReasoningChoices.Levels);
    }

    [Fact]
    public async Task With_thinks_when_off_true_Off_sends_the_lowest_level_on_a_reasoning_effort_dialect()
    {
        var (agent, fake) = With(EndpointDialect.Ollama, Report(true));
        await agent.StartAsync();
        Assert.Equal("lowest", agent.ReasoningChoices.ThinksWhenOff);

        await agent.SetReasoningAsync(ReasoningLevel.Off);
        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Equal("low", LastChat(fake).GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Without_the_flag_Off_still_sends_none()
    {
        var (agent, fake) = With(EndpointDialect.Ollama, Report(false));
        await agent.StartAsync();
        Assert.Null(agent.ReasoningChoices.ThinksWhenOff);

        await agent.SetReasoningAsync(ReasoningLevel.Off);
        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Equal("none", LastChat(fake).GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_chat_template_dialect_keeps_the_off_switch_and_says_the_model_thinks_anyway()
    {
        var (agent, fake) = With(EndpointDialect.Vllm, Report(true));
        await agent.StartAsync();
        Assert.Equal("always", agent.ReasoningChoices.ThinksWhenOff);

        await agent.SetReasoningAsync(ReasoningLevel.Off);
        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.False(LastChat(fake).GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }
}

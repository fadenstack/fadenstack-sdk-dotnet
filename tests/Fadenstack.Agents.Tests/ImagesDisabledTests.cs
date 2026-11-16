using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Image generation is off in core 0.7.1 (EU AI Act, Article 5): a tool that makes pictures is hidden while the agent has no
/// pictures (<see cref="FadenstackAgent.ImagesAvailable"/>), and a refusal with <c>images_disabled</c> reaches the model and the person.
/// </summary>
[Collection(PathsCollection.Name)]
public class ImagesDisabledTests : IDisposable
{
    private const string Contract = """[{"name":"insert_image","risk":"write"},{"name":"read_page","risk":"read"}]""";
    private const string ImagesRoute = "/v1/agents/docs-assistant/images/generations";
    private const string ModelText = "Image generation is turned off in this version (EU AI Act, Article 5). Do not try again.";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-images-" + Guid.NewGuid());

    public ImagesDisabledTests()
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

    /// <summary>An app's tools: one that asks the server for a picture, one that reads.</summary>
    private sealed class AppTools
    {
        public FadenstackAgent? Agent { get; set; }

        public List<string> Ran { get; } = new();

        [AgentTool(ToolClass.Write, MakesPictures = true), Description("Inserts a picture made from a prompt.")]
        public async Task<string> InsertImage(string prompt)
        {
            Ran.Add("insert_image");
            var pictures = await Agent!.Client.Agent!.GenerateImagesAsync(new ImageRequest { Prompt = prompt });
            return $"inserted {pictures.Pictures.Count}";
        }

        [AgentTool(ToolClass.Read), Description("Reads a page.")]
        public string ReadPage(int page)
        {
            Ran.Add("read_page");
            return $"page {page}";
        }
    }

    private static Gateway Refusing(bool images = true)
    {
        var gateway = new Gateway(contract: Contract) { Images = images };
        gateway.Server.On("POST", ImagesRoute, _ => FakeServer.Respond(HttpStatusCode.Forbidden, new
        {
            error = new
            {
                message = "Image generation is turned off in this version.", type = "invalid_request_error", code = "images_disabled",
            },
        }));
        return gateway;
    }

    private static List<string> Offered(Recorded request) =>
        !request.Json.TryGetProperty("tools", out var tools) ? new List<string>()
            : tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList();

    private static (FadenstackAgent Agent, AppTools Tools) Agent(Gateway gateway)
    {
        var tools = new AppTools();
        var agent = gateway.Builder().AddTools(tools).Build();
        tools.Agent = agent;
        return (agent, tools);
    }

    [Fact]
    public async Task A_picture_tool_is_offered_when_the_profile_says_images()
    {
        var gateway = Refusing(images: true).Says("Hello.");
        var (agent, _) = Agent(gateway);
        using var _ = agent;

        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("Hi");

        Assert.True(agent.ImagesAvailable);
        Assert.Equal(new[] { "insert_image", "read_page" }, Offered(gateway.ChatRequests[0]).OrderBy(n => n));
    }

    [Fact]
    public async Task A_picture_tool_is_hidden_when_the_profile_says_no_images_and_stays_registered()
    {
        var gateway = Refusing(images: false).Says("Hello.");
        var (agent, _) = Agent(gateway);
        using var _ = agent;

        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("Hi");

        Assert.False(agent.ImagesAvailable);
        Assert.Equal(new[] { "read_page" }, Offered(gateway.ChatRequests[0]));
        Assert.Equal(new[] { "insert_image", "read_page" }, agent.Tools.Select(t => t.Name).OrderBy(n => n));
        Assert.True(agent.Tools.Single(t => t.Name == "insert_image").MakesPictures);
    }

    [Fact]
    public async Task A_call_to_a_hidden_picture_tool_is_refused_as_unknown_and_does_not_run()
    {
        var gateway = Refusing(images: false).Calls(("call-1", "insert_image", """{"prompt":"a cat"}""")).Says("Done.");
        var (agent, tools) = Agent(gateway);
        using var _ = agent;
        var completed = new List<ToolCallCompleted>();

        await foreach (var e in (await agent.StartSessionAsync(ToolMode.Auto)).StreamAsync("A cat, please."))
        {
            if (e is ToolCallCompleted c)
            {
                completed.Add(c);
            }
        }

        Assert.Empty(tools.Ran);
        Assert.Equal(("error", "Tool 'insert_image' does not exist here."), (completed.Single().Outcome, completed.Single().Result));
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Path == ImagesRoute);
    }

    [Fact]
    public async Task A_profile_that_flips_images_changes_the_next_turn()
    {
        var gateway = Refusing(images: true).Says("One.").Says("Two.").Says("Three.");
        var (agent, _) = Agent(gateway);
        using var _ = agent;
        var now = DateTimeOffset.UtcNow;
        agent.Clock = () => now;

        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("1");
        gateway.Images = false;
        now += TimeSpan.FromSeconds(61);
        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("2");
        gateway.Images = true;
        now += TimeSpan.FromSeconds(61);
        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("3");

        Assert.Equal(new[] { true, false, true },
            gateway.ChatRequests.Select(r => Offered(r).Contains("insert_image")));
    }

    [Fact]
    public async Task A_standalone_agent_has_no_pictures()
    {
        var fake = new OpenAiFake();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "qwen3:14b", Dialect = EndpointDialect.Ollama, Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant","version":{"host_tool_contract":[{"name":"insert_image","risk":"write"},{"name":"read_page","risk":"read"}]}}"""))
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .AddTools(new AppTools())
            .Build();

        var session = await agent.StartSessionAsync(ToolMode.Auto);

        Assert.False(agent.ImagesAvailable);
        Assert.Equal(new[] { "read_page" }, agent.Offer(ToolMode.Auto, session).Select(t => ((Microsoft.Extensions.AI.AITool)t).Name));
        Assert.Null(agent.Find("insert_image"));
        Assert.Contains(agent.Tools, t => t.Name == "insert_image");
    }

    [Fact]
    public async Task A_refusal_tells_the_model_raises_one_notice_per_turn_and_hides_the_picture_tools_from_the_next_round()
    {
        var gateway = Refusing().Calls(("call-1", "insert_image", """{"prompt":"a cat"}"""), ("call-2", "insert_image", """{"prompt":"a dog"}"""))
            .Says("I could not make them.").Says("Anything else?");
        var (agent, tools) = Agent(gateway);
        using var _ = agent;
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var notices = new List<GovernanceNotice>();
        var completed = new List<ToolCallCompleted>();

        AgentResult? result = null;
        await foreach (var e in session.StreamAsync("A cat and a dog."))
        {
            if (e is NoticeRaised n)
            {
                notices.Add(n.Notice);
            }
            if (e is ToolCallCompleted c)
            {
                completed.Add(c);
            }
            result = (e as TurnCompleted)?.Result ?? result;
        }

        Assert.Single(tools.Ran); // the second call of the round meets the tool already hidden, and reads the same text
        Assert.All(completed, c => Assert.Equal((ModelText, "error"), (c.Result, c.Outcome)));
        var notice = Assert.Single(notices);
        Assert.Equal(("images_disabled", "tool", "images"), (notice.Outcome, notice.Stage, notice.Category));
        Assert.Contains("Article 5", notice.Message);
        Assert.Equal(notice, Assert.Single(result!.Notices));
        Assert.False(agent.ImagesAvailable);
        // the model was told the same text, and the picture tool is gone from the round after the refusal and from the next turn
        Assert.Contains(ModelText, gateway.ChatRequests[1].Json.GetRawText());
        Assert.Contains("insert_image", Offered(gateway.ChatRequests[0]));
        Assert.DoesNotContain("insert_image", Offered(gateway.ChatRequests[1]));

        await session.SendAsync("Again?");
        Assert.DoesNotContain("insert_image", Offered(gateway.ChatRequests[2]));
        Assert.Equal(1, gateway.Server.Requests.Count(r => r.Path == ImagesRoute));
    }

    [Fact]
    public async Task One_refusal_raises_one_images_disabled_notice()
    {
        var gateway = Refusing().Calls(("call-1", "insert_image", """{"prompt":"a cat"}""")).Says("No.");
        var (agent, _) = Agent(gateway);
        using var _ = agent;
        var raised = 0;

        await foreach (var e in (await agent.StartSessionAsync(ToolMode.Auto)).StreamAsync("A cat."))
        {
            raised += e is NoticeRaised { Notice.Outcome: "images_disabled" } ? 1 : 0;
        }

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Another_403_is_a_plain_tool_error_and_leaves_the_picture_tools_on()
    {
        var gateway = Refusing();
        gateway.Server.On("POST", ImagesRoute, _ => FakeServer.Respond(HttpStatusCode.Forbidden, new
        {
            error = new { message = "Not allowed.", type = "permission_error", code = "agent_not_granted" },
        }));
        gateway.Calls(("call-1", "insert_image", """{"prompt":"a cat"}""")).Says("No.");
        var (agent, _) = Agent(gateway);
        using var _ = agent;
        var notices = 0;

        await foreach (var e in (await agent.StartSessionAsync(ToolMode.Auto)).StreamAsync("A cat."))
        {
            notices += e is NoticeRaised ? 1 : 0;
        }

        Assert.Equal(0, notices);
        Assert.True(agent.ImagesAvailable);
    }
}

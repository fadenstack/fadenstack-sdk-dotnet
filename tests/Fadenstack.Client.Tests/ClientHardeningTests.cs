using System.Net;
using Fadenstack.Auth;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// The client's checks that make it fail closed as the TypeScript client does (sdk/43 §4, sdk/46 C6): the second
/// sign-in's page stays on the API's own origin, a report holds at most 100 events, and FastAPI's validation errors are
/// read.
/// </summary>
public class ClientHardeningTests
{
    private const string StepUps = "/api/agent-tokens/step-ups";

    private static FadenstackClient Client(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "docs-assistant", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    private static object Started(string uri, string complete) => new
    {
        step_up_code = "synthetic-private-code", user_code = "AAAA-BBBB", verification_uri = uri, verification_uri_complete = complete,
        expires_in = 600, interval = 5,
    };

    // -- the second sign-in's page ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_step_up_whose_pages_are_on_the_servers_own_origin_starts()
    {
        var server = new FakeServer().Json("POST", StepUps, Started("https://faden.test/step-up", "https://FADEN.test:443/step-up#AAAA-BBBB"),
            HttpStatusCode.Created);
        using var client = Client(server);

        var started = await client.Agent!.StartStepUpAsync(30, "test");

        Assert.Equal(("AAAA-BBBB", 5), (started.UserCode, started.Interval));
    }

    [Theory]
    [InlineData("https://foreign.test/step-up", "https://faden.test/step-up#x")]
    [InlineData("https://faden.test/step-up", "https://foreign.test/step-up#x")]
    [InlineData("https://faden.test:8443/step-up", "https://faden.test/step-up#x")]
    [InlineData("http://faden.test/step-up", "http://faden.test/step-up#x")]
    [InlineData("https://faden.test.foreign.test/step-up", "https://faden.test/step-up#x")]
    [InlineData("https://faden.test@foreign.test/step-up", "https://faden.test/step-up#x")]
    [InlineData("https://user:pass@faden.test/step-up", "https://faden.test/step-up#x")]
    [InlineData("/step-up", "https://faden.test/step-up#x")]
    [InlineData("javascript:alert(1)", "https://faden.test/step-up#x")]
    [InlineData("", "https://faden.test/step-up#x")]
    public async Task A_step_up_page_on_another_origin_is_refused(string uri, string complete)
    {
        var server = new FakeServer().Json("POST", StepUps, Started(uri, complete), HttpStatusCode.Created);
        using var client = Client(server);

        var refused = await Assert.ThrowsAsync<FadenstackException>(() => client.Agent!.StartStepUpAsync(30, "test"));

        Assert.Contains("origin changed", refused.Message);
    }

    // -- the report ----------------------------------------------------------------------------------------------------------

    private static List<AgentClientEvent> Events(int count) =>
        Enumerable.Range(0, count).Select(i => (AgentClientEvent)new ApprovalDecidedEvent { Tool = $"tool-{i}", Decision = "approved" }).ToList();

    [Fact]
    public async Task A_report_of_more_than_100_events_is_refused_before_it_is_sent()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/docs-assistant/events", new { });
        using var client = Client(server);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Agent!.ReportAsync(Events(101)));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_report_of_100_events_is_sent()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/docs-assistant/events", new { });
        using var client = Client(server);

        await client.Agent!.ReportAsync(Events(100));

        Assert.Equal(100, server.Requests.Single().Json.GetProperty("events").GetArrayLength());
    }

    // -- the error's detail --------------------------------------------------------------------------------------------------

    private static async Task<FadenstackApiException> Failed(string body, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var server = new FakeServer().Json("GET", "/v1/agents/docs-assistant/profile", body, status);
        using var client = Client(server);
        return await Assert.ThrowsAsync<FadenstackApiException>(() => client.Agent!.GetProfileAsync());
    }

    [Fact]
    public async Task A_validation_detail_array_is_read_into_the_message_and_the_code()
    {
        var error = await Failed("""{"detail":[{"loc":["body","minutes"],"msg":"Input should be greater than 0","type":"greater_than"},{"loc":["body","tools"],"msg":"Field required","type":"missing"}]}""");

        Assert.Equal(("Input should be greater than 0; Field required", "validation_error", HttpStatusCode.UnprocessableEntity),
            (error.Message, error.Code, error.Status));
    }

    [Fact]
    public async Task A_validation_detail_array_without_messages_gives_the_status_text()
    {
        var error = await Failed("""{"detail":[{"loc":["body"]},"x",3]}""");

        Assert.Equal("validation_error", error.Code);
        Assert.StartsWith("422", error.Message);
    }

    [Theory]
    [InlineData("""{"detail":"Not Found"}""", "Not Found", null)]
    [InlineData("""{"detail":{"message":"Nope.","code":"agent_retired"}}""", "Nope.", "agent_retired")]
    public async Task The_other_shapes_of_detail_are_read_as_before(string body, string message, string? code)
    {
        var error = await Failed(body, HttpStatusCode.BadRequest);

        Assert.Equal((message, code), (error.Message, error.Code));
    }
}

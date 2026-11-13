using System.Net;
using System.Text;
using Fadenstack.Auth;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A typed answer that is empty or JSON null is the server's error, named by its route, never a null behind a non-null
/// type that fails later as a NullReferenceException (sdk/51 I00 #5, I-10).
/// </summary>
public class EmptyAnswerTests
{
    private const string Agent = "docs-assistant";

    private static (FadenstackClient Client, FakeServer Server) Make()
    {
        var server = new FakeServer();
        var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = Agent,
            Credentials = new StaticTokenProvider("t"), HttpClient = new HttpClient(server),
        });
        return (client, server);
    }

    private static HttpResponseMessage Answer(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static TheoryData<string> Bodies() => new() { "", "  \n", "null" };

    /// <summary>Every typed entry point of the transport, by name.</summary>
    public static TheoryData<string, string> Calls()
    {
        var data = new TheoryData<string, string>();
        foreach (var body in new[] { "", "  \n", "null" })
        {
            foreach (var call in new[] { "get", "post", "post-with-headers", "put", "patch", "send-with-header", "get-json-element" })
            {
                data.Add(call, body);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Every_typed_call_with_an_empty_answer_fails_with_the_route(string call, string body)
    {
        var (client, server) = Make();
        server.On("GET", "/v1/things/42", _ => Answer(body)).On("POST", "/v1/things/42", _ => Answer(body))
            .On("PUT", "/v1/things/42", _ => Answer(body)).On("PATCH", "/v1/things/42", _ => Answer(body));
        var t = client.Transport;
        var url = t.Gateway("v1/things/42");

        Task act() => call switch
        {
            "get" => t.GetAsync<AgentProfile>(url),
            "post" => t.PostAsync<AgentGrant>(url, new { }),
            "post-with-headers" => t.PostAsync<AgentGrant>(url, new { }, new Dictionary<string, string> { ["X-Faden-Turn-Id"] = "t" }),
            "put" => t.PutAsync<ManifestReceipt>(url, new { }),
            "patch" => t.PatchAsync<Session>(url, new { }),
            "send-with-header" => t.SendJsonAsync<ImagesResponse>(HttpMethod.Post, url, new { }, "X-Faden-Provenance", default),
            "get-json-element" => t.GetAsync<System.Text.Json.JsonElement>(url),
            _ => throw new ArgumentOutOfRangeException(nameof(call)),
        };

        var error = await Assert.ThrowsAsync<FadenstackException>(act);
        Assert.Contains("/v1/things/{id}", error.Message); // the route, its ids left out
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task An_empty_profile_is_the_servers_error_not_a_null_profile(string body)
    {
        var (client, server) = Make();
        server.On("GET", $"/v1/agents/{Agent}/profile", _ => Answer(body));

        var error = await Assert.ThrowsAsync<FadenstackException>(() => client.Agent!.GetProfileAsync());

        Assert.Contains($"GET /v1/agents/{Agent}/profile", error.Message);
    }

    [Fact]
    public async Task An_empty_list_wrapper_grant_or_services_answer_is_the_servers_error()
    {
        var (client, server) = Make();
        server.On("GET", "/v1/sessions", _ => Answer(""))
            .On("POST", $"/v1/agents/{Agent}/grants", _ => Answer("null"))
            .On("GET", "/v1/services", _ => Answer(""));

        await Assert.ThrowsAsync<FadenstackException>(() => client.Sessions.ListAsync());
        await Assert.ThrowsAsync<FadenstackException>(() =>
            client.Agent!.CreateGrantAsync(new GrantRequest { Kind = "work" }, "conversation-1", "proof-1"));
        await Assert.ThrowsAsync<FadenstackException>(() => client.Services.ListAsync());
    }

    [Fact]
    public async Task A_call_that_expects_no_body_still_takes_an_empty_answer()
    {
        var (client, server) = Make();
        server.On("DELETE", "/v1/sessions/7b0e0f5c-0c1e-4c66-9d7a-2c8c4f0e9b11", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On("POST", $"/v1/agents/{Agent}/approvals/a-1", _ => Answer(""));

        await client.Sessions.DeleteAsync(Guid.Parse("7b0e0f5c-0c1e-4c66-9d7a-2c8c4f0e9b11"));
        await client.Agent!.AnswerApprovalAsync("a-1", approved: true);
    }
}

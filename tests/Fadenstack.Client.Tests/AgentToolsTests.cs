using System.Net;
using Fadenstack.Auth;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The organisation's MCP tools the server lists for the signed-in user (<c>GET /v1/agents/{agent}/tools</c>).</summary>
public class AgentToolsTests
{
    private const string Tools = "/v1/agents/excel/tools";

    private static FadenstackClient Client(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "excel", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    [Fact]
    public async Task It_lists_the_tools_with_whether_each_is_allowed_and_switched_on()
    {
        var server = new FakeServer().Json("GET", Tools, """
            {"tools":[
              {"name":"mcp.github.search","server":"github","description":"Searches code.","allowed":true,"enabled":true},
              {"name":"mcp.github.delete_repo","server":"github","description":"Deletes a repository.","allowed":false,"enabled":true},
              {"name":"mcp.time.now","server":"time","description":"The time.","allowed":true,"enabled":false}]}
            """);
        using var client = Client(server);

        var tools = await client.Agent!.ListToolsAsync();

        Assert.Equal(new[]
        {
            new AgentToolInfo { Name = "mcp.github.search", Server = "github", Description = "Searches code.", Allowed = true, Enabled = true },
            new AgentToolInfo { Name = "mcp.github.delete_repo", Server = "github", Description = "Deletes a repository.", Allowed = false, Enabled = true },
            new AgentToolInfo { Name = "mcp.time.now", Server = "time", Description = "The time.", Allowed = true, Enabled = false },
        }, tools);
        var request = server.Requests.Last();
        Assert.Equal(("GET", Tools), (request.Method, request.Path));
        Assert.Equal("Bearer t", request.Header("authorization"));
    }

    [Fact]
    public async Task A_server_with_no_tools_lists_none_and_one_that_cannot_list_them_answers_null()
    {
        using var none = Client(new FakeServer().Json("GET", Tools, """{"tools":[]}"""));
        Assert.Empty((await none.Agent!.ListToolsAsync())!);

        using var old = Client(new FakeServer()); // no route: 404 {"detail":"Not Found"}
        Assert.Null(await old.Agent!.ListToolsAsync());
    }

    [Fact]
    public async Task An_unknown_agent_is_still_an_error()
    {
        using var gone = Client(new FakeServer().Json("GET", Tools,
            new { error = new { code = "agent_not_found", message = "No agent 'excel'." } }, HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<FadenstackApiException>(() => gone.Agent!.ListToolsAsync());
        Assert.Equal("agent_not_found", error.Code);
    }

    [Fact]
    public async Task A_forbidden_user_is_an_error_not_an_empty_list()
    {
        using var forbidden = Client(new FakeServer().Json("GET", Tools,
            new { error = new { code = "permission_denied", message = "No." } }, HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<FadenstackPermissionException>(() => forbidden.Agent!.ListToolsAsync());
    }
}

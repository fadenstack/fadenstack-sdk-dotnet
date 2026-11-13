using System.Net;
using Fadenstack.Client.Tests;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The public face of the seam (sdk/51 I02 commit 9): how the agent is connected, its server client, and the server's
/// tools list with today's null-or-list answer. Hosts keep using <see cref="FadenstackAgent.Client"/> with a server.
/// </summary>
public class ConnectionTests
{
    private const string Tools = "/v1/agents/docs-assistant/tools";

    [Fact]
    public void An_agent_on_a_server_is_governed_and_its_server_client_is_its_client()
    {
        using var agent = new Gateway().Builder().Build();

        Assert.Equal(AgentConnection.Governed, agent.Connection);
        Assert.NotNull(agent.ServerClient);
        Assert.Same(agent.Client, agent.ServerClient);
    }

    [Fact]
    public async Task The_servers_tools_come_as_the_route_lists_them_and_null_from_a_server_without_the_list()
    {
        var gateway = new Gateway();
        gateway.Server.Json("GET", Tools, new
        {
            tools = new[] { new { name = "mcp.github.search", server = "GitHub", allowed = true, enabled = true } },
        });
        using var agent = gateway.Builder().Build();

        var tools = await agent.ListServerToolsAsync();

        Assert.Equal("mcp.github.search", Assert.Single(tools!).Name);

        gateway.Server.On("GET", Tools, _ => FakeServer.Respond(HttpStatusCode.NotFound, new { detail = "Not Found" }));
        Assert.Null(await agent.ListServerToolsAsync());
    }
}

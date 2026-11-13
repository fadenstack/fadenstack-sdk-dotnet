using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Who disposes the agent's client (sdk/51 I02 commit 1 moved it into the backend): the agent disposes a client its builder
/// made, and leaves a host's own client to the host.
/// </summary>
public class ClientOwnershipTests
{
    [Fact]
    public async Task A_client_the_builder_made_is_disposed_with_the_agent()
    {
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"))
            .UseAgent(Gateway.Slug)
            .Build();
        var http = agent.Client.Transport.HttpClient; // the client's own, with no server behind it

        agent.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://faden.test/"));
    }

    [Fact]
    public async Task A_hosts_own_client_stays_the_hosts()
    {
        var server = new FakeServer().Json("GET", "/v1/features", new { edition = "community", features = Array.Empty<string>() });
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = Gateway.Slug,
            Credentials = new StaticTokenProvider("agent-token"), HttpClient = new HttpClient(server),
        });
        var agent = FadenstackAgent.CreateBuilder().UseClient(client).UseAgent(Gateway.Slug).Build();

        agent.Dispose();

        Assert.Equal("community", (await client.GetFeaturesAsync()).Edition);
    }
}

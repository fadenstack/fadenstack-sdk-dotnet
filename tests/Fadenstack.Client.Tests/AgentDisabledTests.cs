using System.Net;
using Fadenstack.Auth;

using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The off-switch's client part (core 0.7.1): the 403 <c>agent_disabled</c> told from other refusals, and the profile's <c>enabled</c>.</summary>
public class AgentDisabledTests
{
    private static FadenstackClient ClientFor(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "docs-assistant", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "agent_disabled", "permission_error", true)]
    [InlineData(HttpStatusCode.Forbidden, "agent_not_granted", "permission_error", false)]
    [InlineData(HttpStatusCode.Forbidden, "agent_retired", "permission_error", false)]
    [InlineData(HttpStatusCode.Forbidden, null, "permission_error", false)]
    [InlineData(HttpStatusCode.BadRequest, "agent_disabled", "permission_error", false)]
    public async Task A_refusal_with_agent_disabled_is_told_apart_from_other_errors(HttpStatusCode status, string? code, string type, bool expected)
    {
        var server = new FakeServer().Json("GET", "/v1/agents/docs-assistant/grants", new { error = new { message = "No.", type, code } }, status);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAnyAsync<FadenstackApiException>(() => client.Agent!.ListGrantsAsync());

        Assert.Equal(expected, error.IsAgentDisabled);
        Assert.Equal("agent_disabled", ErrorCodes.AgentDisabled);
    }

    [Fact]
    public async Task The_profile_says_enabled_and_a_profile_without_the_key_is_on()
    {
        var server = new FakeServer()
            .Json("GET", "/v1/agents/docs-assistant/profile", new { id = "a", slug = "docs-assistant", name = "Docs", enabled = false });
        using var off = ClientFor(server);
        Assert.False((await off.Agent!.GetProfileAsync()).Enabled);

        var older = new FakeServer().Json("GET", "/v1/agents/docs-assistant/profile", new { id = "a", slug = "docs-assistant", name = "Docs" });
        using var on = ClientFor(older);
        Assert.True((await on.Agent!.GetProfileAsync()).Enabled);
        Assert.Equal("AgentDisabled", Fadenstack.Governance.ChatOutcome.AgentDisabled.ToString());
    }
}

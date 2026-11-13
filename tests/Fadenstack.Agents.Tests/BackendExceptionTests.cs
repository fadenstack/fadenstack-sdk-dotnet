using System.Net;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The grant lifecycle, Bypass and the step-up catch the server's failures by type: a 404, 403 or 503 by status, the
/// step-up challenge by its code, and "could not be reached" as a FadenstackException. Through the seam (sdk/51 I02
/// commit 6) each type arrives exactly as the route throws it, so those catches keep working.
/// </summary>
public class BackendExceptionTests
{
    private const string Grants = "/v1/agents/docs-assistant/grants";
    private const string Grant = Grants + "/g-1";

    private static (FadenstackAgent Agent, Gateway Gateway) Make()
    {
        var gateway = new Gateway();
        return (gateway.Builder().Build(), gateway);
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        FakeServer.Respond(status, new { detail = new { code, message = "Refused." } });

    [Fact]
    public async Task The_step_up_challenge_arrives_as_the_authentication_error_with_its_code()
    {
        var (agent, gateway) = Make();
        gateway.Server.On("POST", Grants, _ => Error(HttpStatusCode.Unauthorized, "bypass_step_up_required"));

        var error = await Assert.ThrowsAsync<FadenstackAuthenticationException>(() =>
            agent.Backend.CreateGrantAsync(new GrantRequest { Kind = GrantKinds.Bypass }, "c-1", "proof", default));

        Assert.Equal("bypass_step_up_required", error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_refusal_arrives_as_the_api_error_with_its_status(HttpStatusCode status)
    {
        var (agent, gateway) = Make();
        gateway.Server.On("POST", Grant + "/resume", _ => Error(status, "refused"))
            .On("GET", Grant, _ => Error(status, "refused"))
            .On("GET", Grants, _ => Error(status, "refused"));

        foreach (var call in new Func<Task>[]
        {
            () => agent.Backend.ResumeGrantAsync("g-1", default),
            () => agent.Backend.GetGrantAsync("g-1", default),
            () => agent.Backend.ListGrantsAsync(default),
        })
        {
            Assert.Equal(status, (await Assert.ThrowsAnyAsync<FadenstackApiException>(call)).Status);
        }
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_arrives_as_the_FadenstackException_with_its_cause()
    {
        var (agent, gateway) = Make();
        gateway.Server.On("GET", Grants, _ => throw new HttpRequestException("connection refused"))
            .On("POST", "/api/agent-tokens/step-ups", _ => throw new HttpRequestException("connection refused"));

        var listing = await Assert.ThrowsAsync<FadenstackException>(() => agent.Backend.ListGrantsAsync(default));
        var stepUp = await Assert.ThrowsAsync<FadenstackException>(() => agent.Backend.StartStepUpAsync(30, "1.0", default));

        Assert.IsType<HttpRequestException>(listing.InnerException);
        Assert.IsType<HttpRequestException>(stepUp.InnerException);
    }
}

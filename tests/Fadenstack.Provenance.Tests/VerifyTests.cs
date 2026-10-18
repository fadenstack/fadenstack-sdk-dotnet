using System.Net;
using Fadenstack.Agents.Tests;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>Checking content: the agent's route for an agent's client, the user's route otherwise.</summary>
public class VerifyTests
{
    private static FadenstackClient NewClient(Gateway gateway, string token, string? agent) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = agent, Credentials = new StaticTokenProvider(token),
        HttpClient = new HttpClient(gateway.Server),
    });

    [Fact]
    public async Task An_agents_client_checks_on_its_agents_route_and_finds_a_record_it_had_signed()
    {
        var gateway = new Gateway();
        using var client = NewClient(gateway, "agent-token", Gateway.Slug);
        const string text = "The pump P-200 is due for a seal check next week.";
        var signed = await client.Agent!.Provenance.SignRecordAsync(new RecordRequest
        {
            ContentSha256 = ContentHash.TextSha256(text), MediaType = "text/plain", EditKind = EditKind.Generated,
        });

        var found = await client.Provenance.VerifyAsync(VerifyRequest.ForText("  " + text.Replace(" ", "\n") + " ")); // reflowed: the same text
        var none = await client.Agent.Provenance.VerifyAsync(VerifyRequest.ForText("Something a person wrote."));

        Assert.Equal((VerifyVerdict.AiGenerated, signed.Record.Id), (found.Verdict, Assert.Single(found.Findings).RecordId));
        Assert.Equal(VerifyVerdict.NoMarkFound, none.Verdict);
        Assert.Equal(new[] { "/v1/agents/docs-assistant/provenance/verify", "/v1/agents/docs-assistant/provenance/verify" },
            gateway.Server.Requests.Where(r => r.Path.EndsWith("/verify")).Select(r => r.Path));
        var sent = gateway.Server.Requests.First(r => r.Path.EndsWith("/verify")).Json;
        Assert.Equal(new[] { "signed", "text" }, sent.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_client_without_an_agent_checks_on_the_users_route_which_refuses_an_agent_token()
    {
        var gateway = new Gateway();
        using var user = NewClient(gateway, "user-token", null);
        using var withAgentToken = NewClient(gateway, "agent-token", null);

        var result = await user.Provenance.VerifyAsync(VerifyRequest.ForFile(new byte[] { 1, 2, 3 }, "image/png"));
        var refused = await Assert.ThrowsAnyAsync<FadenstackApiException>(() => withAgentToken.Provenance.VerifyAsync(VerifyRequest.ForText("x")));

        Assert.Equal(VerifyVerdict.NoMarkFound, result.Verdict);
        Assert.Equal("/v1/provenance/verify", gateway.Server.Requests.First(r => r.Path.EndsWith("/verify")).Path);
        Assert.Equal((HttpStatusCode.Forbidden, "agent_token_not_allowed"), (refused.Status, refused.Code));
    }

    [Fact]
    public async Task A_check_of_both_or_of_a_file_without_its_media_type_is_refused_before_it_goes_out()
    {
        var gateway = new Gateway();
        using var client = NewClient(gateway, "agent-token", Gateway.Slug);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Provenance.VerifyAsync(new VerifyRequest { Text = "a", File = "Yg==" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Provenance.VerifyAsync(new VerifyRequest()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Provenance.VerifyAsync(new VerifyRequest { File = "Yg==" }));
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Path.EndsWith("/verify"));
    }
}

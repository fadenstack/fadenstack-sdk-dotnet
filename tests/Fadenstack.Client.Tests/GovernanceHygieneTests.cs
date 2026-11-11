using System.Net;
using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The client takes what the gateway adds later without breaking: refusals by their mechanics, odd events, error details.</summary>
public class GovernanceHygieneTests
{
    private const string Route = "/v1/agents/docs-assistant/chat/completions";

    private static (FadenstackClient Client, FakeServer Server) Make()
    {
        var server = new FakeServer();
        var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"),
            Agent = "docs-assistant",
            Credentials = new StaticTokenProvider("agent-token"),
            HttpClient = new HttpClient(server),
        });
        return (client, server);
    }

    private static string Chunk(object delta, string? finish = null) =>
        FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta, finish_reason = finish } } });

    private static readonly ChatMessage[] Hi = [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task A_policy_blocked_422_is_a_prompt_refusal_whatever_outcome_it_names()
    {
        var (client, server) = Make();
        server.Json("POST", Route, new
        {
            error = new { type = "governance_error", code = "policy_blocked", message = "Not allowed." },
            faden = new { governance = new { outcome = "restrict", stage = "input", message = "Not allowed.", decision_id = "d-5" } },
        }, (HttpStatusCode)422);

        var response = await client.Chat.GetStreamingResponseAsync(Hi).ToChatResponseAsync();

        var notice = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>()).Notice;
        Assert.Equal((ChatFinishReason.ContentFilter, "prompt_blocked", "d-5"), (response.FinishReason, notice.Outcome, notice.DecisionId));
    }

    [Fact]
    public async Task A_governance_event_that_does_not_refuse_is_an_event_only()
    {
        var (client, server) = Make();
        server.On("POST", Route, _ => FakeServer.Sse(
            FakeServer.Fadenstack(new { type = "CUSTOM", name = "faden.governance", value = new { outcome = "observe", message = "Noted." } }),
            Chunk(new { content = "Fine." }), Chunk(new { }, "stop"), FakeServer.Done));

        var response = await client.Chat.GetStreamingResponseAsync(Hi).ToChatResponseAsync();

        Assert.Empty(response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>());
        Assert.Contains(response.Messages.SelectMany(m => m.Contents).OfType<FadenstackEventContent>(), e => e.Kind == "faden.governance");
        Assert.Equal(("Fine.", ChatFinishReason.Stop), (response.Text, response.FinishReason));
    }

    [Fact]
    public async Task A_whole_answer_with_a_governance_outcome_that_does_not_refuse_stands()
    {
        var (client, server) = Make();
        server.Json("POST", Route, new
        {
            id = "c1",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "Fine." }, finish_reason = "stop" } },
            faden = new { governance = new { outcome = "observe", message = "Noted." } },
        });

        var response = await client.Chat.GetResponseAsync(Hi);

        Assert.Equal(("Fine.", ChatFinishReason.Stop), (response.Text, response.FinishReason));
        Assert.Empty(response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>());
        Assert.Equal("observe", Assert.IsType<GovernanceNotice>(response.AdditionalProperties![FadenstackChatOptions.Governance]).Outcome);
    }

    [Fact]
    public async Task A_faden_event_that_is_not_json_is_skipped()
    {
        var (client, server) = Make();
        server.On("POST", Route, _ => FakeServer.Sse(
            "event: faden\ndata: {not json\n\n", "event: faden\ndata: [1,2]\n\n",
            Chunk(new { content = "Still here." }), Chunk(new { }, "stop"), FakeServer.Done));

        var response = await client.Chat.GetStreamingResponseAsync(Hi).ToChatResponseAsync();

        Assert.Equal("Still here.", response.Text);
    }

    [Fact]
    public async Task A_streamed_tool_call_carries_its_index()
    {
        var (client, server) = Make();
        server.On("POST", Route, _ => FakeServer.Sse(
            Chunk(new { tool_calls = new[]
            {
                new { index = 0, id = "", function = new { name = "a", arguments = "{}" } },
                new { index = 1, id = "", function = new { name = "b", arguments = "{}" } },
            } }),
            Chunk(new { }, "tool_calls"), FakeServer.Done));

        var response = await client.Chat.GetStreamingResponseAsync(Hi).ToChatResponseAsync();

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(new object?[] { 0, 1 }, calls.Select(c => c.AdditionalProperties![FadenstackChatOptions.CallIndex]));
    }

    [Fact]
    public async Task Every_api_exception_keeps_the_errors_faden_block()
    {
        var (client, server) = Make();
        server.Json("POST", Route, new
        {
            error = new { type = "invalid_request_error", code = "quiet_hours", message = "Closed at night." },
            faden = new { decision_id = "d-7", governance = new { outcome = "restrict" } },
        }, HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<FadenstackApiException>(() => client.Chat.GetResponseAsync(Hi));

        Assert.Equal("quiet_hours", error.Code);
        Assert.Equal("d-7", error.Faden!.Value.GetProperty("decision_id").GetString());
        Assert.Equal("restrict", error.Faden.Value.GetProperty("governance").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_error_without_a_faden_block_has_none()
    {
        var (client, server) = Make();
        server.Json("POST", Route, new { error = new { message = "No grant.", code = "agent_not_granted" } }, HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<FadenstackPermissionException>(() => client.Chat.GetResponseAsync(Hi));

        Assert.Null(error.Faden);
    }
}

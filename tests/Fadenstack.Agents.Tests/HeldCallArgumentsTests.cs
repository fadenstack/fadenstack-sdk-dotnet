using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The card of a call the gateway holds shows the call's arguments (core 0.7.1 rc22): the members of the JSON object when the text is
/// complete, else the text as one read-only value that the card says is shortened. An older gateway sends none: the card stays as it was.
/// </summary>
public class HeldCallArgumentsTests
{
    private static async Task<ApprovalRequest> Asked(string event_)
    {
        var gateway = new Gateway();
        gateway.Then(event_, Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-1", new { approval_id = "ap-1", approved = true });
        ApprovalRequest? seen = null;
        using var agent = gateway.Builder().UseApprovals((request, _) =>
        {
            seen = request;
            return Task.FromResult(ApprovalDecision.Approve());
        }).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        return Assert.IsType<ApprovalRequest>(seen);
    }

    [Fact]
    public async Task A_complete_JSON_object_fills_the_arguments_like_the_apps_own_calls()
    {
        var request = await Asked(Gateway.HeldCall("""{"title":"x","page":2,"labels":["a","b"]}"""));

        Assert.False(request.ArgumentsShortened);
        Assert.Equal(new[] { "title", "page", "labels" }, request.Arguments.Keys);
        Assert.Equal("x", ((JsonElement)request.Arguments["title"]!).GetString());
        Assert.Equal(2, ((JsonElement)request.Arguments["page"]!).GetInt32());
        Assert.Equal("""["a","b"]""", ((JsonElement)request.Arguments["labels"]!).GetRawText());
    }

    [Fact]
    public async Task A_cut_text_is_one_read_only_value_and_says_it_is_shortened()
    {
        var request = await Asked(Gateway.HeldCall("{\"title\":\"x", truncated: true, sha256: new string('a', 64)));

        Assert.True(request.ArgumentsShortened);
        Assert.Equal("{\"title\":\"x", Assert.Single(request.Arguments).Value);
    }

    [Fact]
    public async Task A_cut_text_that_would_parse_is_still_shown_as_text()
    {
        var request = await Asked(Gateway.HeldCall("""{"title":"x"}""", truncated: true));

        Assert.True(request.ArgumentsShortened);
        Assert.Equal("""{"title":"x"}""", Assert.Single(request.Arguments).Value);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public async Task Text_that_is_not_a_JSON_object_is_one_read_only_value(string text)
    {
        var request = await Asked(Gateway.HeldCall(text));

        Assert.True(request.ArgumentsShortened);
        Assert.Equal(text, Assert.Single(request.Arguments).Value);
    }

    [Fact]
    public async Task Text_longer_than_the_gateways_cap_is_cut_here_too()
    {
        var request = await Asked(Gateway.HeldCall(new string('x', 9000)));

        Assert.True(request.ArgumentsShortened);
        Assert.Equal(8000, ((string)Assert.Single(request.Arguments).Value!).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_older_gateway_sends_none_and_the_card_stays_as_it_was(string? text)
    {
        var request = await Asked(Gateway.HeldCall(text));

        Assert.False(request.ArgumentsShortened);
        Assert.Empty(request.Arguments);
    }
}

using System.Net;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The host-call check (<c>POST /v1/agents/{agent}/tools/check</c>): its wire form, and how its answer is read.</summary>
public class HostToolCheckTests
{
    private const string Route = "/v1/agents/docs/tools/check";
    private static readonly string Hex = new('a', 64);

    private static FadenstackClient Client(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "docs", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    private static HostToolCheck Check(HostToolOrigin origin = HostToolOrigin.UserCommand, string? callId = null, HostToolPhase phase = HostToolPhase.Prepare,
        HostToolDecision? prepared = null) => new()
    {
        Name = "delete_page", Arguments = JsonDocument.Parse("""{"page":7}""").RootElement.Clone(), ToolMode = ToolMode.Bypass, Origin = origin,
        CallId = callId, Phase = phase, SchemaSha256 = Hex, Prepared = prepared, ConversationId = "conv-1", TurnId = "turn-1",
    };

    private static object Answer(object? decision = null, object? toolClass = null, object? mode = null, object? rule = null, object? principal = null,
        string? stamp = null) => new
    {
        decision = decision ?? "ask", tool_class = toolClass ?? "destructive", effective_tool_mode = mode ?? "auto", required_by_rule = rule ?? false,
        message = "Individual approval", profile_stamp = stamp ?? Hex, arguments_stamp = Hex, decision_stamp = Hex, decision_id = (string?)null,
        principal = principal ?? new { tenant_id = "t", user_id = "u", agent_id = "a", device_id = "d" },
    };

    [Fact]
    public async Task The_request_has_the_servers_wire_form_and_the_conversation_headers()
    {
        var server = new FakeServer().Json("POST", Route, Answer());
        using var client = Client(server);

        var decision = await client.Agent!.CheckHostToolAsync(Check());

        var request = Assert.Single(server.Requests);
        var body = request.Json;
        Assert.Equal(("delete_page", "bypass", "user_command", "prepare", Hex), (body.GetProperty("name").GetString(), body.GetProperty("tool_mode").GetString(),
            body.GetProperty("origin").GetString(), body.GetProperty("phase").GetString(), body.GetProperty("schema_sha256").GetString()));
        Assert.Equal(7, body.GetProperty("arguments").GetProperty("page").GetInt32());
        Assert.False(body.TryGetProperty("call_id", out _));
        Assert.False(body.TryGetProperty("prepared_profile", out _));
        Assert.Equal(("conv-1", "turn-1"), (request.Header("X-Faden-Conversation-Id"), request.Header("X-Faden-Turn-Id")));
        Assert.Equal((HostToolVerdict.Ask, ToolClass.Destructive, ToolMode.Auto, false, "Individual approval"),
            (decision.Decision, decision.ToolClass, decision.EffectiveToolMode, decision.RequiredByRule, decision.Message));
        Assert.Equal(new AgentPrincipal { TenantId = "t", UserId = "u", AgentId = "a", DeviceId = "d" }, decision.Principal);
    }

    [Fact]
    public async Task A_dispatch_check_carries_the_three_stamps_of_the_prepare_answer_and_a_model_call_its_id()
    {
        var server = new FakeServer().Json("POST", Route, Answer());
        using var client = Client(server);
        var prepared = new HostToolDecision { ProfileStamp = new string('1', 64), ArgumentsStamp = new string('2', 64), DecisionStamp = new string('3', 64) };

        await client.Agent!.CheckHostToolAsync(Check(HostToolOrigin.ModelTool, "call-9", HostToolPhase.Dispatch, prepared));

        var body = server.Requests.Single().Json;
        Assert.Equal(("model_tool", "call-9", "dispatch"), (body.GetProperty("origin").GetString(), body.GetProperty("call_id").GetString(), body.GetProperty("phase").GetString()));
        Assert.Equal((new string('1', 64), new string('2', 64), new string('3', 64)), (body.GetProperty("prepared_profile").GetString(),
            body.GetProperty("prepared_arguments").GetString(), body.GetProperty("prepared_decision").GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_model_host_call_without_its_call_id_is_refused_locally_and_nothing_is_sent(string? callId)
    {
        var server = new FakeServer().Json("POST", Route, Answer());
        using var client = Client(server);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Agent!.CheckHostToolAsync(Check(HostToolOrigin.ModelTool, callId)));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(HostToolOrigin.UserCommand, "user_command")]
    [InlineData(HostToolOrigin.UserHistory, "user_history")]
    public async Task A_command_needs_no_call_id(HostToolOrigin origin, string wire)
    {
        var server = new FakeServer().Json("POST", Route, Answer());
        using var client = Client(server);

        await client.Agent!.CheckHostToolAsync(Check(origin));

        Assert.Equal(wire, server.Requests.Single().Json.GetProperty("origin").GetString());
    }

    [Theory]
    [InlineData("allow_everything")]
    [InlineData(null)]
    [InlineData(5)]
    public async Task A_decision_the_sdk_does_not_know_is_a_denial(object? decision)
    {
        var server = new FakeServer().Json("POST", Route, Answer(decision: decision ?? ""));
        using var client = Client(server);

        Assert.Equal(HostToolVerdict.Deny, (await client.Agent!.CheckHostToolAsync(Check())).Decision);
    }

    [Fact]
    public async Task A_class_a_level_or_a_rule_the_sdk_does_not_know_narrows()
    {
        var server = new FakeServer().Json("POST", Route, Answer(toolClass: "dangerous", mode: "turbo", rule: "maybe"));
        using var client = Client(server);

        var decision = await client.Agent!.CheckHostToolAsync(Check());

        Assert.Equal((ToolClass.Unknown, ToolMode.Off, true), (decision.ToolClass, decision.EffectiveToolMode, decision.RequiredByRule));
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("principal")]
    [InlineData("shape")]
    public async Task An_answer_without_valid_stamps_or_principal_is_an_error_not_a_decision(string defect)
    {
        var server = new FakeServer().Json("POST", Route, defect switch
        {
            "stamp" => Answer(stamp: "not-hex"),
            "principal" => Answer(principal: new { tenant_id = "t" }),
            _ => "[1,2]",
        });
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<FadenstackException>(() => client.Agent!.CheckHostToolAsync(Check()));

        Assert.Contains("valid host-command policy decision", error.Message);
    }

    [Fact]
    public async Task An_older_server_without_the_route_answers_404()
    {
        using var client = Client(new FakeServer());

        var error = await Assert.ThrowsAnyAsync<FadenstackApiException>(() => client.Agent!.CheckHostToolAsync(Check()));

        Assert.Equal(HttpStatusCode.NotFound, error.Status);
    }

    [Fact]
    public async Task The_dispatch_check_feature_has_the_servers_name()
    {
        await Task.CompletedTask;
        Assert.Equal("host_tools.dispatch_check", HostToolFeatures.DispatchCheck);
    }
}

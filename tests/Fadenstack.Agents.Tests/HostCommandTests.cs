using System.Net;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Host tools the person starts (a button, a module's view), through the server's current policy: checked, approved for this
/// call alone, checked again right before they run, recorded for the chat and left out of the model's history. Ported from the
/// TypeScript SDK's <c>commands.test.ts</c>.
/// </summary>
public class HostCommandTests
{
    private const string OtherUser = """{"tenant_id":"tenant-test","user_id":"another-user","agent_id":"agent-test","device_id":"device-test"}""";

    private sealed class Rig : IDisposable
    {
        public Gateway Gateway = null!;
        public FadenstackAgent Agent = null!;
        public AgentSession Session = null!;
        public PageTools Tools = null!;
        public List<ApprovalRequest> Asked = new();

        public void Dispose() => Agent.Dispose();
    }

    private static async Task<Rig> Fixture(bool answer = true, Func<AgentBuilder, AgentBuilder>? configure = null, ToolMode mode = ToolMode.Auto,
        HostDecision? decision = null)
    {
        var rig = new Rig { Gateway = new Gateway().UseHostChecks(decision), Tools = new PageTools() };
        var builder = rig.Gateway.Builder().AddTools(rig.Tools)
            .UseApprovals((request, _) => { rig.Asked.Add(request); return Task.FromResult(answer ? ApprovalDecision.Approve() : ApprovalDecision.Deny()); });
        rig.Agent = (configure is null ? builder : configure(builder)).Build();
        rig.Session = await rig.Agent.StartSessionAsync(mode);
        return rig;
    }

    private static Dictionary<string, object?> Page(int page) => new() { ["page"] = page };

    private static string Phase(Recorded check) => check.Json.GetProperty("phase").GetString()!;

    [Theory]
    [InlineData("Undo\nforged command")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    public async Task A_label_that_is_not_one_line_is_refused_before_policy_or_dispatch(string label)
    {
        using var rig = await Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Session.InvokeToolAsync("delete_page", Page(7), new HostCommandOptions { Label = label }));

        Assert.Empty(rig.Gateway.Checks);
        Assert.Empty(rig.Tools.Ran);
    }

    [Fact]
    public async Task A_label_of_two_hundred_characters_is_kept_trimmed_and_one_of_two_hundred_and_one_is_refused()
    {
        using var rig = await Fixture();
        var longest = new string('x', 200);

        await rig.Session.InvokeToolAsync("delete_page", Page(7), new HostCommandOptions { Label = longest });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Session.InvokeToolAsync("delete_page", Page(8), new HostCommandOptions { Label = longest + "x" }));

        var command = Assert.Single(rig.Session.Transcript, e => e.Type == EntryTypes.UserCommand);
        Assert.Equal(longest, command.Data.GetProperty("label").GetString());
        Assert.Equal(new[] { "delete_page 7" }, rig.Tools.Ran);
    }

    [Fact]
    public async Task It_checks_twice_asks_individually_and_runs_without_a_model_request()
    {
        using var rig = await Fixture();

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7), new HostCommandOptions { Origin = HostToolOrigin.UserHistory });

        Assert.Equal("ok", result.Outcome);
        Assert.Equal(new[] { "delete_page 7" }, rig.Tools.Ran);
        var asked = Assert.Single(rig.Asked);
        Assert.False(asked.CanAllowAlways);
        Assert.False(asked.CanAllowForSession);
        Assert.False(asked.RequiredByRule);
        Assert.Equal(7, ((JsonElement)asked.Arguments["page"]!).GetInt32());
        Assert.Equal(new[] { "prepare", "dispatch" }, rig.Gateway.Checks.Select(Phase));
        var first = rig.Gateway.Checks[0];
        Assert.Equal("user_history", first.Json.GetProperty("origin").GetString());
        Assert.Equal(7, first.Json.GetProperty("arguments").GetProperty("page").GetInt32());
        Assert.Equal(rig.Session.Id, first.Header("X-Faden-Conversation-Id"));
        Assert.Equal(result.TurnId, first.Header("X-Faden-Turn-Id"));
        Assert.Empty(rig.Gateway.ChatRequests);
        Assert.Empty(rig.Session.History);
    }

    [Fact]
    public async Task The_command_is_recorded_with_its_label_and_never_its_arguments_and_the_model_does_not_see_it()
    {
        using var rig = await Fixture();
        rig.Gateway.Says("Done.");

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7), new HostCommandOptions { Label = "  Undo page delete  " });
        await rig.Session.SendAsync("A separate message");

        var command = Assert.Single(rig.Session.Transcript, e => e.Type == EntryTypes.UserCommand);
        Assert.Equal(result.TurnId, command.TurnId);
        Assert.Equal(("delete_page", "user_command", "Undo page delete"),
            (command.Data.GetProperty("name").GetString(), command.Data.GetProperty("origin").GetString(), command.Data.GetProperty("label").GetString()));
        Assert.False(command.Data.TryGetProperty("arguments", out _));
        var sent = rig.Gateway.ChatRequests.Single().Body;
        Assert.DoesNotContain("Undo page delete", sent);
        var messages = rig.Gateway.ChatRequests.Single().Json.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(new[] { "user" }, messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(new[] { "A separate message", "Done." }, rig.Session.History.Select(m => m.Text));
    }

    [Fact]
    public async Task The_history_builder_leaves_out_every_turn_that_holds_a_user_command()
    {
        var entries = new List<TranscriptEntry>
        {
            new() { Type = EntryTypes.UserMessage, TurnId = "t1", Data = Json("""{"text":"question"}""") },
            new() { Type = EntryTypes.AssistantMessage, TurnId = "t1", Data = Json("""{"text":"answer","tool_calls":[]}""") },
            new() { Type = EntryTypes.TurnEnd, TurnId = "t1", Status = TurnStatus.Ok, Data = Json("{}") },
            new() { Type = EntryTypes.UserCommand, TurnId = "t2", Data = Json("""{"name":"delete_page","origin":"user_command","label":"Undo"}""") },
            new() { Type = EntryTypes.ToolStart, TurnId = "t2", Data = Json("""{"call_id":"c1","name":"delete_page","source":"host"}""") },
            new() { Type = EntryTypes.ToolComplete, TurnId = "t2", Data = Json("""{"call_id":"c1","name":"delete_page","source":"host","result":"deleted","outcome":"ok"}""") },
            new() { Type = EntryTypes.TurnEnd, TurnId = "t2", Status = TurnStatus.Ok, Data = Json("{}") },
        };

        var history = new ContextBuilder().Build(entries);

        Assert.Equal(new[] { "question", "answer" }, history.Select(m => m.Text));
    }

    [Theory]
    [InlineData(ToolMode.Off)]
    [InlineData(ToolMode.ReadOnly)]
    public async Task A_consequential_command_is_refused_in_a_mode_that_hides_it(ToolMode mode)
    {
        using var rig = await Fixture();
        rig.Session.ToolMode = mode;

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal("denied", result.Outcome);
        Assert.Empty(rig.Tools.Ran);
        Assert.Empty(rig.Gateway.Checks);
    }

    [Fact]
    public async Task A_central_deny_and_the_users_decline_both_leave_no_native_effect()
    {
        using var central = await Fixture(decision: new HostDecision("deny"));
        Assert.Equal("denied", (await central.Session.InvokeToolAsync("delete_page", Page(7))).Outcome);
        Assert.Empty(central.Tools.Ran);
        Assert.Empty(central.Asked);

        using var user = await Fixture(answer: false);
        Assert.Equal("denied", (await user.Session.InvokeToolAsync("delete_page", Page(7))).Outcome);
        Assert.Empty(user.Tools.Ran);
        Assert.Single(user.Gateway.Checks);
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("profile")]
    [InlineData("arguments")]
    [InlineData("decision")]
    public async Task Drift_between_the_checks_stops_the_command(string kind)
    {
        using var rig = await Fixture();
        rig.Gateway.OnCheck = request => Phase(request) == "prepare" ? null : kind switch
        {
            "principal" => new HostDecision(Principal: OtherUser).ToWire(),
            "profile" => new HostDecision(Profile: 'd').ToWire(),
            "arguments" => new HostDecision(Arguments: 'd').ToWire(),
            _ => new HostDecision(DecisionStamp: 'd').ToWire(),
        };

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal("error", result.Outcome);
        Assert.Empty(rig.Tools.Ran);
        Assert.Equal(2, rig.Gateway.Checks.Count);
    }

    [Fact]
    public async Task A_dispatch_answer_that_denies_stops_the_command()
    {
        using var rig = await Fixture();
        rig.Gateway.OnCheck = request => Phase(request) == "prepare" ? null : new HostDecision("deny").ToWire();

        Assert.Equal("error", (await rig.Session.InvokeToolAsync("delete_page", Page(7))).Outcome);
        Assert.Empty(rig.Tools.Ran);
    }

    [Fact]
    public async Task A_changed_authenticated_subject_is_refused_before_asking()
    {
        using var rig = await Fixture(decision: new HostDecision(Principal: OtherUser));

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal("error", result.Outcome);
        Assert.Empty(rig.Asked);
        Assert.Empty(rig.Tools.Ran);
    }

    [Fact]
    public async Task An_old_gateway_without_the_route_fails_closed_instead_of_falling_back_to_a_model_or_a_direct_call()
    {
        using var rig = await Fixture();
        rig.Gateway.Check = null;

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal("error", result.Outcome);
        Assert.Empty(rig.Tools.Ran);
        Assert.Empty(rig.Gateway.ChatRequests);
    }

    [Fact]
    public async Task A_decision_the_sdk_does_not_know_is_a_refusal()
    {
        using var rig = await Fixture();
        rig.Gateway.OnCheck = _ => new { decision = "allow_everything", tool_class = "write", effective_tool_mode = "auto", required_by_rule = false,
            message = "Surprise", profile_stamp = Gateway.Stamp('a'), arguments_stamp = Gateway.Stamp('b'), decision_stamp = Gateway.Stamp('c'),
            principal = JsonDocument.Parse(Gateway.HostPrincipal).RootElement };

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal(("denied", "Surprise"), (result.Outcome, result.Result));
        Assert.Empty(rig.Tools.Ran);
        Assert.Empty(rig.Asked);
    }

    [Fact]
    public async Task Arguments_that_a_stage_transformed_are_not_disclosed_to_the_check_endpoint()
    {
        using var rig = await Fixture(configure: b => b.AddToolStage(StageOrder.Pii, new ReplacePage(8)));

        var result = await rig.Session.InvokeToolAsync("delete_page", Page(7));

        Assert.Equal("error", result.Outcome);
        Assert.Empty(rig.Gateway.Checks);
        Assert.Empty(rig.Tools.Ran);
    }

    [Fact]
    public async Task Arguments_that_do_not_fit_the_tool_are_refused_before_policy()
    {
        using var rig = await Fixture();

        var result = await rig.Session.InvokeToolAsync("delete_page", new Dictionary<string, object?> { ["page"] = "seven" });

        Assert.Equal("error", result.Outcome);
        Assert.Contains("'page' must be integer", result.Result);
        Assert.Empty(rig.Gateway.Checks);
    }

    [Fact]
    public async Task The_arguments_are_copied_when_the_command_starts()
    {
        using var rig = await Fixture();
        var arguments = Page(7);

        var running = rig.Session.InvokeToolAsync("delete_page", arguments);
        arguments["page"] = 9;
        await running;

        Assert.Equal(new[] { "delete_page 7" }, rig.Tools.Ran);
    }

    [Fact]
    public async Task Stop_during_approval_cancels_the_same_command_without_dispatch()
    {
        var requested = new TaskCompletionSource();
        var rig = new Rig { Gateway = new Gateway().UseHostChecks(), Tools = new PageTools() };
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools)
            .UseApprovals(async (_, ct) => { requested.SetResult(); await Task.Delay(Timeout.Infinite, ct); return ApprovalDecision.Deny(); }).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync(ToolMode.Auto);
        using var stop = new CancellationTokenSource();

        var running = rig.Session.InvokeToolAsync("delete_page", Page(7), null, stop.Token);
        await requested.Task;
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(rig.Gateway.Checks);
        Assert.Empty(rig.Tools.Ran);
        Assert.Equal(TurnStatus.Cancelled, rig.Session.Transcript.Last().Status);
    }

    [Fact]
    public async Task A_second_command_or_message_cannot_start_while_one_runs_and_a_bad_tool_name_is_refused()
    {
        var gate = new TaskCompletionSource();
        var rig = new Rig { Gateway = new Gateway().UseHostChecks(), Tools = new PageTools() };
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools).UseApprovals(async (_, _) => { await gate.Task; return ApprovalDecision.Approve(); }).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync(ToolMode.Auto);

        var first = rig.Session.InvokeToolAsync("delete_page", Page(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.InvokeToolAsync("delete_page", Page(8)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.SendAsync("hello"));
        gate.SetResult();
        await first;

        Assert.Equal(new[] { "delete_page 7" }, rig.Tools.Ran);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Session.InvokeToolAsync("not a tool name!", Page(1)));
    }

    [Fact]
    public async Task A_command_on_an_unknown_tool_ends_as_an_error_result()
    {
        using var rig = await Fixture();

        var result = await rig.Session.InvokeToolAsync("no_such_tool", Page(1));

        Assert.Equal("error", result.Outcome);
        Assert.Empty(rig.Gateway.Checks);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A stage that changes the page, as local PII unmasking would change a value.</summary>
    internal sealed class ReplacePage : IToolStage
    {
        private readonly int _page;

        public ReplacePage(int page) => _page = page;

        public Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            invocation.Arguments["page"] = _page;
            return next(invocation, cancellationToken);
        }
    }
}

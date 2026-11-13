using System.Net;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Fresh central policy for host calls of the model (<c>host_tools.dispatch_check</c>): checked with origin <c>model_tool</c>
/// before the person is asked and right before the tool runs, failing closed once the server has offered it. Ported from the
/// TypeScript SDK's <c>hostPolicy.test.ts</c>, with a test for each rule of the model-call recheck.
/// </summary>
public class HostPolicyTests
{
    private const string OtherUser = """{"tenant_id":"tenant-test","user_id":"another-user","agent_id":"agent-test","device_id":"device-test"}""";
    private const string Open = """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"]}""";
    private const string Closed = """{"standing":[],"longest_grant_minutes":0,"grant_classes":[]}""";

    private static readonly HostDecision Reviewed = new("ask", "write", "ask", false, "Review this call");

    private sealed class Rig : IDisposable
    {
        public Gateway Gateway = null!;
        public FadenstackAgent Agent = null!;
        public ChatSession Session = null!;
        public PageTools Tools = null!;
        public List<ApprovalRequest> Asked = new();

        public void Dispose() => Agent.Dispose();
    }

    private static Gateway Model() => new Gateway().UseHostChecks(Reviewed)
        .Calls(("model-call", "add_comment", """{"page":8,"text":"Reviewed content"}""")).Says("Finished");

    private static async Task<Rig> Fixture(bool answer = true, Func<AgentBuilder, AgentBuilder>? configure = null, bool required = true,
        Func<AgentBuilder, AgentBuilder>? approvals = null, bool start = true)
    {
        var rig = new Rig { Gateway = Model(), Tools = new PageTools() };
        rig.Gateway.Approvals = Open;
        var builder = rig.Gateway.Builder().AddTools(rig.Tools);
        builder = approvals is null
            ? builder.UseApprovals((request, _) => { rig.Asked.Add(request); return Task.FromResult(answer ? ApprovalDecision.Approve() : ApprovalDecision.Deny()); })
            : approvals(builder);
        if (required)
        {
            builder.RequireHostToolPolicyChecks();
        }
        rig.Agent = (configure is null ? builder : configure(builder)).Build();
        if (start)
        {
            rig.Session = await rig.Agent.StartSessionAsync();
        }
        return rig;
    }

    private static string Phase(Recorded check) => check.Json.GetProperty("phase").GetString()!;

    private static bool Commented(Rig rig) => rig.Tools.Ran.Any(r => r.StartsWith("add_comment"));

    [Fact]
    public async Task A_server_that_advertises_the_check_is_used_without_an_app_opt_in()
    {
        using var rig = await Fixture(required: false);

        Assert.True(rig.Agent.HostToolPolicyChecks);
        await rig.Session.SendAsync("write");

        Assert.Equal(new[] { "prepare", "dispatch" }, rig.Gateway.Checks.Select(Phase));
    }

    [Fact]
    public async Task A_server_that_does_not_advertise_it_keeps_the_legacy_path_unless_the_app_requires_it()
    {
        var rig = new Rig { Gateway = new Gateway().Calls(("model-call", "add_comment", """{"page":8,"text":"x"}""")).Says("Done."), Tools = new PageTools() };
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools).UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync();

        await rig.Session.SendAsync("write");

        Assert.False(rig.Agent.HostToolPolicyChecks);
        Assert.Empty(rig.Gateway.Checks);
        Assert.True(Commented(rig));
    }

    [Fact]
    public async Task Standing_restrictions_are_read_fresh_before_a_stored_approval_is_considered()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        var rig = new Rig { Gateway = Model(), Tools = new PageTools() };
        rig.Gateway.Approvals = Open;
        rig.Agent = rig.Gateway.Builder(approvals: store).AddTools(rig.Tools).RequireHostToolPolicyChecks()
            .UseApprovals((request, _) => { rig.Asked.Add(request); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync();
        rig.Gateway.Approvals = Closed;

        await rig.Session.SendAsync("write");

        var asked = Assert.Single(rig.Asked);
        Assert.False(asked.CanAllowAlways);
        Assert.False(asked.CanAllowForSession);
    }

    [Fact]
    public async Task It_checks_prepare_and_dispatch_under_the_model_origin_and_runs_only_the_reviewed_arguments()
    {
        using var rig = await Fixture();

        await rig.Session.SendAsync("write");

        Assert.Equal(new[] { "add_comment 8 Reviewed content" }, rig.Tools.Ran);
        Assert.Single(rig.Asked);
        Assert.Equal(new[] { ("model_tool", "prepare", "model-call"), ("model_tool", "dispatch", "model-call") },
            rig.Gateway.Checks.Select(c => (c.Json.GetProperty("origin").GetString()!, Phase(c), c.Json.GetProperty("call_id").GetString()!)));
        var dispatch = rig.Gateway.Checks[1].Json;
        Assert.Equal(8, dispatch.GetProperty("arguments").GetProperty("page").GetInt32());
        Assert.Equal("Reviewed content", dispatch.GetProperty("arguments").GetProperty("text").GetString());
        Assert.Equal(Gateway.Stamp('a'), dispatch.GetProperty("prepared_profile").GetString());
        Assert.Equal(Gateway.Stamp('b'), dispatch.GetProperty("prepared_arguments").GetString());
        Assert.Equal(Gateway.Stamp('c'), dispatch.GetProperty("prepared_decision").GetString());
        Assert.Equal(rig.Session.Id, rig.Gateway.Checks[0].Header("X-Faden-Conversation-Id"));
    }

    [Fact]
    public async Task The_schema_hash_sent_is_the_hash_of_the_canonical_parameter_schema()
    {
        using var rig = await Fixture();

        await rig.Session.SendAsync("write");

        var sent = rig.Gateway.Checks[0].Json.GetProperty("schema_sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", sent);
        var tool = rig.Agent.Tools.Single(t => t.Name == "add_comment");
        Assert.Equal(sent, ContentHash.BytesSha256(System.Text.Encoding.UTF8.GetBytes(Canonical(tool.Function.JsonSchema))));
    }

    /// <summary>The same canonical JSON the server hashes (sorted keys, compact), written independently of the SDK's.</summary>
    private static string Canonical(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => System.Text.Json.JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        System.Text.Json.JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        _ => e.GetRawText(),
    };

    [Fact]
    public async Task An_agent_that_requires_the_check_fails_to_start_against_a_server_without_it()
    {
        var rig = new Rig { Gateway = new Gateway(), Tools = new PageTools() };
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools).RequireHostToolPolicyChecks().Build();
        using var disposer = rig;

        var error = await Assert.ThrowsAsync<FadenstackException>(() => rig.Agent.StartSessionAsync());

        Assert.Contains("does not advertise support", error.Message);
        Assert.Empty(rig.Gateway.ChatRequests);
        Assert.False(rig.Agent.Tools.Any());
    }

    [Fact]
    public async Task A_central_deny_and_a_local_decline_leave_no_native_effect()
    {
        foreach (var central in new[] { true, false })
        {
            using var rig = await Fixture(answer: central);
            if (central)
            {
                rig.Gateway.Check = new HostDecision("deny", "write", "ask", false, "No");
            }

            await rig.Session.SendAsync("write");

            Assert.False(Commented(rig));
            Assert.Single(rig.Gateway.Checks);
            Assert.Equal(central ? 0 : 1, rig.Asked.Count);
        }
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("principal")]
    [InlineData("profileStamp")]
    [InlineData("argumentsStamp")]
    [InlineData("decisionStamp")]
    public async Task Drift_after_the_approval_stops_the_call(string field)
    {
        using var rig = await Fixture();
        rig.Gateway.OnCheck = request => Phase(request) == "prepare" ? null : (field switch
        {
            "deny" => new HostDecision("deny", "write", "ask", false, "No"),
            "principal" => Reviewed with { Principal = OtherUser },
            "profileStamp" => Reviewed with { Profile = 'd' },
            "argumentsStamp" => Reviewed with { Arguments = 'd' },
            _ => Reviewed with { DecisionStamp = 'd' },
        }).ToWire();

        await rig.Session.SendAsync("write");

        Assert.False(Commented(rig));
        Assert.Equal(2, rig.Gateway.Checks.Count);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("dispatch")]
    public async Task A_failed_check_fails_closed_without_another_send(string phase)
    {
        using var rig = await Fixture();
        rig.Gateway.OnCheck = request => Phase(request) == phase
            ? FakeServer.Respond(HttpStatusCode.BadRequest, new { error = new { message = "Policy timeout", type = "invalid_request_error", code = "invalid_host_command" } })
            : null;

        await rig.Session.SendAsync("write");

        Assert.False(Commented(rig));
        Assert.Equal(phase == "prepare" ? 1 : 2, rig.Gateway.Checks.Count);
        Assert.Contains(rig.Session.Transcript, e => e.Type == EntryTypes.ToolComplete && e.Data.GetProperty("outcome").GetString() == "error");
    }

    [Fact]
    public async Task Arguments_transformed_by_a_local_stage_are_not_disclosed_to_the_dispatch_endpoint()
    {
        using var rig = await Fixture(configure: b => b.AddToolStage(StageOrder.Pii, new TextStage("Local unmasked value")));

        await rig.Session.SendAsync("write");

        Assert.False(Commented(rig));
        Assert.Single(rig.Gateway.Checks);
        Assert.DoesNotContain("Local unmasked value", string.Concat(rig.Gateway.Checks.Select(c => c.Body)));
    }

    [Theory]
    [InlineData("limits")]
    [InlineData("version")]
    [InlineData("feature")]
    [InlineData("mode")]
    public async Task A_change_while_the_approval_is_open_stops_the_call(string change)
    {
        var rig = new Rig { Gateway = Model(), Tools = new PageTools() };
        rig.Gateway.Approvals = Open;
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools).RequireHostToolPolicyChecks().UseApprovals((_, _) =>
        {
            switch (change)
            {
                case "limits": rig.Gateway.Approvals = Closed; break;
                case "version": rig.Gateway.Version = 2; break;
                case "feature": rig.Gateway.Features = """["agents"]"""; break;
                default: rig.Session.ToolMode = ToolMode.ReadOnly; break;
            }
            return Task.FromResult(ApprovalDecision.Approve());
        }).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync();
        if (change == "mode")
        {
            rig.Gateway.OnCheck = request => Phase(request) == "dispatch" ? new HostDecision("deny", "write", "ask", false, "No").ToWire() : null;
        }

        await rig.Session.SendAsync("write");

        Assert.False(Commented(rig));
        if (change == "mode")
        {
            Assert.Equal("read_only", rig.Gateway.Checks[1].Json.GetProperty("tool_mode").GetString());
        }
    }

    [Fact]
    public async Task Once_the_server_has_offered_the_check_losing_it_fails_closed_even_when_the_app_did_not_require_it()
    {
        using var rig = await Fixture(required: false);
        Assert.True(rig.Agent.HostToolPolicyChecks);
        rig.Gateway.Features = """["agents"]""";

        await rig.Session.SendAsync("write");

        Assert.True(rig.Agent.HostToolPolicyChecks);
        Assert.False(Commented(rig));
        Assert.Empty(rig.Asked);
        var failed = Assert.Single(rig.Session.Transcript, e => e.Type == EntryTypes.ToolComplete);
        Assert.Equal("error", failed.Data.GetProperty("outcome").GetString());
        Assert.Contains("does not advertise support", failed.Data.GetProperty("result").GetString());
    }

    [Fact]
    public async Task The_individual_floor_holds_against_a_stored_approval_and_an_overbroad_handler_answer()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        var rig = new Rig { Gateway = Model(), Tools = new PageTools() };
        rig.Gateway.Approvals = Open;
        rig.Gateway.Check = Reviewed with { RequiredByRule = true };
        rig.Agent = rig.Gateway.Builder(approvals: store).AddTools(rig.Tools).RequireHostToolPolicyChecks()
            .UseApprovals((request, _) => { rig.Asked.Add(request); return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();
        using var disposer = rig;
        rig.Session = await rig.Agent.StartSessionAsync();

        await rig.Session.SendAsync("write");

        Assert.Single(rig.Asked);
        Assert.True(rig.Asked[0].RequiredByRule);
        Assert.Equal("Review this call", rig.Asked[0].Reason);
        Assert.Empty(rig.Session.ApprovedForSession);
        Assert.Equal(new[] { "add_comment 8 Reviewed content" }, rig.Tools.Ran);
        Assert.Equal(2, rig.Gateway.Checks.Count);
    }

    [Fact]
    public async Task Stop_aborts_the_pending_approval_without_reaching_dispatch()
    {
        using var stop = new CancellationTokenSource();
        using var rig = await Fixture(approvals: b => b.UseApprovals((_, _) => { stop.Cancel(); return Task.FromResult(ApprovalDecision.Approve()); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Session.SendAsync("write", stop.Token));

        Assert.False(Commented(rig));
        Assert.Single(rig.Gateway.Checks);
    }

    [Fact]
    public async Task A_central_class_and_level_make_the_call_stricter_than_the_tool_declared()
    {
        // The tool is declared write and the session runs in Auto, which would run it; the server holds it destructive.
        using var rig = await Fixture(configure: null);
        rig.Gateway.Check = new HostDecision("run", "destructive", "auto", false, "Held destructive");
        rig.Session.ToolMode = ToolMode.Auto;

        await rig.Session.SendAsync("write");

        var asked = Assert.Single(rig.Asked);
        Assert.Equal(ToolClass.Destructive, asked.ToolClass);
        Assert.True(Commented(rig));
    }

    [Fact]
    public async Task A_host_call_of_a_tool_the_sdk_runs_through_a_stage_still_reaches_the_tool_with_the_reviewed_arguments_only()
    {
        using var rig = await Fixture(configure: b => b.AddToolStage(StageOrder.Custom, new Passing()));

        await rig.Session.SendAsync("write");

        Assert.Equal(new[] { "add_comment 8 Reviewed content" }, rig.Tools.Ran);
    }

    private sealed class Passing : IToolStage
    {
        public Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken) =>
            next(invocation, cancellationToken);
    }

    private sealed class TextStage : IToolStage
    {
        private readonly string _text;

        public TextStage(string text) => _text = text;

        public Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            invocation.Arguments["text"] = _text;
            return next(invocation, cancellationToken);
        }
    }
}

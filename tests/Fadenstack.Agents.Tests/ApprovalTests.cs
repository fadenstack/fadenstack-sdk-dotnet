using System.Diagnostics;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Who decides a call that needs approval, in order: an admin's rule, bypass mode, a destructive or unclassified
/// tool, "Allow always", "Allow for this chat", the user.
/// </summary>
public class ApprovalTests
{
    internal static List<JsonElement> Decisions(AgentSession session) =>
        session.Transcript.Where(e => e.Type == EntryTypes.ApprovalDecision).Select(e => e.Data).ToList();

    internal static List<JsonElement> Decided(Gateway gateway) =>
        gateway.Reported.Where(e => e.GetProperty("type").GetString() == "approval.decided").ToList();

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The gateway marks a call of the agent's own tool, then the model calls it.</summary>
    internal static Gateway MarkedCall(Gateway gateway, string callId, string tool, string arguments, object mark) =>
        gateway.Then(Gateway.Custom("faden.approval_required", mark), Gateway.ToolCalls((callId, tool, arguments)), Gateway.Finish("tool_calls"), FakeServer.Done);

    [Theory]
    [InlineData("delete_page", ToolClass.Destructive)]
    [InlineData("stamp", ToolClass.Unknown)]
    public async Task Destructive_and_unclassified_tools_are_never_offered_for_the_chat_or_always_and_such_an_answer_counts_once(string tool, ToolClass toolClass)
    {
        var gateway = new Gateway()
            .Calls(("call-1", tool, """{"page":1}""")).Says("Done.")
            .Calls(("call-2", tool, """{"page":2}""")).Says("Done again.");
        var store = new MemoryApprovalStore();
        var requests = new List<ApprovalRequest>();
        var answers = new Queue<ApprovalDecision>(new[] { ApprovalDecision.ApproveForSession(), ApprovalDecision.ApproveAlways() });
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(answers.Dequeue()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var decided = new List<ApprovalDecided>();

        await foreach (var e in session.StreamAsync("Go."))
        {
            if (e is ApprovalDecided d)
            {
                decided.Add(d);
            }
        }
        await session.SendAsync("Again.");

        Assert.Equal(2, requests.Count); // asked both times
        Assert.All(requests, r => Assert.Equal((toolClass, false, false, false), (r.ToolClass, r.CanAllowForSession, r.CanAllowAlways, r.RequiredByRule)));
        Assert.Empty(session.ApprovedForSession);
        Assert.Empty(await store.ListAsync(Gateway.Slug));
        Assert.All(Decisions(session), d => Assert.Equal(("once", true, false), (Str(d, "scope"), d.GetProperty("asked").GetBoolean(), d.GetProperty("for_session").GetBoolean())));
        Assert.Equal((false, false), (decided.Single().Decision.ForSession, decided.Single().Decision.Always));
        Assert.All(Decided(gateway), e => Assert.Equal("once", Str(e, "scope")));
    }

    [Fact]
    public async Task An_admins_rule_asks_even_for_a_read_tool_and_offers_this_call_only_with_the_admins_message()
    {
        var gateway = MarkedCall(new Gateway(), "call-1", "read_page", """{"page":1}""",
            new { toolCallId = "call-1", toolCallName = "read_page", message = "Pages of project Falcon need a second look.", decisionId = "d-1", toolClass = "read", rule = true })
            .Says("Page 1.");
        var tools = new PageTools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Read page 1.");

        var request = Assert.Single(requests);
        Assert.Equal((true, false, false, ToolClass.Read), (request.RequiredByRule, request.CanAllowForSession, request.CanAllowAlways, request.ToolClass));
        Assert.Equal("Pages of project Falcon need a second look.", request.Reason);
        Assert.Single(tools.Ran);
        Assert.Empty(session.ApprovedForSession); // the handler's "for this chat" counted once
        var entry = Assert.Single(Decisions(session));
        Assert.Equal(("once", true, true), (Str(entry, "scope"), entry.GetProperty("rule").GetBoolean(), entry.GetProperty("asked").GetBoolean()));
    }

    [Fact]
    public async Task An_admins_rule_asks_even_when_the_tool_was_allowed_for_the_chat_and_always()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.");
        MarkedCall(gateway, "call-2", "add_comment", """{"page":2,"text":"b"}""",
            new { toolCallId = "call-2", toolCallName = "add_comment", message = "Comments on page 2 need approval.", rule = true }).Says("Added.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Comment page 1."); // allowed always: not asked
        Assert.Empty(requests);

        await session.SendAsync("Comment page 2.");
        var request = Assert.Single(requests);
        Assert.True(request.RequiredByRule);
        Assert.Equal("Comments on page 2 need approval.", request.Reason);
        Assert.Equal(new (string?, bool)[] { ("always", false), ("once", true) },
            Decisions(session).Select(d => (Str(d, "scope"), d.GetProperty("asked").GetBoolean())));
    }

    [Fact]
    public async Task A_mark_without_rule_takes_the_stricter_class_and_a_mark_from_an_older_gateway_counts_as_a_rule()
    {
        var gateway = new Gateway();
        // The gateway holds add_comment as destructive: asks every time, once only, in Auto too.
        MarkedCall(gateway, "call-1", "add_comment", """{"page":1,"text":"a"}""",
            new { toolCallId = "call-1", toolCallName = "add_comment", message = "The tool mode asks.", toolClass = "destructive", rule = false }).Says("Ok.");
        // No rule field: an older gateway; taken as a rule.
        MarkedCall(gateway, "call-2", "read_page", """{"page":1}""",
            new { toolCallId = "call-2", toolCallName = "read_page", message = "Approval required." }).Says("Ok.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await session.SendAsync("Comment.");
        await session.SendAsync("Read.");

        Assert.Equal(2, requests.Count);
        Assert.Equal((ToolClass.Destructive, false, false), (requests[0].ToolClass, requests[0].RequiredByRule, requests[0].CanAllowForSession));
        Assert.Equal("'add_comment' is destructive; the tool mode is auto.", requests[0].Reason);
        Assert.Equal((ToolClass.Read, true, "Approval required."), (requests[1].ToolClass, requests[1].RequiredByRule, requests[1].Reason));
    }

    [Fact]
    public async Task A_call_the_gateway_refused_by_its_id_does_not_run_and_is_not_asked()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.tool_denied", new { toolCallId = "call-1", toolCallName = "read_page", message = "Not here." }),
                Gateway.ToolCalls(("call-1", "read_page", """{"page":1}""")), Gateway.Finish("tool_calls"), FakeServer.Done)
            .Says("OK.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var result = await (await agent.StartSessionAsync()).SendAsync("Read page 1.");
        Assert.Empty(tools.Ran);
        Assert.Equal(0, asked);
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
    }

    [Fact]
    public async Task Allow_always_is_stored_and_lets_the_tool_through_in_a_new_session_without_asking()
    {
        var store = new MemoryApprovalStore();
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("Added again.");
        var tools = new PageTools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder(approvals: store).AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();

        var first = await agent.StartSessionAsync();
        await first.SendAsync("Comment page 1.");
        var request = Assert.Single(requests);
        Assert.Equal((true, true), (request.CanAllowForSession, request.CanAllowAlways));
        var kept = Assert.Single(await store.ListAsync(Gateway.Slug));
        Assert.Equal(("add_comment", ToolClass.Write), (kept.Tool, kept.ToolClass));

        var second = await agent.StartSessionAsync();
        var events = new List<AgentEvent>();
        await foreach (var e in second.StreamAsync("Comment page 2."))
        {
            events.Add(e);
        }
        Assert.Single(requests);
        Assert.Equal(2, tools.Ran.Count);
        Assert.DoesNotContain(events, e => e is ApprovalRequested or ApprovalDecided); // let through: no card
        var entry = Assert.Single(Decisions(second));
        Assert.Equal(("always", false, 0L, false), (Str(entry, "scope"), entry.GetProperty("asked").GetBoolean(),
            entry.GetProperty("wait_ms").GetInt64(), entry.GetProperty("for_session").GetBoolean()));
        Assert.Equal(new[] { "always", "always" }, Decided(gateway).Select(e => Str(e, "scope")));
        Assert.False(Decided(gateway)[1].TryGetProperty("wait_ms", out _)); // nobody was asked
    }

    [Fact]
    public async Task Allow_always_no_longer_holds_when_the_tool_became_destructive()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        var gateway = new Gateway(contract: """[{"name":"add_comment","risk":"destructive"}]""")
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        await (await agent.StartSessionAsync()).SendAsync("Comment page 1.");
        Assert.Equal(ToolClass.Destructive, Assert.Single(requests).ToolClass);
    }

    [Fact]
    public async Task Reset_forgets_what_was_allowed_always_for_one_tool_or_all_and_what_this_chat_allowed()
    {
        var store = new MemoryApprovalStore();
        var gateway = new Gateway();
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools()).Build();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        await store.AddAsync(Gateway.Slug, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));
        await store.AddAsync("other-agent", new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));

        await agent.ResetApprovalsAsync("add_comment");
        Assert.Equal(new[] { "read_page" }, (await store.ListAsync(Gateway.Slug)).Select(a => a.Tool));
        await agent.ResetApprovalsAsync();
        Assert.Empty(await store.ListAsync(Gateway.Slug));
        Assert.Single(await store.ListAsync("other-agent")); // another agent's stay

        var chat = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("Added.");
        var asked = 0;
        using var chatAgent = chat.Builder().AddTools(new PageTools())
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();
        var session = await chatAgent.StartSessionAsync();
        await session.SendAsync("Comment page 1.");
        session.ResetApprovals("read_page");
        Assert.Contains("add_comment", session.ApprovedForSession);
        session.ResetApprovals();
        Assert.Empty(session.ApprovedForSession);
        await session.SendAsync("Comment page 2.");
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task The_first_that_applies_decides_rule_destructive_always_chat_then_the_user()
    {
        var store = new MemoryApprovalStore();
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("1.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("2.")
            .Calls(("call-3", "add_comment", """{"page":3,"text":"c"}""")).Says("3.");
        MarkedCall(gateway, "call-4", "add_comment", """{"page":4,"text":"d"}""",
            new { toolCallId = "call-4", toolCallName = "add_comment", message = "Rule.", rule = true }).Says("4.");
        gateway.Calls(("call-5", "delete_page", """{"page":5}""")).Says("5.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("1"); // asked: allowed for this chat
        await session.SendAsync("2"); // this chat
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow));
        await session.SendAsync("3"); // always comes before this chat
        await session.SendAsync("4"); // a rule asks, over both
        await session.SendAsync("5"); // destructive asks, once only

        Assert.Equal(new[] { "call-1", "call-4", "call-5" }, requests.Select(r => r.CallId));
        Assert.Equal(new (string?, bool)[] { ("chat", true), ("chat", false), ("always", false), ("once", true), ("once", true) },
            Decisions(session).Select(d => (Str(d, "scope"), d.GetProperty("asked").GetBoolean())));
        Assert.Equal(new[] { "chat", "chat", "always", "once", "once" }, Decided(gateway).Select(e => Str(e, "scope")));
    }

    [Fact]
    public async Task A_gateway_tool_without_a_rule_may_be_allowed_for_the_chat_and_is_then_answered_without_asking()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.approval_required", new { approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "The tool mode asks.", timeoutS = 300, toolClass = "write", rule = false }),
                Gateway.Text("Commented."), Gateway.Finish("stop"), FakeServer.Done)
            .Then(Gateway.Custom("faden.approval_required", new { approvalId = "ap-2", toolCallId = "gw-2", toolCallName = "mcp.github.comment", message = "The tool mode asks.", timeoutS = 300, toolClass = "write", rule = false }),
                Gateway.Text("Commented again."), Gateway.Finish("stop"), FakeServer.Done);
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-2", new { ok = true });
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder()
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Comment.");
        await session.SendAsync("Comment again.");

        var request = Assert.Single(requests);
        Assert.Equal((ToolClass.Write, true, true, false), (request.ToolClass, request.CanAllowForSession, request.CanAllowAlways, request.RequiredByRule));
        Assert.True(gateway.Server.Requests.Single(r => r.Path.EndsWith("/approvals/ap-2")).Json.GetProperty("approved").GetBoolean());
        Assert.Equal(new[] { "chat", "chat" }, Decided(gateway).Select(e => Str(e, "scope")));
    }

    [Fact]
    public async Task A_gateway_tool_without_a_rule_field_is_taken_as_a_rule_and_offered_once_only()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.approval_required", new { approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.merge", message = "Merging needs your OK.", timeoutS = 300 }),
                Gateway.Text("Merged."), Gateway.Finish("stop"), FakeServer.Done);
        ApprovalRequest? asked = null;
        using var agent = gateway.Builder()
            .UseApprovals((r, _) => { asked = r; return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Merge it.");
        Assert.Equal((ToolClass.Unknown, true, false, false), (asked!.ToolClass, asked.RequiredByRule, asked.CanAllowForSession, asked.CanAllowAlways));
        Assert.Equal("once", Str(Assert.Single(Decided(gateway)), "scope"));
    }
}

/// <summary>Bypass mode: what it lets through, what it still asks, and its warning. One class: the warning is global.</summary>
public class PermissionModeTests
{
    private sealed class Captured : TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(string? message)
        {
            lock (_text)
            {
                _text.Append(message);
            }
        }

        public override void WriteLine(string? message) => Write(message + "\n");
    }

    [Fact]
    public async Task Bypass_approves_a_destructive_call_without_asking_and_records_it()
    {
        var gateway = new Gateway().Calls(("call-1", "delete_page", """{"page":1}""")).Says("Deleted.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools).UsePermissionMode(PermissionMode.Bypass)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        Assert.Equal(PermissionMode.Bypass, agent.PermissionMode);
        var session = await agent.StartSessionAsync();
        var events = new List<AgentEvent>();

        await foreach (var e in session.StreamAsync("Delete page 1."))
        {
            events.Add(e);
        }

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "delete_page 1" }, tools.Ran);
        Assert.DoesNotContain(events, e => e is ApprovalRequested or ApprovalDecided);
        var entry = Assert.Single(ApprovalTests.Decisions(session));
        Assert.Equal(("bypass", false, true), (ApprovalTests.Str(entry, "scope"), entry.GetProperty("asked").GetBoolean(), entry.GetProperty("approved").GetBoolean()));
        var reported = Assert.Single(ApprovalTests.Decided(gateway));
        Assert.Equal(("approved", "bypass"), (ApprovalTests.Str(reported, "decision"), ApprovalTests.Str(reported, "scope")));
    }

    [Fact]
    public async Task Bypass_still_asks_where_an_admins_rule_does()
    {
        var gateway = ApprovalTests.MarkedCall(new Gateway(), "call-1", "add_comment", """{"page":1,"text":"a"}""",
            new { toolCallId = "call-1", toolCallName = "add_comment", message = "Comments need approval.", rule = true }).Says("Ok.");
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(new PageTools()).UsePermissionMode(PermissionMode.Bypass)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var result = await (await agent.StartSessionAsync()).SendAsync("Comment.");
        Assert.True(Assert.Single(requests).RequiredByRule);
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
    }

    [Fact]
    public async Task Bypass_never_widens_the_tool_mode()
    {
        var gateway = new Gateway().Calls(("call-1", "delete_page", """{"page":1}""")).Says("No.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools).UsePermissionMode(PermissionMode.Bypass).Build();
        var result = await (await agent.StartSessionAsync(ToolMode.ReadOnly)).SendAsync("Delete page 1.");
        Assert.Empty(tools.Ran);
        Assert.Equal("mode", result.Notice!.Category);
    }

    [Fact]
    public async Task An_agent_in_bypass_warns_once_and_one_that_asks_does_not()
    {
        var captured = new Captured();
        Trace.Listeners.Add(captured);
        try
        {
            using var asks = new Gateway().Builder().Build();
            await asks.StartAsync();
            Assert.DoesNotContain("bypass mode", captured.Text);

            using var bypass = new Gateway().Builder().UsePermissionMode(PermissionMode.Bypass).Build();
            await bypass.StartAsync();
            await bypass.StartAsync();
            await bypass.StartSessionAsync();
            var text = captured.Text;
            Assert.Equal(1, text.Split(new[] { "runs in bypass mode" }, StringSplitOptions.None).Length - 1);
            Assert.Contains("The agent 'docs-assistant' runs in bypass mode: tool calls that would ask are approved without asking, destructive ones too; " +
                "an admin's \"Ask first\" rule still asks. Use it only for development and trusted automation.", text);
        }
        finally
        {
            Trace.Listeners.Remove(captured);
        }
    }
}

/// <summary>"Allow always" on disk: one protected file per agent, never the user's data folder in tests.</summary>
public class ApprovalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-approvals-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task Approvals_are_kept_per_agent_protected_replaced_by_tool_and_removed()
    {
        var store = new FileApprovalStore(_directory, new XorProtector());
        var at = DateTimeOffset.Parse("2026-10-11T08:00:00Z");
        await store.AddAsync("a", new StoredApproval("add_comment", ToolClass.Write, at));
        await store.AddAsync("a", new StoredApproval("add_comment", ToolClass.Read, at)); // replaces
        await store.AddAsync("a", new StoredApproval("read_page", ToolClass.Read, at));
        await store.AddAsync("b", new StoredApproval("stamp", ToolClass.Write, at));

        var again = new FileApprovalStore(_directory, new XorProtector());
        Assert.Equal(new[] { ("add_comment", ToolClass.Read, at), ("read_page", ToolClass.Read, at) },
            (await again.ListAsync("a")).Select(a => (a.Tool, a.ToolClass, a.ApprovedAt)));
        Assert.DoesNotContain("add_comment", File.ReadAllText(Path.Combine(_directory, "a.json")));

        await again.RemoveAsync("a", "read_page");
        Assert.Equal(new[] { "add_comment" }, (await again.ListAsync("a")).Select(a => a.Tool));
        await again.RemoveAsync("a");
        Assert.Empty(await again.ListAsync("a"));
        Assert.Single(await again.ListAsync("b"));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_approves_nothing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "a.json"), "not json");
        Assert.Empty(await new FileApprovalStore(_directory, new XorProtector()).ListAsync("a"));
    }

    private sealed class XorProtector : Fadenstack.Auth.ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The hook API (sdk/51 F07): order, what a hook sees, the rule that keeps the floor (a hook may ask or deny, never approve, never
/// change a class), notes, and a hook that throws.
/// </summary>
public class HookTests
{
    private const string Sentinel = "SENTINEL-HOOK-41";

    /// <summary>A hook that records what it sees and answers as it is told.</summary>
    private sealed class Probe : AgentHooks
    {
        private readonly string _name;
        private readonly List<string> _log;

        public Probe(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public Func<ToolCallContext, ToolHookVerdict>? Verdict { get; set; }

        public List<ToolCallContext> Before { get; } = new();

        public List<ToolCallResult> After { get; } = new();

        public List<ModelCallContext> ModelBefore { get; } = new();

        public List<ModelCallResult> ModelAfter { get; } = new();

        public override Task BeforeModelCallAsync(ModelCallContext context, CancellationToken cancellationToken)
        {
            _log.Add($"{_name}:before-model");
            ModelBefore.Add(context);
            return Task.CompletedTask;
        }

        public override Task AfterModelCallAsync(ModelCallResult result, CancellationToken cancellationToken)
        {
            _log.Add($"{_name}:after-model");
            ModelAfter.Add(result);
            return Task.CompletedTask;
        }

        public override Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken)
        {
            _log.Add($"{_name}:before-tool");
            Before.Add(context);
            return Task.FromResult(Verdict?.Invoke(context) ?? ToolHookVerdict.Continue);
        }

        public override Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken)
        {
            _log.Add($"{_name}:after-tool");
            After.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class Throwing : AgentHooks
    {
        public override Task BeforeModelCallAsync(ModelCallContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"model {Sentinel}");

        public override Task AfterModelCallAsync(ModelCallResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"after model {Sentinel}");

        public override Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken) =>
            throw new ArgumentException($"before tool {Sentinel}");

        public override Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException($"after tool {Sentinel}");
    }

    private static Gateway Call(string tool, string arguments = """{"page":1}""") => new Gateway().Calls(("call-1", tool, arguments)).Says("Done.");

    private static async Task<(AgentResult Result, ChatSession Session, List<ApprovalRequest> Cards, PageTools Tools)> RunAsync(
        Gateway gateway, ToolMode mode, bool approve, params (IAgentHooks Hooks, int Order)[] hooks)
    {
        var tools = new PageTools();
        var cards = new List<ApprovalRequest>();
        var builder = gateway.Builder().AddTools(tools).UseApprovals((r, _) =>
        {
            cards.Add(r);
            return Task.FromResult(approve ? ApprovalDecision.Approve() : ApprovalDecision.Deny("no"));
        });
        foreach (var (hook, order) in hooks)
        {
            builder.AddHooks(hook, order);
        }
        using var agent = builder.Build();
        var session = await agent.StartSessionAsync(mode);
        var result = await session.SendAsync("Go.");
        return (result, session, cards, tools);
    }

    [Fact]
    public async Task Before_hooks_run_by_order_then_registration_and_after_hooks_in_the_reverse()
    {
        var log = new List<string>();
        var (c, a, b, d) = (new Probe("c", log), new Probe("a", log), new Probe("b", log), new Probe("d", log));

        // c has order 5; a, b and d have order 1 and were added in that order, d last, after c.
        await RunAsync(Call("read_page"), ToolMode.Auto, true, (c, 5), (a, 1), (b, 1), (d, -3));

        var tool = log.Where(l => l.EndsWith("tool")).ToList();
        Assert.Equal(new[] { "d:before-tool", "a:before-tool", "b:before-tool", "c:before-tool", "c:after-tool", "b:after-tool", "a:after-tool", "d:after-tool" }, tool);
        var model = log.Where(l => l.EndsWith("model")).Take(8).ToList();
        Assert.Equal(new[] { "d:before-model", "a:before-model", "b:before-model", "c:before-model", "c:after-model", "b:after-model", "a:after-model", "d:after-model" }, model);
    }

    [Fact]
    public async Task A_tool_hook_sees_the_call_and_the_policys_decision_and_the_after_hook_the_outcome_and_the_host_tools_result()
    {
        var log = new List<string>();
        var probe = new Probe("p", log);

        var (result, _, cards, tools) = await RunAsync(Call("read_page"), ToolMode.Auto, true, (probe, 0));

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Empty(cards);
        var before = Assert.Single(probe.Before);
        Assert.Equal(("read_page", "host", ToolClass.Read, "call-1", ToolPolicyDecision.Runs), (before.ToolName, before.Source, before.Class, before.CallId, before.Decision));
        Assert.Equal(Gateway.Slug, before.AgentName);
        Assert.Equal(result.TurnId, before.TurnId);
        Assert.NotEmpty(before.SessionId);
        Assert.Equal(1, Convert.ToInt32(before.Arguments["page"]!.ToString()));
        var after = Assert.Single(probe.After);
        Assert.Equal((HookOutcome.Ok, ToolPolicyDecision.Runs, "page 1: 3 terminals"), (after.Outcome, after.Decision, after.ResultText));
        Assert.True(after.Duration >= TimeSpan.Zero);
        Assert.Equal(new[] { "read_page 1" }, tools.Ran);
    }

    [Fact]
    public async Task A_call_the_policy_asks_about_is_shown_as_asks_and_a_declined_call_ends_denied()
    {
        var probe = new Probe("p", new List<string>());

        var (result, _, cards, tools) = await RunAsync(Call("add_comment", """{"page":1,"text":"a"}"""), ToolMode.Ask, false, (probe, 0));

        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        Assert.Single(cards);
        Assert.Equal(ToolPolicyDecision.Asks, Assert.Single(probe.Before).Decision);
        Assert.Equal(HookOutcome.Denied, Assert.Single(probe.After).Outcome);
        Assert.Empty(tools.Ran);
    }

    [Theory]
    [InlineData("delete_page", ToolClass.Destructive)]
    [InlineData("stamp", ToolClass.Unknown)]
    public async Task A_hook_that_goes_on_cannot_get_a_destructive_or_unclassified_call_run_without_the_card(string tool, ToolClass toolClass)
    {
        var probe = new Probe("p", new List<string>()) { Verdict = _ => ToolHookVerdict.Continue };

        var (_, _, cards, tools) = await RunAsync(Call(tool, tool == "stamp" ? "{}" : """{"page":1}"""), ToolMode.Auto, false, (probe, 0));

        Assert.Equal(toolClass, Assert.Single(cards).ToolClass); // the card was shown
        Assert.Empty(tools.Ran); // and the person's "no" stopped it
        Assert.Equal(ToolPolicyDecision.Asks, Assert.Single(probe.Before).Decision);
    }

    [Fact]
    public async Task A_hook_that_tries_every_answer_still_leaves_a_refused_call_refused()
    {
        foreach (var verdict in new[] { ToolHookVerdict.Continue, ToolHookVerdict.Ask("please"), ToolHookVerdict.Deny("no") })
        {
            // Read-only mode: the policy refuses a destructive tool; the model calls it anyway.
            var probe = new Probe("p", new List<string>()) { Verdict = _ => verdict };

            var (result, _, cards, tools) = await RunAsync(Call("delete_page"), ToolMode.ReadOnly, true, (probe, 0));

            Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
            Assert.Empty(cards);
            Assert.Empty(tools.Ran);
            Assert.Equal(ToolPolicyDecision.Refused, Assert.Single(probe.Before).Decision);
            Assert.Equal(HookOutcome.Denied, Assert.Single(probe.After).Outcome);
        }
    }

    [Fact]
    public void The_api_has_no_answer_that_approves_and_nothing_a_hook_is_given_can_be_set()
    {
        // The answers a hook can give: go on, ask, deny. No "approve", "allow", "run" or class setter anywhere.
        var answers = typeof(ToolHookVerdict).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is PropertyInfo or MethodInfo { IsSpecialName: false })
            .Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Ask", "Continue", "Deny" }, answers);
        Assert.Empty(typeof(ToolHookVerdict).GetConstructors());
        foreach (var type in new[] { typeof(ToolCallContext), typeof(ToolCallResult), typeof(ModelCallContext), typeof(ModelCallResult), typeof(ToolHookVerdict) })
        {
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => Assert.True(p.SetMethod is null, $"{type.Name}.{p.Name} has a setter"));
            Assert.Empty(type.GetConstructors()); // none is public: only the SDK makes them
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                m => Regex.IsMatch(m.Name, "Approve|Allow|SetClass|Class$") && !m.IsSpecialName);
        }
        Assert.DoesNotContain(Enum.GetNames(typeof(ToolPolicyDecision)), n => Regex.IsMatch(n, "Approve|Allow|Granted"));
    }

    [Fact]
    public async Task A_hook_can_make_a_call_the_policy_would_run_unasked_show_the_card_even_when_the_user_allowed_it_always()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow)); // "Allow always" earlier
        var gateway = Call("add_comment", """{"page":1,"text":"a"}""");
        var probe = new Probe("p", new List<string>()) { Verdict = _ => ToolHookVerdict.Ask("The app wants to see this one.") };
        var cards = new List<ApprovalRequest>();
        var tools = new PageTools();
        using var agent = gateway.Builder(approvals: store).AddTools(tools).AddHooks(probe)
            .UseApprovals((r, _) => { cards.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("Go.");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Equal(ToolPolicyDecision.Runs, Assert.Single(probe.Before).Decision); // the policy ran it unasked... then the hook asked
        var card = Assert.Single(cards);
        Assert.Equal("The app wants to see this one.", card.Reason);
        Assert.False(card.CanAllowAlways);
        Assert.Equal(new[] { "add_comment 1 a" }, tools.Ran);
    }

    [Fact]
    public async Task A_hook_can_deny_a_call_the_reason_is_cut_to_500_characters_and_the_model_hears_a_declined_call()
    {
        var gateway = Call("read_page");
        var probe = new Probe("p", new List<string>()) { Verdict = _ => ToolHookVerdict.Deny(new string('x', 600)) };

        var (result, _, cards, tools) = await RunAsync(gateway, ToolMode.Auto, true, (probe, 0));

        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        Assert.Empty(cards);
        Assert.Empty(tools.Ran);
        var second = gateway.ChatRequests[1].Body;
        Assert.Matches("declined by the app \\(x{500}\\)", second);
        Assert.DoesNotMatch("x{501}", second);
        Assert.Equal(HookOutcome.Denied, Assert.Single(probe.After).Outcome);
    }

    [Fact]
    public async Task A_deny_stops_the_before_hooks_that_come_after_it_and_a_later_ask_does_not_undo_it()
    {
        var log = new List<string>();
        var (first, second) = (new Probe("a", log) { Verdict = _ => ToolHookVerdict.Deny("stop") }, new Probe("b", log) { Verdict = _ => ToolHookVerdict.Ask() });

        var (_, _, cards, tools) = await RunAsync(Call("read_page"), ToolMode.Auto, true, (first, 0), (second, 1));

        Assert.Empty(cards);
        Assert.Empty(tools.Ran);
        Assert.DoesNotContain("b:before-tool", log);
        Assert.Contains("b:after-tool", log); // it is still told how the call ended
    }

    [Fact]
    public async Task A_hooks_notes_are_bounded_kept_in_the_calls_transcript_entry_and_never_sent_to_the_server()
    {
        var gateway = Call("read_page");
        var verdict = ToolHookVerdict.Continue;
        for (var i = 0; i < 20; i++)
        {
            verdict = verdict.WithNote(i == 0 ? new string('k', 100) : $"key{i}", new string('v', 300));
        }
        var probe = new Probe("p", new List<string>()) { Verdict = _ => verdict };

        var (_, session, _, _) = await RunAsync(gateway, ToolMode.Auto, true, (probe, 0));

        var entry = Assert.Single(session.Transcript, e => e.Type == EntryTypes.ToolComplete);
        var notes = entry.Data.GetProperty("notes");
        var all = notes.EnumerateObject().ToList();
        Assert.Equal(ToolHookVerdict.MaxNotes, all.Count);
        Assert.Contains(all, n => n.Name == new string('k', 64));
        Assert.All(all, n => Assert.Equal(new string('v', 200), n.Value.GetString()));
        Assert.DoesNotContain(gateway.Server.Requests, r => r.Body.Contains("vvvvvvvv") || r.Body.Contains("key1"));
        Assert.DoesNotContain(gateway.Reported, e => e.ToString().Contains("vvvvvvvv"));
    }

    [Fact]
    public async Task A_call_without_notes_writes_no_notes_field()
    {
        var (_, session, _, _) = await RunAsync(Call("read_page"), ToolMode.Auto, true, (new Probe("p", new List<string>()), 0));

        Assert.False(Assert.Single(session.Transcript, e => e.Type == EntryTypes.ToolComplete).Data.TryGetProperty("notes", out var n) && n.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_hook_that_throws_never_breaks_the_run_it_is_logged_without_the_message_and_counts_as_go_on()
    {
        var logs = new CapturingLoggerFactory();
        var log = new List<string>();
        var later = new Probe("later", log);
        var gateway = Call("delete_page");
        var tools = new PageTools();
        var cards = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools).UseLogging(logs).AddHooks(new Throwing(), -1).AddHooks(later, 0)
            .UseApprovals((r, _) => { cards.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("Go.");

        // The run finished; the destructive call still asked, as the policy decided, and ran once approved.
        Assert.Equal((ChatOutcome.Completed, "Done."), (result.Outcome, result.Text));
        Assert.Single(cards);
        Assert.Equal(new[] { "delete_page 1" }, tools.Ran);
        // The hooks after the throwing one were still called, before and after.
        Assert.Contains("later:before-tool", log);
        Assert.Contains("later:after-tool", log);
        var failures = logs.Entries.Where(e => e.EventId == 2060).ToList();
        Assert.Equal(6, failures.Count); // before and after, model (2 rounds: 4) and tool (2)
        Assert.All(failures, f => Assert.Contains("Throwing", f.Message));
        Assert.Contains(failures, f => f.Message.Contains(nameof(ArgumentException)));
        Assert.Contains(failures, f => f.Message.Contains(nameof(NotSupportedException)));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(Sentinel));
    }

    [Fact]
    public async Task A_model_hook_sees_names_ids_counts_and_outcome_and_no_text_unless_the_app_asks_for_content()
    {
        var plain = new Probe("p", new List<string>());
        var (result, session, _, _) = await RunAsync(Call("read_page"), ToolMode.Auto, true, (plain, 0));

        Assert.Equal(2, plain.ModelBefore.Count); // the call, then the answer
        var first = plain.ModelBefore[0];
        Assert.Equal((Gateway.Slug, session.Id, result.TurnId, 1), (first.AgentName, first.SessionId, first.TurnId, first.Round));
        Assert.True(first.MessageCount >= 1);
        Assert.Equal(4, first.ToolCount); // the page tools
        Assert.Null(first.Messages);
        Assert.Equal(new[] { HookOutcome.Ok, HookOutcome.Ok }, plain.ModelAfter.Select(a => a.Outcome).ToArray());
        Assert.Equal(1, plain.ModelAfter[0].ToolCallCount);
        Assert.All(plain.ModelAfter, a => Assert.Null(a.Text));

        var asked = new Probe("c", new List<string>());
        var gateway = Call("read_page");
        using var agent = gateway.Builder().AddTools(new PageTools()).AddHooks(asked).UseHookContent().Build();
        var s = await agent.StartSessionAsync(ToolMode.Auto);
        await s.SendAsync("Go.");
        Assert.NotNull(asked.ModelBefore[0].Messages);
        Assert.Equal("Done.", asked.ModelAfter[1].Text);
    }

    [Fact]
    public async Task A_failed_model_call_is_reported_to_the_after_hook_as_an_error()
    {
        var probe = new Probe("p", new List<string>());
        var gateway = new Gateway();
        using var agent = gateway.Builder().AddTools(new PageTools()).AddHooks(probe).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        try
        {
            await session.SendAsync("Go.");
        }
        catch (Exception)
        {
            // The model call failed; whether the turn throws is not what this test is about.
        }

        Assert.Equal(HookOutcome.Error, Assert.Single(probe.ModelAfter).Outcome);
    }

    [Fact]
    public async Task A_tool_the_model_made_up_and_an_agent_without_hooks_run_as_before()
    {
        var gateway = Call("read_page");
        using var agent = gateway.Builder().AddTools(new PageTools()).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("Go.");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.False(Assert.Single(session.Transcript, e => e.Type == EntryTypes.ToolComplete).Data.TryGetProperty("notes", out var n) && n.ValueKind != System.Text.Json.JsonValueKind.Null);
    }
}

using System.Net;
using System.Text.Json;
using Faden.Agents.Sessions;
using Faden.Client.Tests;
using Faden.Governance;
using Faden.Models;
using Faden.Tools;
using Xunit;

namespace Faden.Agents.Tests;

/// <summary>The loop: the agent's tools under the session's tool mode, approvals, outcomes (sdk/07, 04 §8).</summary>
public class AgentLoopTests
{
    private static List<string> ToolNames(Recorded request) =>
        request.Json.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList()
            : new List<string>();

    private static List<JsonElement> Messages(Recorded request) => request.Json.GetProperty("messages").EnumerateArray().ToList();

    [Fact]
    public async Task A_read_tool_runs_unasked_and_its_answer_goes_back_to_the_model()
    {
        var gateway = new Gateway().Calls(("call-1", "read_page", """{"page":7}""")).Says("Page 7 has 3 terminals.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("What is on page 7?");

        Assert.Equal((ChatOutcome.Completed, "Page 7 has 3 terminals."), (result.Outcome, result.Text));
        Assert.Equal(new[] { "read_page 7" }, tools.Ran);
        Assert.Equal(0, asked);
        var (first, second) = (gateway.ChatRequests[0], gateway.ChatRequests[1]);
        Assert.Equal("ask", first.Header("X-Faden-Tool-Mode"));
        Assert.Equal(session.Id, first.Header("X-Faden-Conversation-Id"));
        Assert.Equal(result.TurnId, second.Header("X-Faden-Turn-Id"));
        Assert.Equal("ag-ui", first.Header("X-Faden-Events"));
        Assert.Equal(new[] { "read_page", "add_comment", "delete_page", "stamp" }, ToolNames(first));
        var sent = Messages(second);
        Assert.Equal(new[] { "user", "assistant", "tool" }, sent.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("call-1", sent[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("page 7: 3 terminals", sent[2].GetProperty("content").GetString());

        var executed = gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()).Single();
        Assert.Equal(("tool.executed", "read_page", "host", "ok", session.Id),
            (executed.GetProperty("type").GetString(), executed.GetProperty("tool").GetString(), executed.GetProperty("source").GetString(),
             executed.GetProperty("outcome").GetString(), executed.GetProperty("conversation_id").GetString()));
        Assert.DoesNotContain("terminals", gateway.Reports.Single().Body); // names and counts, never content
    }

    [Fact]
    public async Task The_agents_reasoning_setting_goes_with_every_request_of_a_turn()
    {
        var gateway = new Gateway().Calls(("call-1", "read_page", """{"page":7}""")).Says("Page 7.");
        using var agent = gateway.Builder().AddTools(new PageTools()).UseReasoning(Microsoft.Extensions.AI.ReasoningEffort.None).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("What is on page 7?");

        Assert.Equal(2, gateway.ChatRequests.Count);
        Assert.All(gateway.ChatRequests, r => Assert.Equal("none", r.Json.GetProperty("reasoning_effort").GetString()));
    }

    [Fact]
    public async Task A_tools_duration_leaves_out_the_time_the_user_took_to_approve()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Added.");
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals(async (_, ct) => { await Task.Delay(400, ct); return ApprovalDecision.Approve(); }).Build();
        var session = await agent.StartSessionAsync();
        var events = new List<AgentEvent>();

        await foreach (var e in session.StreamAsync("Comment page 2."))
        {
            events.Add(e);
        }

        Assert.True(events.OfType<ToolCallCompleted>().Single().Duration < TimeSpan.FromMilliseconds(300));
        var reported = gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()).ToList();
        Assert.True(reported.Single(e => e.GetProperty("type").GetString() == "tool.executed").GetProperty("duration_ms").GetInt64() < 300);
        Assert.True(reported.Single(e => e.GetProperty("type").GetString() == "approval.decided").GetProperty("wait_ms").GetInt64() >= 400);
        Assert.True(session.Transcript.Single(e => e.Type == EntryTypes.ToolComplete).Data.GetProperty("duration_ms").GetInt64() < 300);
    }

    [Theory]
    [InlineData(ToolMode.Ask, "add_comment", true)]
    [InlineData(ToolMode.Auto, "add_comment", false)]
    [InlineData(ToolMode.Auto, "delete_page", true)]
    [InlineData(ToolMode.Auto, "stamp", true)] // unclassified: never unasked
    public async Task The_mode_decides_what_asks(ToolMode mode, string tool, bool asks)
    {
        var gateway = new Gateway().Calls(("call-1", tool, """{"page":1,"text":"ok"}""")).Says("Done.");
        var tools = new PageTools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(mode);
        var result = await session.SendAsync("Go.");
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Single(tools.Ran);
        Assert.Equal(asks ? 1 : 0, requests.Count);
        if (asks)
        {
            Assert.Equal((tool, "host"), (requests[0].ToolName, requests[0].Source));
            Assert.Equal(1, ((JsonElement)requests[0].Arguments["page"]!).GetInt32());
        }
    }

    [Fact]
    public async Task Without_an_approval_handler_a_write_tool_is_declined_and_the_model_told()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"x"}""")).Says("I could not add it.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("Comment page 1.");

        Assert.Empty(tools.Ran);
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        var told = Messages(gateway.ChatRequests[1]).Last();
        Assert.StartsWith("The user declined to run 'add_comment'", told.GetProperty("content").GetString());
        var decided = gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray())
            .Single(e => e.GetProperty("type").GetString() == "approval.decided");
        Assert.Equal("denied", decided.GetProperty("decision").GetString());
        Assert.Contains(session.Transcript, e => e.Type == EntryTypes.ApprovalDecision);
    }

    [Fact]
    public async Task Allowed_for_this_chat_holds_in_the_session_and_ends_with_it()
    {
        var store = new MemorySessionStore();
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("Added again.")
            .Calls(("call-3", "add_comment", """{"page":3,"text":"c"}""")).Says("Added once more.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder(store).AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.ApproveForSession()); }).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Comment page 1.");
        await session.SendAsync("And page 2.");
        Assert.Equal(1, asked);
        Assert.Contains("add_comment", session.ApprovedForSession);

        var reopened = await agent.OpenSessionAsync(session.Id);
        Assert.Empty(reopened.ApprovedForSession); // never rebuilt from the transcript
        await reopened.SendAsync("And page 3.");
        Assert.Equal(2, asked);
        Assert.Equal(3, tools.Ran.Count);
    }

    [Fact]
    public async Task Read_only_hides_everything_else_and_refuses_it_if_called_anyway()
    {
        var gateway = new Gateway().Calls(("call-1", "delete_page", """{"page":1}""")).Says("I cannot delete pages here.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var session = await agent.StartSessionAsync(ToolMode.ReadOnly);

        var events = new List<AgentEvent>();
        await foreach (var e in session.StreamAsync("Delete page 1."))
        {
            events.Add(e);
        }

        Assert.Equal(new[] { "read_page" }, ToolNames(gateway.ChatRequests[0]));
        Assert.Equal("read_only", gateway.ChatRequests[0].Header("X-Faden-Tool-Mode"));
        Assert.Empty(tools.Ran);
        var result = events.OfType<TurnCompleted>().Single().Result;
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        Assert.Equal("mode", result.Notice!.Category);
        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        Assert.Equal("denied", events.OfType<ToolCallCompleted>().Single().Outcome);
        Assert.IsType<TurnStarted>(events[0]);
        Assert.All(events, e => Assert.Equal(result.TurnId, e.TurnId));
    }

    [Fact]
    public async Task Off_offers_no_tools()
    {
        var gateway = new Gateway().Says("Hello.");
        using var agent = gateway.Builder().AddTools(new PageTools()).Build();
        var session = await agent.StartSessionAsync(ToolMode.Off);
        await session.SendAsync("Hi");
        Assert.False(gateway.ChatRequests[0].Json.TryGetProperty("tools", out _));
        Assert.Equal("off", gateway.ChatRequests[0].Header("X-Faden-Tool-Mode"));
    }

    [Fact]
    public async Task The_agents_contract_drops_other_tools_and_can_only_make_a_class_stricter()
    {
        var gateway = new Gateway(contract: """[{"name":"read_page","risk":"destructive"},{"name":"stamp","risk":"read"},{"name":"add_comment"}]""");
        using var agent = gateway.Builder().AddTools(new PageTools()).Build();
        await agent.StartAsync();
        // An entry without a risk: nobody on the server vouched for the tool, so it is unclassified.
        Assert.Equal(new[] { ("read_page", ToolClass.Destructive), ("add_comment", ToolClass.Unknown), ("stamp", ToolClass.Read) },
            agent.Tools.Select(t => (t.Name, t.Class)));
    }

    [Fact]
    public async Task Without_a_contract_every_host_tool_is_unclassified_and_asks_every_time()
    {
        var gateway = new Gateway(contract: "[]")
            .Calls(("call-1", "read_page", """{"page":1}""")).Says("Page 1.")
            .Calls(("call-2", "read_page", """{"page":2}""")).Says("Page 2.");
        var tools = new PageTools();
        var requests = new List<ApprovalRequest>();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.ApproveAlways()); }).Build();
        await agent.StartAsync();
        Assert.All(agent.Tools, t => Assert.Equal(ToolClass.Unknown, t.Class));
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await session.SendAsync("Read page 1.");
        await session.SendAsync("Read page 2.");

        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal((ToolClass.Unknown, false, false), (r.ToolClass, r.CanAllowForSession, r.CanAllowAlways)));
        Assert.Equal("Nobody has said what this tool does.", requests[0].Reason);
        Assert.Equal(2, tools.Ran.Count);
    }

    [Fact]
    public async Task The_app_tells_the_server_what_it_brings_also_the_tools_its_contract_does_not_list()
    {
        var gateway = new Gateway(contract: """[{"name":"read_page","risk":"read"}]""");
        using var agent = gateway.Builder().AddTools(new PageTools()).DescribeApp(new AppDescription
        {
            Name = "EPLAN add-in",
            Version = "2.1.0",
            Context = [new ManifestContext { Id = "project:pages", Title = "The project's pages" }],
            Ui = new ManifestUi { Greeting = "Ask about your project." },
        }).Build();
        await agent.StartAsync();
        Assert.Equal(new[] { "read_page" }, agent.Tools.Select(t => t.Name)); // the others wait for the server's review

        var sent = await Until(() => gateway.Manifests.FirstOrDefault());
        var manifest = sent.Json;
        Assert.Equal("EPLAN add-in", manifest.GetProperty("app").GetProperty("name").GetString());
        Assert.Equal("2.1.0", manifest.GetProperty("app").GetProperty("version").GetString());
        Assert.Equal("dotnet", manifest.GetProperty("sdk").GetProperty("language").GetString());
        Assert.Equal("project:pages", manifest.GetProperty("context")[0].GetProperty("id").GetString());
        Assert.Equal("Ask about your project.", manifest.GetProperty("ui").GetProperty("greeting").GetString());
        var tools = manifest.GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("class").GetString());
        Assert.Equal("read", tools["read_page"]);
        Assert.Equal("write", tools["add_comment"]);
        Assert.Equal("destructive", tools["delete_page"]);
        var read = manifest.GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "read_page");
        Assert.Equal("Reads a page of the schematic.", read.GetProperty("description").GetString());
        Assert.Equal("object", read.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task No_manifest_when_event_reporting_is_off()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().AddTools(new PageTools()).UseEventReporting(false).Build();
        await agent.StartAsync();
        await Task.Delay(50);
        Assert.Empty(gateway.Manifests);
    }

    private static async Task<T> Until<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 200; i++)
        {
            if (find() is { } found)
            {
                return found;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task A_ceiling_narrows_the_users_pick_and_new_sessions_start_in_ask()
    {
        var gateway = new Gateway(ceiling: "\"read_only\"").Says("Hi.");
        using var agent = gateway.Builder().AddTools(new PageTools()).UseDefaultToolMode(ToolMode.Auto).Build();
        var session = await agent.StartSessionAsync();
        Assert.Equal(ToolMode.Ask, session.ToolMode); // a builder cannot widen the default
        session.ToolMode = ToolMode.Auto;
        Assert.Equal(ToolMode.ReadOnly, session.EffectiveToolMode);
        await session.SendAsync("Hi");
        Assert.Equal("read_only", gateway.ChatRequests[0].Header("X-Faden-Tool-Mode"));
    }

    [Fact]
    public async Task A_required_module_that_is_missing_stops_the_start()
    {
        var gateway = new Gateway(modules: """["pii.local"]""");
        using var agent = gateway.Builder().Build();
        var e = await Assert.ThrowsAsync<FadenException>(() => agent.StartAsync());
        Assert.Contains("pii.local", e.Message);
    }

    [Fact]
    public async Task A_refused_prompt_is_an_outcome_and_never_sent_again()
    {
        var gateway = new Gateway()
            .Then(_ => FakeServer.Respond((HttpStatusCode)422, new
            {
                error = new { type = "governance_error", code = "policy_blocked", message = "Project names stay out." },
                faden = new { governance = new { outcome = "prompt_blocked", stage = "input", category = "confidential", message = "Project names stay out.", decision_id = "d-1" } },
            }))
            .Says("Sure.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();

        var refused = await session.SendAsync("Tell me about Project Falcon.");
        Assert.Equal(ChatOutcome.PromptBlocked, refused.Outcome);
        Assert.Equal(("Project names stay out.", "d-1"), (refused.Notice!.Message, refused.Notice.DecisionId));

        var next = await session.SendAsync("Tell me about wiring.");
        Assert.Equal(ChatOutcome.Completed, next.Outcome);
        var sent = Messages(gateway.ChatRequests[1]);
        Assert.Equal("Tell me about wiring.", sent.Single().GetProperty("content").GetString());
        Assert.Equal(TurnStatus.PromptBlocked, session.Transcript.First(e => e.Type == EntryTypes.TurnEnd).Status);
    }

    [Fact]
    public async Task A_refused_answer_keeps_the_question_and_its_tool_calls_never_run()
    {
        var gateway = new Gateway()
            .Then(Gateway.Text("The secret is"),
                Gateway.Custom("faden.governance", new { outcome = "response_blocked", stage = "output", message = "Stopped." }),
                Gateway.ToolCalls(("call-1", "read_page", """{"page":1}""")),
                Gateway.Finish("content_filter"), FakeServer.Done)
            .Says("Fine.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools).Build();
        var session = await agent.StartSessionAsync();

        var refused = await session.SendAsync("What is the secret?");
        Assert.Equal((ChatOutcome.ResponseBlocked, "The secret is", "Stopped."), (refused.Outcome, refused.Text, refused.Notice!.Message));
        Assert.Empty(tools.Ran);

        await session.SendAsync("Something else.");
        var sent = Messages(gateway.ChatRequests[1]);
        Assert.Equal(new[] { "user" }, sent.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("What is the secret?\n\nSomething else.", sent[0].GetProperty("content").GetString()); // merged
    }

    [Fact]
    public async Task The_gateways_own_round_is_kept_and_sent_back()
    {
        var round = new
        {
            messages = new object[]
            {
                new { role = "assistant", content = "", tool_calls = new[] { new { id = "gw-1", type = "function", function = new { name = "knowledge_search", arguments = """{"query":"terminals"}""" } } } },
                new { role = "tool", tool_call_id = "gw-1", content = "Terminals are on page 7." },
            },
        };
        var gateway = new Gateway()
            .Then(FakeServer.Faden(new { type = "TOOL_CALL_START", toolCallId = "gw-1", toolCallName = "knowledge_search", kind = "knowledge" }),
                FakeServer.Faden(new { type = "TOOL_CALL_END", toolCallId = "gw-1", toolCallName = "knowledge_search", durationMs = 40, error = false }),
                Gateway.Custom("faden.gateway_round", round),
                Gateway.Text("They are on page 7."), Gateway.Finish("stop"), FakeServer.Done)
            .Says("Yes.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        var events = new List<AgentEvent>();
        await foreach (var e in session.StreamAsync("Where are the terminals?"))
        {
            events.Add(e);
        }
        Assert.Equal(("knowledge_search", "gateway"), events.OfType<ToolCallStarted>().Select(e => (e.Name, e.Source)).Single());
        Assert.Equal(TimeSpan.FromMilliseconds(40), events.OfType<ToolCallCompleted>().Single().Duration);

        await session.SendAsync("Sure?");
        var sent = Messages(gateway.ChatRequests[1]);
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "user" }, sent.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("knowledge_search", sent[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Terminals are on page 7.", sent[2].GetProperty("content").GetString());
        Assert.DoesNotContain(gateway.Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()),
            e => e.GetProperty("type").GetString() == "tool.executed"); // the gateway logs its own tools
    }

    [Fact]
    public async Task A_gateway_tool_held_for_approval_is_answered_while_the_gateway_waits()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.approval_required", new { approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.merge", message = "Merging needs your OK.", timeoutS = 300 }),
                Gateway.Text("Merged."), Gateway.Finish("stop"), FakeServer.Done);
        ApprovalRequest? asked = null;
        using var agent = gateway.Builder()
            .UseApprovals((r, _) => { asked = r; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync();
        var result = await session.SendAsync("Merge it.");
        Assert.Equal(("mcp.github.merge", "gateway", "Merging needs your OK."), (asked!.ToolName, asked.Source, asked.Reason));
        var answer = gateway.Server.Requests.Single(r => r.Path.EndsWith("/approvals/ap-1"));
        Assert.True(answer.Json.GetProperty("approved").GetBoolean());
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task A_tool_the_gateway_refused_is_a_notice_and_does_not_run()
    {
        var gateway = new Gateway()
            .Then(Gateway.Custom("faden.tool_denied", new { toolCallId = "call-1", toolCallName = "delete_page", message = "No deleting.", decisionId = "d-9" }),
                Gateway.ToolCalls(("call-1", "delete_page", """{"page":1}""")), Gateway.Finish("tool_calls"), FakeServer.Done)
            .Says("OK.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var result = await (await agent.StartSessionAsync()).SendAsync("Delete page 1.");
        Assert.Empty(tools.Ran);
        Assert.Equal((ChatOutcome.ToolDenied, "No deleting.", "d-9"), (result.Outcome, result.Notice!.Message, result.Notice.DecisionId));
    }

    [Fact]
    public async Task A_rate_limit_is_an_outcome_with_the_time_to_wait()
    {
        var gateway = new Gateway().Then(_ =>
        {
            var r = FakeServer.Respond(HttpStatusCode.TooManyRequests, new { error = new { type = "rate_limit_error", code = "rate_limit_rpm", message = "Slow down." } });
            r.Headers.Add("Retry-After", "12");
            return r;
        });
        using var agent = gateway.Builder().Build();
        var result = await (await agent.StartSessionAsync()).SendAsync("Hi");
        Assert.Equal(ChatOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Notice!.RetryAfter);
    }

    [Fact]
    public async Task A_failing_tool_is_told_to_the_model_and_the_turn_goes_on()
    {
        var gateway = new Gateway().Calls(("call-1", "boom", "{}")).Says("That failed.");
        using var agent = gateway.Builder()
            .AddTool(Microsoft.Extensions.AI.AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("No page open.")), "boom"), ToolClass.Read)
            .Build();
        var result = await (await agent.StartSessionAsync()).SendAsync("Go");
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Equal("Error: No page open.", Messages(gateway.ChatRequests[1]).Last().GetProperty("content").GetString());
    }

    [Fact]
    public async Task Leaving_the_stream_early_cancels_the_turn_and_it_is_not_sent_again()
    {
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"x"}"""))
            .Says("Hello.");
        using var agent = gateway.Builder().AddTools(new PageTools())
            .UseApprovals(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ApprovalDecision.Approve(); }).Build();
        var session = await agent.StartSessionAsync();
        await foreach (var e in session.StreamAsync("Comment."))
        {
            if (e is ApprovalRequested)
            {
                break;
            }
        }
        Assert.Equal(TurnStatus.Cancelled, session.Transcript.Last(e => e.Type == EntryTypes.TurnEnd).Status);
        await session.SendAsync("Hi");
        Assert.Equal("Hi", Messages(gateway.ChatRequests[1]).Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task Tool_stages_run_in_their_order_around_the_tool()
    {
        var log = new List<string>();
        var gateway = new Gateway().Calls(("call-1", "read_page", """{"page":3}""")).Says("ok");
        using var agent = gateway.Builder().AddTools(new PageTools())
            .AddToolStage(StageOrder.Custom, new Stage("custom", log))
            .AddToolStage(StageOrder.Pii, new Stage("pii", log))
            .Build();
        await (await agent.StartSessionAsync()).SendAsync("Go");
        Assert.Equal(new[] { "pii>", "custom>", "<custom", "<pii" }, log);
    }

    private sealed class Stage(string name, List<string> log) : IToolStage
    {
        public async Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            log.Add(name + ">");
            var result = await next(invocation, cancellationToken);
            log.Add("<" + name);
            return result;
        }
    }
}

/// <summary>Context attached to a message goes with it, and stays with it in later turns.</summary>
public class AttachmentTests
{
    [Fact]
    public async Task Attachments_reach_the_model_before_the_question_and_stay_in_the_history()
    {
        var gateway = new Gateway().Says("It sums to 42.").Says("Yes.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("What does this sum to?", [new MessageAttachment
        {
            Title = "Sheet1!A1:A3", Content = "| A |\n|---|\n| 40 |\n| 2 |", Source = "excel", Kind = "range", Reference = "excel:range:Sheet1!A1:A3",
        }]);
        await session.SendAsync("Sure?");

        var first = gateway.ChatRequests[0].Json.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.StartsWith("<attachment title=\"Sheet1!A1:A3\" kind=\"range\" source=\"excel\" reference=\"excel:range:Sheet1!A1:A3\">\n| A |", first);
        Assert.EndsWith("</attachment>\n\nWhat does this sum to?", first);
        var again = gateway.ChatRequests[1].Json.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Equal(first, again);
        var stored = session.Transcript.First(e => e.Type == Faden.Agents.Sessions.EntryTypes.UserMessage).Data;
        Assert.Equal(("What does this sum to?", "Sheet1!A1:A3"), (stored.GetProperty("text").GetString(),
            stored.GetProperty("attachments")[0].GetProperty("title").GetString()));
        Assert.Equal("What does this sum to?", Assert.Single(await agent.ListSessionsAsync()).Title);
    }
}

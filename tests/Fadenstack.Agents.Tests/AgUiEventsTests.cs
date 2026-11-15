using System.Text;
using System.Text.Json;
using Fadenstack.Agents.AgUi;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// AG-UI events (sdk/51 F07): the golden files in <c>AgUi</c> are the same bytes as the TypeScript SDK's
/// <c>packages/agents/test/agui</c>, one JSON event per line.
/// </summary>
public class AgUiEventsTests
{
    private const string T = "turn-1";

    private static async IAsyncEnumerable<AgentEvent> From(IEnumerable<AgentEvent> events, Exception? failure = null)
    {
        foreach (var e in events)
        {
            yield return e;
            await Task.Yield();
        }
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task<string> Run(IEnumerable<AgentEvent> events, Exception? failure = null)
    {
        var now = 1_700_000_000_000L;
        var text = new StringBuilder();
        var options = new AgUiOptions { ThreadId = "thread-1", Clock = () => now++ };
        await foreach (var e in AgUiEvents.From(From(events, failure), options))
        {
            text.Append(AgUiJson.Serialize(e)).Append('\n');
        }
        return text.ToString();
    }

    private static void Golden(string name, string actual)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AgUi", name + ".jsonl");
        Assert.Equal(File.ReadAllText(path, new UTF8Encoding(false)), actual);
    }

    private static readonly AgentEvent Started = new TurnStarted(ToolMode.Ask) { TurnId = T };

    private static AgentEvent Done(ChatOutcome outcome, string text, bool usage = true) => new TurnCompleted(new AgentResult
    {
        TurnId = T,
        Outcome = outcome,
        Text = text,
        Usage = usage ? new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5, TotalTokenCount = 17 } : null,
    }) { TurnId = T };

    private static readonly ApprovalRequest Request = new()
    {
        SessionId = "thread-1", TurnId = T, CallId = "call-2", ToolName = "delete_page", ToolClass = ToolClass.Destructive,
        Source = "host", Reason = "Deleting cannot be undone.",
        Arguments = new Dictionary<string, object?> { ["page"] = 8 },
    };

    [Fact]
    public async Task Text_with_reasoning_before_it()
    {
        Golden("text", await Run(new AgentEvent[]
        {
            Started,
            new ReasoningDelta("Greeting ") { TurnId = T },
            new ReasoningDelta("the user.") { TurnId = T },
            new TextDelta("Hello <b>wor") { TurnId = T },
            new TextDelta("ld</b> & \"all\" - café\nBye") { TurnId = T },
            Done(ChatOutcome.Completed, "Hello <b>world</b> & \"all\" - café\nBye"),
        }));
    }

    [Fact]
    public async Task A_tool_call_and_its_result()
    {
        Golden("tool", await Run(new AgentEvent[]
        {
            Started,
            new TextDelta("Let me look.") { TurnId = T },
            new ToolCallStarted("call-1", "read_page", ToolClass.Read, "host",
                new Dictionary<string, object?> { ["page"] = 7, ["note"] = "a<b" }) { TurnId = T },
            new ToolCallCompleted("call-1", "read_page", "host", "ok", "Table T1, 4 rows", TimeSpan.FromMilliseconds(12)) { TurnId = T },
            new TextDelta("Four rows.") { TurnId = T },
            Done(ChatOutcome.Completed, "Let me look.Four rows."),
        }));
    }

    [Fact]
    public async Task An_approval_ends_the_run_with_an_interrupt_and_the_answer_resumes_on_a_new_run()
    {
        Golden("approval", await Run(new AgentEvent[]
        {
            Started,
            new ToolCallStarted("call-2", "delete_page", ToolClass.Destructive, "host",
                new Dictionary<string, object?> { ["page"] = 8 }) { TurnId = T },
            new ApprovalRequested(Request) { TurnId = T },
            new ApprovalDecided(Request, ApprovalDecision.Approve()) { TurnId = T },
            new ToolCallCompleted("call-2", "delete_page", "host", "ok", "deleted", TimeSpan.FromMilliseconds(3)) { TurnId = T },
            new TextDelta("Deleted.") { TurnId = T },
            Done(ChatOutcome.Completed, "Deleted."),
        }));
    }

    [Fact]
    public async Task An_error_of_the_stream_ends_the_run_with_RUN_ERROR()
    {
        Golden("error", await Run(new AgentEvent[] { Started, new TextDelta("Partial") { TurnId = T } },
            new InvalidOperationException("The model call failed.")));
    }

    [Fact]
    public async Task Notices_and_gateway_words_become_faden_custom_events_and_the_gateway_round_stays_out()
    {
        using var citations = JsonDocument.Parse(
            "{\"type\":\"CUSTOM\",\"name\":\"faden.citations\",\"value\":{\"tool\":\"knowledge_search\",\"sources\":[{\"source\":\"a.pdf\",\"score\":0.9}]}}");
        using var round = JsonDocument.Parse("{\"type\":\"CUSTOM\",\"name\":\"faden.gateway_round\",\"value\":{\"messages\":[]}}");
        using var start = JsonDocument.Parse("{\"type\":\"TOOL_CALL_START\",\"toolCallId\":\"x\"}");
        Golden("notice", await Run(new AgentEvent[]
        {
            Started,
            new GatewayEvent(new FadenstackEventContent(citations.RootElement)) { TurnId = T },
            new GatewayEvent(new FadenstackEventContent(round.RootElement)) { TurnId = T },
            new GatewayEvent(new FadenstackEventContent(start.RootElement)) { TurnId = T },
            new NoticeRaised(new GovernanceNotice
            {
                Outcome = "response_blocked", Stage = "output", Category = "pii", Message = "Answer withheld.", RequestId = "req-1",
            }) { TurnId = T },
            Done(ChatOutcome.ResponseBlocked, "", usage: false),
        }));
    }

    [Fact]
    public async Task A_cancellation_is_thrown_on_not_turned_into_RUN_ERROR()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(new[] { Started }, new OperationCanceledException("aborted")));
    }

    [Fact]
    public async Task Without_a_thread_id_the_first_turn_stands_in_and_the_system_clock_is_used()
    {
        var all = new List<AgUiEvent>();
        await foreach (var e in AgUiEvents.From(From(new[] { Started, Done(ChatOutcome.Completed, "", usage: false) })))
        {
            all.Add(e);
        }
        var finished = Assert.IsType<RunFinished>(all[^1]);
        Assert.Equal(T, finished.ThreadId);
        Assert.True(all.All(e => e.Timestamp > 1_700_000_000_000L));
    }
}

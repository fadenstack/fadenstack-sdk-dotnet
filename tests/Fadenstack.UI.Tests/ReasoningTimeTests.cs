using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>"Thought for N s" counts the thinking only: not a tool's run, an approval's wait or the answer's streaming.</summary>
public class ReasoningTimeTests
{
    private static ToolCallStarted Delegate() =>
        new("call-1", "delegate", ToolClass.Write, "host", new Dictionary<string, object?> { ["agent"] = "word-assistant" });

    [Fact]
    public void Thinking_after_a_tool_call_is_its_own_block_below_it_and_the_first_keeps_its_time()
    {
        // Found live: a turn that thought 30 s, delegated for 100 s and thought again read "Thought for 139 s".
        var start = DateTimeOffset.UtcNow.AddSeconds(-4);
        var turn = new TurnViewModel(null, "q", start, 100);

        turn.AppendReasoning("Word work goes to the Word assistant.", start);
        turn.ToolStarted(Delegate());
        var after = DateTimeOffset.UtcNow.AddSeconds(100);
        turn.AppendReasoning("It is done; say so.", after);
        turn.Fail("stopped here", after.AddSeconds(3));

        Assert.Equal(new[] { typeof(ReasoningPart), typeof(ToolCallPart), typeof(ReasoningPart) },
            turn.Parts.Select(p => p.GetType()).ToArray());
        var thoughts = turn.Parts.OfType<ReasoningPart>().ToList();
        Assert.Equal("Thought for 4 s", thoughts[0].Title);
        Assert.Equal("Thought for 3 s", thoughts[1].Title);
        Assert.All(thoughts, t => Assert.False(t.IsStreaming));
    }

    [Fact]
    public void The_answer_starting_ends_the_thinking()
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-2);
        var turn = new TurnViewModel(null, "q", start, 100);

        turn.AppendReasoning("A short answer will do.", start);
        turn.AppendText("Hello");

        var thought = turn.Parts.OfType<ReasoningPart>().Single();
        Assert.False(thought.IsStreaming);
        Assert.Equal("Thought for 2 s", thought.Title);

        turn.Fail("stopped here", DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.Equal("Thought for 2 s", thought.Title);
    }
}

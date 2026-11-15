using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>sdk/51 F08 §1: a pending approval is answered once; a second answer (a double click, a second card) changes nothing.</summary>
public class PendingApprovalOnceTests
{
    [Fact]
    public async Task The_first_answer_holds_and_a_second_is_refused()
    {
        var pending = new PendingApproval(new ApprovalRequest { ToolName = "add_comment", ToolClass = ToolClass.Write, CallId = "call-1" }, CancellationToken.None);

        Assert.True(pending.Resolve(ApprovalDecision.Deny("Not now.")));
        Assert.False(pending.Resolve(ApprovalDecision.ApproveAlways()));

        var decision = await pending.Decision;
        Assert.False(decision.Approved);
        Assert.Equal("Not now.", decision.Reason);
        Assert.True(pending.IsResolved);
    }
}

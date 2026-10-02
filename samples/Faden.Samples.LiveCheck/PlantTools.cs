using System.ComponentModel;
using Faden.Agents;
using Faden.Tools;

namespace Faden.Samples.LiveCheck;

/// <summary>Host tools as an EPLAN add-in would offer them; every call is recorded, so a check can see what ran.</summary>
public sealed class PlantTools
{
    private readonly List<string> _calls = new();

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_calls)
        {
            _calls.Clear();
        }
    }

    [AgentTool(ToolClass.Read), Description("Reads a page of the open schematic and returns what is on it.")]
    public string ReadPage([Description("Page number")] int page = 1) =>
        Record($"read_page:{page}", $"Page {page}: contactor K{page}1, terminal strip X{page}, cable W{page}0{page}.");

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page of the schematic.")]
    public string AddComment([Description("Page number")] int page, [Description("The comment's text")] string text) =>
        Record($"add_comment:{page}", $"Comment '{text}' added to page {page}.");

    [AgentTool(ToolClass.Destructive), Description("Deletes a page from the schematic.")]
    public string DeletePage([Description("Page number")] int page) =>
        Record($"delete_page:{page}", $"Page {page} deleted.");

    // Not in the agent's host tool contract: the SDK must drop it at start.
    [AgentTool(ToolClass.Read), Description("Stamps the drawing with today's date.")]
    public string StampPage() => Record("stamp_page", $"Stamped {DateTime.Today:yyyy-MM-dd}.");

    private string Record(string call, string result)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
        return result;
    }
}

/// <summary>Approvals decided by the check that runs, and every request remembered.</summary>
public sealed class ScriptedApprovals : IApprovalHandler
{
    private readonly List<ApprovalRequest> _asked = new();

    public Func<ApprovalRequest, ApprovalDecision> Decide { get; set; } = _ => ApprovalDecision.Deny("scripted: deny");

    public IReadOnlyList<ApprovalRequest> Asked
    {
        get
        {
            lock (_asked)
            {
                return _asked.ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_asked)
        {
            _asked.Clear();
        }
    }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_asked)
        {
            _asked.Add(request);
        }
        return Task.FromResult(Decide(request));
    }
}

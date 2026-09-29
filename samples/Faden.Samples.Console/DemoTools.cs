using System.ComponentModel;
using Faden.Agents;
using Faden.Tools;

namespace Faden.Samples;

/// <summary>Host tools as an add-in would write them: each says what it does, or it asks every time.</summary>
public sealed class DemoTools
{
    [AgentTool(ToolClass.Read), Description("Reads a page of the open schematic.")]
    public string ReadPage([Description("Page number")] int page = 1) => $"Page {page}: contactor K{page}, terminal strip X{page}.";

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
    public string AddComment(int page = 1, string text = "checked") => $"Comment '{text}' added to page {page}.";

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page = 1) => $"Page {page} deleted.";

    [AgentTool, Description("Stamps the drawing with today's date.")]
    public string StampPage() => $"Stamped {DateTime.Today:yyyy-MM-dd}.";
}

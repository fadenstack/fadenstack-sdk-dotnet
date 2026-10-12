using System.ComponentModel;
using Fadenstack.Tools;

namespace Fadenstack.Agents.Tests;

/// <summary>Host tools as an integrator writes them.</summary>
public sealed class PageTools
{
    public List<string> Ran { get; } = new();

    [AgentTool(ToolClass.Read), Description("Reads a page of the document.")]
    public string ReadPage([Description("Page number")] int page)
    {
        Ran.Add($"read_page {page}");
        return $"page {page}: 3 terminals";
    }

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
    public string AddComment(int page, string text)
    {
        Ran.Add($"add_comment {page} {text}");
        return "added";
    }

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public async Task<string> DeletePageAsync(int page)
    {
        await Task.Yield();
        Ran.Add($"delete_page {page}");
        return "deleted";
    }

    [AgentTool, Description("Stamps the drawing.")]
    public string Stamp()
    {
        Ran.Add("stamp");
        return "stamped";
    }
}

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

/// <summary>Tools that reach outside the organisation, and one that does not.</summary>
public sealed class WebTools
{
    /// <summary>Their contract: all ask in Ask mode.</summary>
    public const string Contract = """
        [{"name":"search_web","risk":"write"},{"name":"fill_field","risk":"write"},{"name":"publish_post","risk":"destructive"}]
        """;

    [AgentTool(ToolClass.Write, OpenWorld = true), Description("Searches the web.")]
    public string SearchWeb(string query) => $"results for {query}";

    [AgentTool(ToolClass.Write), Description("Fills a field of the form.")]
    public string FillField(string query) => "filled";

    [AgentTool(ToolClass.Destructive, OpenWorld = true), Description("Publishes a post on the company's site.")]
    public string PublishPost(string query) => "published";
}

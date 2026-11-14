using System.ComponentModel;
using Fadenstack.Agents;
using Fadenstack.Tools;

namespace Fadenstack.Samples;

/// <summary>Host tools as an add-in would write them: each says what it does, or it asks every time.</summary>
public sealed class DemoTools
{
    /// <summary>The agent definition for standalone runs (<c>--endpoint</c>): what an app ships as its <c>contracts/*.json</c>.</summary>
    public const string Definition = """
        {"slug":"console-assistant","name":"Console assistant","version":{
          "instructions":"You are a helpful assistant in a console. The open document has pages; read them with read_page before you answer about them. Keep answers short.",
          "host_tool_contract":[{"name":"read_page","risk":"read"},{"name":"add_comment","risk":"write"},
            {"name":"delete_page","risk":"destructive"},{"name":"stamp_page"}],
          "ui":{"greeting":"Ask about the open document."}}}
        """;

    [AgentTool(ToolClass.Read), Description("Reads a page of the open document.")]
    public string ReadPage([Description("Page number")] int page = 1) => $"Page {page}: section {page}.1, table T{page}.";

    [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
    public string AddComment(int page = 1, string text = "checked") => $"Comment '{text}' added to page {page}.";

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page = 1) => $"Page {page} deleted.";

    [AgentTool, Description("Stamps the drawing with today's date.")]
    public string StampPage() => $"Stamped {DateTime.Today:yyyy-MM-dd}.";
}

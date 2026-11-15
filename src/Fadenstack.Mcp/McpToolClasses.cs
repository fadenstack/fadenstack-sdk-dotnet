using Fadenstack.Tools;

namespace Fadenstack.Mcp;

/// <summary>
/// The class of an MCP tool (sdk/51 F05, rule H4). An MCP server describes its own tools, and the MCP specification says a client
/// must treat annotations as untrusted unless it trusts the server. So:
/// <list type="bullet">
/// <item>a tool arrives <see cref="ToolClass.Unknown"/>, which asks (the floor);</item>
/// <item><c>destructiveHint: true</c> from any server makes it <see cref="ToolClass.Destructive"/>, which always asks: a hint can only make a tool stricter;</item>
/// <item>a <c>readOnlyHint</c> counts only from a server the person (standalone) or the administrator (governed) marked trusted;</item>
/// <item>only the person or the administrator can raise a tool to <see cref="ToolClass.Read"/> or <see cref="ToolClass.Write"/>, and never below a destructive hint.</item>
/// </list>
/// </summary>
public static class McpToolClasses
{
    /// <param name="serverTrusted">Whether the person or the administrator marked the server trusted.</param>
    /// <param name="hints">The server's annotations for the tool.</param>
    /// <param name="raised">The class the person or the administrator gave the tool; only Read and Write count.</param>
    public static ToolClass Classify(bool serverTrusted, McpToolHints hints, ToolClass? raised)
    {
        if (hints is null)
        {
            throw new ArgumentNullException(nameof(hints));
        }
        if (hints.Destructive == true)
        {
            return ToolClass.Destructive;
        }
        if (raised is ToolClass.Read or ToolClass.Write)
        {
            return raised.Value;
        }
        return serverTrusted && hints.ReadOnly == true ? ToolClass.Read : ToolClass.Unknown;
    }

    /// <summary>The class of a tool of <paramref name="server"/>, by the name the server knows it by.</summary>
    public static ToolClass Classify(LocalMcpServer server, string serverToolName, McpToolHints hints)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }
        ToolClass? raised = server.ToolClasses is { } raises && raises.TryGetValue(serverToolName, out var value) ? value : null;
        return Classify(server.Trusted, hints, raised);
    }
}

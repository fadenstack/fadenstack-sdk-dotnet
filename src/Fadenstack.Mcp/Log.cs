using Microsoft.Extensions.Logging;

namespace Fadenstack.Mcp;

/// <summary>
/// What the local MCP module logs: server labels, transport kinds and tool counts -- never a program's arguments, a URL,
/// tool names, arguments or results. Event ids 3000-3099.
/// </summary>
internal static partial class Log
{
    /// <summary>The category of everything the MCP module logs.</summary>
    internal const string Category = "Fadenstack.Mcp";

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "MCP server {Label} ({Transport}) attached with {ToolCount} tools")]
    internal static partial void Attached(ILogger logger, string label, string transport, int toolCount);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "MCP server {Label} ({Transport}) was refused by the agent's policy")]
    internal static partial void Refused(ILogger logger, string label, string transport);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "MCP server {Label} ({Transport}) could not be attached ({ErrorType})")]
    internal static partial void AttachFailed(ILogger logger, string label, string transport, string errorType);
}

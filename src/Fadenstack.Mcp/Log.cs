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

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "MCP server {Label} ({Transport}) stopped; starting it again (try {Attempt} of {Max})")]
    internal static partial void Restarting(ILogger logger, string label, string transport, int attempt, int max);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "MCP server {Label} changed its tools: {ToolCount} now")]
    internal static partial void ToolsChanged(ILogger logger, string label, int toolCount);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "A call to MCP server {Label} did not finish within {Seconds} s and was cancelled")]
    internal static partial void CallTimedOut(ILogger logger, string label, int seconds);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Information, Message = "MCP server {Label} asked the person a question: {Outcome}")]
    internal static partial void Question(ILogger logger, string label, string outcome);
}

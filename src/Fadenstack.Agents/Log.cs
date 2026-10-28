using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>
/// What the agent logs: names, ids, counts, durations and outcomes -- never a prompt, an answer, reasoning, attachment text,
/// tool arguments or results, a token, a secret, or who the user is. Event ids 2000-2099.
/// </summary>
internal static partial class Log
{
    /// <summary>The category of everything the agent logs.</summary>
    internal const string Category = "Fadenstack.Agents";

    private static readonly Func<ILogger, string, string, IDisposable?> TurnScopeOf =
        LoggerMessage.DefineScope<string, string>("conversation {ConversationId} turn {TurnId}");

    /// <summary>The scope of a turn's entries: its conversation and turn ids.</summary>
    internal static IDisposable? TurnScope(ILogger logger, string conversationId, string turnId) =>
        TurnScopeOf(logger, conversationId, turnId);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Agent {Agent} loaded: version {Version}, edition {Edition}, {FeatureCount} features, user partition {UserPartition}")]
    internal static partial void ProfileLoaded(ILogger logger, string agent, int? version, string edition, int featureCount, bool userPartition);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Agent {Agent} tools settled: {HostTools} host, {SourceTools} from sources, {Dropped} dropped by the contract")]
    internal static partial void ToolsSettled(ILogger logger, string agent, int hostTools, int sourceTools, int dropped);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Host tool {Tool} is not in the agent's contract and was dropped")]
    internal static partial void ToolDropped(ILogger logger, string tool);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "Agent {Agent} runs in bypass mode: tool calls that would ask are approved without asking, destructive ones too; an admin's \"Ask first\" rule still asks")]
    internal static partial void BypassMode(ILogger logger, string agent);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "The earlier sessions and approvals ({Folders} folders) moved into the user's own folder")]
    internal static partial void DataAdopted(ILogger logger, int folders);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning,
        Message = "The earlier sessions and approvals could not move into the user's own folder ({ErrorType}); they stay where they are")]
    internal static partial void DataAdoptionFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information,
        Message = "Turn {TurnId} of conversation {ConversationId} started in tool mode {ToolMode} with {Attachments} attachments")]
    internal static partial void TurnStarted(ILogger logger, string conversationId, string turnId, string toolMode, int attachments);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Information,
        Message = "Turn {TurnId} of conversation {ConversationId} ended {Status} ({Outcome}) after {Rounds} rounds in {DurationMs} ms, tool mode {ToolMode}")]
    internal static partial void TurnEnded(ILogger logger, string conversationId, string turnId, string toolMode, int rounds, string status,
        string? outcome, long durationMs);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Debug,
        Message = "Model request {Round} of turn {TurnId} answered with {ToolCalls} tool calls in {DurationMs} ms")]
    internal static partial void ModelRequest(ILogger logger, string turnId, int round, int toolCalls, long durationMs);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Information,
        Message = "Tool {Tool} ({ToolClass}, {Source}) ended {Outcome} in {DurationMs} ms, approval {Approval}")]
    internal static partial void ToolCall(ILogger logger, string tool, string toolClass, string source, string outcome, string approval, long durationMs);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Information,
        Message = "Approval for {Tool} ({ToolClass}): {Approval}, asked {Asked}, waited {WaitMs} ms")]
    internal static partial void Approval(ILogger logger, string tool, string toolClass, string approval, bool asked, long waitMs);

    [LoggerMessage(EventId = 2040, Level = LogLevel.Warning,
        Message = "Agent {Agent} keeps session transcripts in memory only: no OS key store is available to encrypt them on disk")]
    internal static partial void TranscriptsNotKept(ILogger logger, string agent);

    [LoggerMessage(EventId = 2030,Level = LogLevel.Information,
        Message = "Governance notice {Outcome} at stage {Stage}, category {Category}, code {Code}, decision {DecisionId}")]
    internal static partial void Notice(ILogger logger, string outcome, string? stage, string? category, string? code, string? decisionId);
}

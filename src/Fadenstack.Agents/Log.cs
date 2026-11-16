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

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information,
        Message = "Agent {Agent} asked {Sources} changed tool sources again: {Tools} tools now")]
    internal static partial void ToolsRefreshed(ILogger logger, string agent, int sources, int tools);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Warning,
        Message = "Agent {Agent} could not ask a changed tool source again ({ErrorType}); it keeps the tools it had")]
    internal static partial void ToolsRefreshFailed(ILogger logger, string agent, string errorType);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Host tool {Tool} is not in the agent's contract and was dropped")]
    internal static partial void ToolDropped(ILogger logger, string tool);

    [LoggerMessage(EventId = 2018, Level = LogLevel.Warning, Message = "A {Kind} was left out of the device report: {Reason}")]
    internal static partial void DeviceEntryDropped(ILogger logger, string kind, string reason);

    [LoggerMessage(EventId = 2016, Level = LogLevel.Warning, Message = "A tool source's tool {Tool} was left out: the name is the SDK's own")]
    internal static partial void ToolNameReserved(ILogger logger, string tool);

    [LoggerMessage(EventId = 2017, Level = LogLevel.Warning, Message = "A tool source's tool {Tool} was left out: {Winner} has that name")]
    internal static partial void ToolNameTaken(ILogger logger, string tool, string winner);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "Agent {Agent} is set to bypass mode, which this server does not allow: every tool call that would ask still asks; bypass mode never answers the gateway's own tools")]
    internal static partial void BypassMode(ILogger logger, string agent);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Warning,
        Message = "Agent {Agent} could not read its profile again ({ErrorType}); the one held stays")]
    internal static partial void ProfileRereadFailed(ILogger logger, string agent, string errorType);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "The earlier sessions and approvals ({Folders} folders) moved into the user's own folder")]
    internal static partial void DataAdopted(ILogger logger, int folders);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning,
        Message = "The earlier sessions and approvals could not move into the user's own folder ({ErrorType}); they stay where they are")]
    internal static partial void DataAdoptionFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information,
        Message = "The user's data on this device was exported: {Agents} agents, {Sessions} sessions, {Approvals} approvals, {Unreadable} unreadable")]
    internal static partial void DataExported(ILogger logger, int agents, int sessions, int approvals, int unreadable);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information,
        Message = "The user's data on this device was deleted: {Folders} folders removed")]
    internal static partial void DataDeleted(ILogger logger, int folders);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Warning,
        Message = "The user's data on this device was deleted, but not in the stores the host passed (sessions {Sessions}, approvals {Approvals}): the host deletes its part")]
    internal static partial void DataLeftInHostStores(ILogger logger, bool sessions, bool approvals);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning,
        Message = "The user's data on this device could not be deleted ({ErrorType})")]
    internal static partial void DataDeletionFailed(ILogger logger, string errorType);

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

    [LoggerMessage(EventId = 2083, Level = LogLevel.Warning,
        Message = "A tool call could not be written to the audit log ({Kind}); the call goes on and the next check of the log reports the gap")]
    internal static partial void AuditNotWritten(ILogger logger, string kind);

    [LoggerMessage(EventId = 2084, Level = LogLevel.Warning,
        Message = "Old entries of the audit log could not be removed ({Kind})")]
    internal static partial void AuditNotPruned(ILogger logger, string kind);

    [LoggerMessage(EventId = 2050, Level = LogLevel.Warning,
        Message = "Stored approvals could not be read ({Kind}); the user is asked again")]
    internal static partial void ApprovalsUnreadable(ILogger logger, string kind);

    [LoggerMessage(EventId = 2051, Level = LogLevel.Warning,
        Message = "{Count} lines of a saved chat could not be read (the first: {Kind}); they are skipped")]
    internal static partial void ChatLinesSkipped(ILogger logger, int count, string kind);

    [LoggerMessage(EventId = 2052, Level = LogLevel.Warning,
        Message = "A saved chat could not be read ({Kind}); it is left out for now")]
    internal static partial void ChatUnreadable(ILogger logger, string kind);

    [LoggerMessage(EventId = 2053, Level = LogLevel.Warning,
        Message = "An approval answer got no word back ({Kind}); it is sent once more")]
    internal static partial void ApprovalAnswerRetried(ILogger logger, string kind);

    [LoggerMessage(EventId = 2054, Level = LogLevel.Warning,
        Message = "Agent {Agent} runs on its own model endpoint without the provenance module: what it places in documents carries no mark. Add Fadenstack.Provenance (builder.UseProvenance)")]
    internal static partial void NoProvenanceModule(ILogger logger, string agent);

    [LoggerMessage(EventId = 2060, Level = LogLevel.Warning,
        Message = "A hook ({HookType}) threw {ErrorType}; the call goes on as the policy decided")]
    internal static partial void HookFailed(ILogger logger, string hookType, string errorType);

    // web_search (sdk/51 F06): the provider, a count, a duration and the kind of failure; never the query, a result or the key.
    [LoggerMessage(EventId = 2080, Level = LogLevel.Information,
        Message = "A web search with {Provider} found {Count} results in {DurationMs} ms")]
    internal static partial void WebSearchRan(ILogger logger, string provider, int count, long durationMs);

    [LoggerMessage(EventId = 2081, Level = LogLevel.Warning,
        Message = "A web search with {Provider} failed ({Kind})")]
    internal static partial void WebSearchFailed(ILogger logger, string provider, string kind);

    [LoggerMessage(EventId = 2082, Level = LogLevel.Debug,
        Message = "Web search is not offered ({Reason})")]
    internal static partial void WebSearchOff(ILogger logger, string reason);

    // The off-switch and "Stop everything" (core 0.7.1, release plan 8.3 and 8.11): the agent's slug and counts, never content (ASVS 5.0 V16).
    [LoggerMessage(EventId = 2090, Level = LogLevel.Warning,
        Message = "Agent {Agent} was turned off by the organisation: its turns stopped and its grants ended")]
    internal static partial void AgentTurnedOff(ILogger logger, string agent);

    [LoggerMessage(EventId = 2091, Level = LogLevel.Information,
        Message = "Agent {Agent} stopped everything: {Turns} turns stopped, {Grants} grants ended, {NotTold} the server was not told about")]
    internal static partial void StoppedEverything(ILogger logger, string agent, int turns, int grants, int notTold);

    // Content logging (8.1): when it was switched on or off and when it ends; never any content.
    [LoggerMessage(EventId = 2083, Level = LogLevel.Information,
        Message = "Agent {Agent}: content logging was switched on for {Minutes} minutes, until {Until}")]
    internal static partial void ContentLoggingStarted(ILogger logger, string agent, int minutes, DateTimeOffset until);

    [LoggerMessage(EventId = 2084, Level = LogLevel.Information,
        Message = "Agent {Agent}: content logging is off ({How}); it was set to end at {Until}")]
    internal static partial void ContentLoggingStopped(ILogger logger, string agent, string how, DateTimeOffset until);

    [LoggerMessage(EventId = 2030,Level = LogLevel.Information,
        Message = "Governance notice {Outcome} at stage {Stage}, category {Category}, code {Code}, decision {DecisionId}")]
    internal static partial void Notice(ILogger logger, string outcome, string? stage, string? category, string? code, string? decisionId);
}

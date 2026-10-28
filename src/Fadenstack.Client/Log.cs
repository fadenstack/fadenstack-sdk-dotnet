using Microsoft.Extensions.Logging;

namespace Fadenstack;

/// <summary>
/// What the client logs: names, route templates, status codes, counts and durations -- never a prompt, an answer, a body,
/// a token, a query string or who the user is. Event ids 1000-1099.
/// </summary>
internal static partial class Log
{
    /// <summary>The category of everything the client logs.</summary>
    internal const string Category = "Fadenstack.Client";

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "{Method} {Route} answered {Status} in {DurationMs} ms (attempt {Attempt})")]
    internal static partial void Request(ILogger logger, string method, string route, int status, long durationMs, int attempt);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "{Method} {Route} failed with {ErrorType} after {DurationMs} ms (attempt {Attempt})")]
    internal static partial void RequestFailed(ILogger logger, string method, string route, string errorType, long durationMs, int attempt);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "The sign-in to agent {Agent} was renewed")]
    internal static partial void TokenRefreshed(ILogger logger, string agent);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "The sign-in to agent {Agent} could not be renewed ({Reason})")]
    internal static partial void TokenRefreshFailed(ILogger logger, string agent, string reason);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning, Message = "A faden event was skipped: it is {Reason}")]
    internal static partial void EventSkipped(ILogger logger, string reason);
}

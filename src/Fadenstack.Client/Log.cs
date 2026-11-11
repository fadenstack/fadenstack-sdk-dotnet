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

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Signed out of agent {Agent} on this device (revoked at the server: {Revoked})")]
    internal static partial void SignedOut(ILogger logger, string agent, bool revoked);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning,
        Message = "The sign-in to agent {Agent} could not be revoked at the server ({Reason}); it is deleted here")]
    internal static partial void SignOutRevokeFailed(ILogger logger, string agent, string reason);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Pairing for agent {Agent} started; it is valid for {ExpiresInS} s")]
    internal static partial void PairingStarted(ILogger logger, string agent, int expiresInS);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "Pairing for agent {Agent}: the server asked to slow down ({IntervalS} s)")]
    internal static partial void PairingSlowedDown(ILogger logger, string agent, int intervalS);

    [LoggerMessage(EventId = 1032, Level = LogLevel.Warning,
        Message = "Pairing for agent {Agent}: the server could not be reached ({Reason}); trying again in {WaitS} s")]
    internal static partial void PairingRetrying(ILogger logger, string agent, string reason, int waitS);

    [LoggerMessage(EventId = 1033, Level = LogLevel.Information, Message = "Pairing for agent {Agent} ended: {Outcome}")]
    internal static partial void PairingEnded(ILogger logger, string agent, string outcome);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning, Message = "A faden event was skipped: it is {Reason}")]
    internal static partial void EventSkipped(ILogger logger, string reason);
}

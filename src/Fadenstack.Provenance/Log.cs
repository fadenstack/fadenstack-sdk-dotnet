using Fadenstack.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Provenance;

/// <summary>
/// What the provenance module logs: tool, marker and argument names, counts and error types -- never the content that
/// was placed, a record's text or a signature. Event ids 4000-4099.
/// </summary>
internal static partial class Log
{
    /// <summary>The category of everything the provenance module logs.</summary>
    internal const string Category = "Fadenstack.Provenance";

    /// <summary>The agent's logger for this module; none outside a session.</summary>
    internal static ILogger For(FadenstackAgent? agent) => agent?.CreateLogger(Category) ?? NullLogger.Instance;

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "The provenance policy could not be read ({ErrorType}); the last known or the default applies")]
    internal static partial void PolicyUnavailable(ILogger logger, string errorType);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "A short-text threshold of {Tokens} tokens would loosen the policy's {PolicyTokens}; ignored")]
    internal static partial void ThresholdIgnored(ILogger logger, int tokens, int policyTokens);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "Tool {Tool} declares {Arguments} as content for people and did not place it through IProvenance.PlaceAsync")]
    internal static partial void NotPlaced(ILogger logger, string tool, string arguments);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "The unmarked content of {Tool} was not reported ({ErrorType})")]
    internal static partial void UnmarkedNotReported(ILogger logger, string tool, string errorType);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "The failed placement by {Tool} was not recorded ({ErrorType})")]
    internal static partial void FailureNotRecorded(ILogger logger, string tool, string errorType);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "The edit classifier failed ({ErrorType}); the declared kind stays")]
    internal static partial void ClassifierFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "The marker {Marker} failed to say what it supports ({ErrorType})")]
    internal static partial void MarkerFailed(ILogger logger, string marker, string errorType);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "The record of a placement by {Tool} was not signed ({ErrorType})")]
    internal static partial void RecordNotSigned(ILogger logger, string tool, string errorType);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning, Message = "The placement by {Tool} was not reported ({ErrorType})")]
    internal static partial void PlacementNotReported(ILogger logger, string tool, string errorType);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Warning, Message = "A placement observer failed ({ErrorType})")]
    internal static partial void ObserverFailed(ILogger logger, string errorType);
}

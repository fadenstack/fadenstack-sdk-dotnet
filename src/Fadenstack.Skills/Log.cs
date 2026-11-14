using Microsoft.Extensions.Logging;

namespace Fadenstack.Skills;

/// <summary>What the skills module logs: skill names and counts, never a skill's text. Event ids 3100-3199.</summary>
internal static partial class Log
{
    internal const string Category = "Fadenstack.Skills";

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Skills are on: {Count} folders, {Bundled} bundled skills")]
    internal static partial void On(ILogger logger, int count, int bundled);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Skills are off: {Reason}")]
    internal static partial void Off(ILogger logger, string reason);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "Skill {Skill} was skipped: {Reason}")]
    internal static partial void Skipped(ILogger logger, string skill, string reason);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information, Message = "A skills folder the machine policy does not list was left out")]
    internal static partial void FolderNotListed(ILogger logger);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Debug, Message = "The skills provider brought a tool the module does not offer; it was left out: {Tool}")]
    internal static partial void ToolLeftOut(ILogger logger, string tool);
}

using Microsoft.Extensions.Logging;

namespace Fadenstack.Secrets;

/// <summary>What the key-store module logs: the kind of failure, never a key, a path or data. Event ids 5000-5099.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "No usable OS key store ({ErrorType}); data stays in owner-only files")]
    internal static partial void NoKeyStore(ILogger logger, string errorType);
}

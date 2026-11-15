using Microsoft.Extensions.Logging;

namespace Fadenstack.Secrets;

/// <summary>What the key-store module logs: the kind of failure, never a key, a path or data. Event ids 5000-5099.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "No usable OS key store ({ErrorType}); data stays in owner-only files")]
    internal static partial void NoKeyStore(ILogger logger, string errorType);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "The OS key store is on: {Count} plain files were encrypted, once; plain data is refused from now on")]
    internal static partial void PlainFilesMoved(ILogger logger, int count);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "The plain files could not all be encrypted ({ErrorType}); the move is tried again at the next start")]
    internal static partial void PlainMoveFailed(ILogger logger, string errorType);
}

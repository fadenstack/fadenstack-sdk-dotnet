using Fadenstack.Auth;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Secrets;

/// <summary>The one call a host makes to encrypt the SDK's data on this device.</summary>
public static class FadenstackSecrets
{
    /// <summary>
    /// Makes the OS key store the default <see cref="SecretProtection"/>. Call it once at start-up, before any store,
    /// token store or agent is created. Returns whether data is now encrypted at rest; false (headless Linux without a
    /// Secret Service) leaves the owner-only files, and the agent then keeps transcripts in memory only.
    /// </summary>
    public static bool UseOsKeyStore(ILogger? logger = null)
    {
        var encrypts = OsKeyStore.TryCreate(out var protector, logger);
        if (encrypts)
        {
            SecretProtection.Use(protector);
        }
        return encrypts;
    }
}

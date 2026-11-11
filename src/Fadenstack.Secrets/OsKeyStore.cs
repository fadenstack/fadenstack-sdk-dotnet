using Fadenstack.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client.Extensions.Msal;
using MsalStorage = Microsoft.Identity.Client.Extensions.Msal.Storage;

namespace Fadenstack.Secrets;

/// <summary>Finds the secret protector the OS offers: DPAPI on Windows, the Keychain or the Secret Service elsewhere.</summary>
public static class OsKeyStore
{
    private const string Service = "Fadenstack";
    private const string Account = "Fadenstack data key";
    private const string Schema = "com.fadenstack.sdk";

    /// <summary>
    /// Windows: DPAPI, as before (existing data stays readable). macOS and Linux: AES-GCM with a data key kept in the
    /// Keychain or the Secret Service (GNOME Keyring, KWallet), when that store works. Otherwise false and
    /// <paramref name="protector"/> is the owner-only file protector: a headless Linux without a Secret Service.
    /// </summary>
    public static bool TryCreate(out ISecretProtector protector, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            protector = new DpapiSecretProtector();
            return true;
        }
        return TryCreate(out protector, logger, OpenOsKeyStore);
    }

    internal static bool TryCreate(out ISecretProtector protector, ILogger? logger, Func<IKeyStore> open)
    {
        try
        {
            protector = new OsKeyStoreProtector(open());
            return true;
        }
        catch (Exception e)
        {
            if (logger is not null)
            {
                Log.NoKeyStore(logger, e.GetType().Name);
            }
            protector = new OwnerOnlyFileProtector();
            return false;
        }
    }

    private static IKeyStore OpenOsKeyStore()
    {
        var properties = new StorageCreationPropertiesBuilder("key-store.bin", SecretProtection.DataDirectory("keystore"))
            .WithMacKeyChain(Service, Account)
            .WithLinuxKeyring(Schema, "default", Account, new KeyValuePair<string, string>("Product", Service), new KeyValuePair<string, string>("Version", "1"))
            .Build();
        var storage = MsalStorage.Create(properties);
        storage.VerifyPersistence(); // writes, reads and clears a test value; throws when there is no usable key store
        return new MsalKeyStore(storage);
    }

    private sealed class MsalKeyStore(MsalStorage storage) : IKeyStore
    {
        public byte[]? Read() => storage.ReadData() is { Length: > 0 } data ? data : null;

        public void Write(byte[] key) => storage.WriteData(key);
    }
}

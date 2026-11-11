using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Fadenstack.Storage;

namespace Fadenstack.Auth;

/// <summary>Encrypts what the SDK keeps on disk (tokens, local sessions) for the current user.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);

    /// <summary>Whether the data is encrypted at rest (DPAPI), or only kept to the user's own file.</summary>
    bool Encrypts { get; }
}

/// <summary>DPAPI, current-user scope: only this Windows user on this machine can read it.</summary>
#if NET
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Fadenstack SDK v1"u8.ToArray();

    public bool Encrypts => true;

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// No encryption: the data goes to a file only its owner may read (mode 0600). The default on macOS and Linux
/// until the host turns on the OS key store (Fadenstack.Secrets, <c>FadenstackSecrets.UseOsKeyStore</c>).
/// </summary>
public sealed class OwnerOnlyFileProtector : ISecretProtector
{
    public bool Encrypts => false;

    public byte[] Protect(byte[] data) => data;

    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>The protector for this machine, and the file helpers that go with it.</summary>
public static class SecretProtection
{
    private static readonly object Gate = new();
    private static ISecretProtector? s_default;
    private static bool s_chosen;

    /// <summary>
    /// The protector the stores use unless given one: DPAPI on Windows, else what the host chose with <see cref="Use"/>,
    /// else owner-only files. Reading it fixes the choice: a store created now keeps this protector.
    /// </summary>
    public static ISecretProtector Default
    {
        get
        {
            lock (Gate)
            {
                s_chosen = true;
                return s_default ??= Create();
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="protector"/> the default, once. Call it at start-up, before the first store or agent is
    /// created: they keep the protector they were created with, and data one protector wrote another may not read.
    /// Throws if the default was already chosen or read.
    /// </summary>
    public static void Use(ISecretProtector protector)
    {
        if (protector is null)
        {
            throw new ArgumentNullException(nameof(protector));
        }
        lock (Gate)
        {
            if (s_chosen)
            {
                throw new InvalidOperationException(
                    "The secret protector was already chosen or used; call SecretProtection.Use before the first store is created.");
            }
            s_default = protector;
            s_chosen = true;
        }
    }

    /// <summary>Forgets the choice, so a test can make it again.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            s_default = null;
            s_chosen = false;
        }
    }

    private static ISecretProtector Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DpapiSecretProtector();
        }
        return new OwnerOnlyFileProtector();
    }

    /// <summary>A folder in the SDK's data root; see <see cref="FadenstackPaths.Data"/> for where that is.</summary>
    public static string DataDirectory(params string[] parts) => FadenstackPaths.Data(parts);

    /// <summary>Writes a file only the current user may read, atomically.</summary>
    public static void WriteOwnerOnly(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        OwnerOnly(temporary, directory: false);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(temporary, path);
    }

    internal static void OwnerOnly(string path, bool directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // the user profile is the user's; DPAPI encrypts
        }
        try
        {
            _ = chmod(path, directory ? 0x1C0 /* 0700 */ : 0x180 /* 0600 */);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not a libc system: nothing to tighten.
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, int mode);
}

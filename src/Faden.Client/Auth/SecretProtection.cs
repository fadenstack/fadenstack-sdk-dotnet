using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Faden.Auth;

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
/// No encryption: the data goes to a file only its owner may read (mode 0600). For systems without DPAPI
/// until an OS keyring module exists.
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
    public static ISecretProtector Default { get; } = Create();

    private static ISecretProtector Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DpapiSecretProtector();
        }
        return new OwnerOnlyFileProtector();
    }

    /// <summary>The SDK's data folder: <c>%LOCALAPPDATA%\Fadenstack</c>, or the user data folder elsewhere.</summary>
    public static string DataDirectory(params string[] parts)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        var path = Path.Combine(new[] { root, "Fadenstack" }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        OwnerOnly(path, directory: true);
        return path;
    }

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

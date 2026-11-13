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

    /// <summary>
    /// Writes a file only the current user may read, atomically: a reader finds the old file or the new one, never a torn
    /// one, and writers at the same time never clash (the last one wins). On macOS and Linux the file is made owner-only
    /// (0600) while it is still empty, before the data goes in; when that fails, nothing is written, the old file stays
    /// and an <see cref="IOException"/> names the kind of failure, never the path (OWASP ASVS 5.0 V14.2, data at rest).
    /// </summary>
    public static void WriteOwnerOnly(string path, byte[] data) => WriteOwnerOnly(path, data, RequireOwnerOnly);

    /// <summary>The same, with the step that makes the empty file owner-only given (tests simulate its failure).</summary>
    internal static void WriteOwnerOnly(string path, byte[] data, Action<string> ownerOnly)
    {
        var target = Path.GetFullPath(path);
        // A name of its own for every write, in the same folder, so the last step is a rename on one volume.
        var temporary = Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CreateEmpty(temporary).Dispose();
            ownerOnly(temporary);
            using (var stream = new FileStream(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true); // on disk before the rename, so a crash leaves the old file or the new one
            }
            Replace(temporary, target);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Reads a file <see cref="WriteOwnerOnly(string, byte[])"/> wrote, sharing it so that a writer's rename goes ahead
    /// meanwhile (on Windows a plain read holds it up); this read keeps the bytes it opened.
    /// </summary>
    internal static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>
    /// How often the last step is tried while another process holds the file (Windows: a plain reader, or another writer's
    /// rename at the same moment); about two seconds in all.
    /// </summary>
    private const int ReplaceAttempts = 20;

    private static FileStream CreateEmpty(string path)
    {
#if NET
        if (!OperatingSystem.IsWindows())
        {
            // Created 0600 (the umask can only narrow it), so there is not even an empty moment with a wider mode.
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
#endif
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    private static void Replace(string temporary, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
#if NET
                File.Move(temporary, target, overwrite: true); // rename(2) on macOS and Linux; MoveFileEx on Windows
#else
                if (File.Exists(target))
                {
                    File.Replace(temporary, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, target);
                }
#endif
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(Math.Min(10 * attempt, 150));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left behind; it holds the protected bytes only, with the file's own mode
        }
    }

    /// <summary>
    /// Makes a file owner-only (0600) on macOS and Linux, and checks that it is: a file system that cannot (a FAT or SMB
    /// mount), or a system without <c>libc</c>, fails the write instead of leaving the file readable by others.
    /// </summary>
    internal static void RequireOwnerOnly(string file)
    {
#if NET
        if (OperatingSystem.IsWindows())
        {
            return; // the user profile is the user's; DPAPI encrypts
        }
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw OwnerOnlyFailed(e.GetType().Name);
        }
        if ((File.GetUnixFileMode(file) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
        {
            throw OwnerOnlyFailed("the file system keeps a wider mode");
        }
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        int result;
        try
        {
            result = chmod(file, 0x180 /* 0600 */);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw OwnerOnlyFailed("no libc");
        }
        if (result != 0)
        {
            throw OwnerOnlyFailed("errno " + Marshal.GetLastWin32Error());
        }
#endif
    }

    /// <summary>The error when a file cannot be made owner-only: the kind of failure, never the path (it names the user).</summary>
    internal static IOException OwnerOnlyFailed(string kind) =>
        new($"A file only its owner may read could not be made ({kind}); nothing was written.");

    /// <summary>Best effort: tightens a folder (0700) or a file (0600) on macOS and Linux, and says nothing when it cannot.</summary>
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

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fadenstack.Agents;

/// <summary>
/// Where a stretch of work's places really lead on this device (sdk/38 §6; ASVS 5.0 5.3.2, file paths from untrusted
/// input; CWE-59, link following). The gateway matches a call's place against the grant's by their text: it cannot see that
/// a link, a junction or a mount point on this device leads somewhere else. Before a covered call runs, the device resolves
/// the place and each target as the operating system does -- every link on the way, in every part of the path (Windows:
/// <c>GetFinalPathNameByHandleW</c>; macOS and Linux: <c>realpath</c>) -- and the call runs under the grant only when the
/// real place lies in a real target. The end of a path that does not exist yet (a file the call will make) is taken as it
/// is, below the real path of the part that does. What cannot be resolved covers nothing: the person is asked.
/// <para>
/// Not caught: a hard link (one file under two names, both real), and a link that changes between this check and the
/// tool's own opening of the path.
/// </para>
/// </summary>
internal static class GrantPaths
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Windows and macOS compare names without case, as their file systems do by default; Linux by the bytes.
    private static readonly StringComparison Names =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly char Separator = Windows ? '\\' : '/';

    /// <summary>A path's parts resolved at most this often (a loop of links is cut short, as <c>ELOOP</c> does).</summary>
    private const int MaxSteps = 64;

    /// <summary>Whether places of <paramref name="type"/> may name a path on this device (an app by its executable's path).</summary>
    public static bool NamesPath(string type) => type is "document" or "folder" or "app";

    /// <summary>Whether <paramref name="value"/> is a full path on this device: not a URL, not relative.</summary>
    public static bool IsLocal(string value)
    {
        if (Windows)
        {
            return value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/'
                || value.StartsWith(@"\\", StringComparison.Ordinal);
        }
        return value.StartsWith("/", StringComparison.Ordinal);
    }

    /// <summary>Where <paramref name="path"/> really leads, every link resolved; null when that cannot be told.</summary>
    public static string? Real(string path) => Real(path, Windows ? FinalPath : RealPath);

    /// <summary>Asks the system where one path leads (<c>GetFinalPathNameByHandleW</c>, <c>realpath</c>).</summary>
    internal delegate string? Resolver(string path, out bool absent);

    /// <summary>The same, with the system call given (tests simulate a system without <c>libc</c>).</summary>
    internal static string? Real(string path, Resolver resolve)
    {
        string at;
        try
        {
            at = Trim(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
        var missing = new Stack<string>();
        for (var step = 0; step < MaxSteps; step++)
        {
            string? real;
            bool absent;
            try
            {
                real = resolve(at, out absent);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null; // no libc to ask (a minimal container): what cannot be resolved covers nothing, so the call asks
            }
            if (real is not null)
            {
                return missing.Count == 0 ? real : Path.Combine(new[] { real }.Concat(missing).ToArray());
            }
            // Only a name that is not there at all may be taken as it is: a link whose target is missing still leads there.
            if (!absent || Exists(at) || Path.GetDirectoryName(at) is not { } parent)
            {
                return null;
            }
            missing.Push(Path.GetFileName(at));
            at = parent;
        }
        return null;
    }

    /// <summary>
    /// Whether the real <paramref name="place"/> is the real <paramref name="target"/>, or for a folder lies under it.
    /// </summary>
    public static bool Lies(string type, string target, string place)
    {
        var (folder, named) = (Trim(target), Trim(place));
        if (string.Equals(folder, named, Names))
        {
            return true;
        }
        if (type != "folder")
        {
            return false;
        }
        var under = folder.EndsWith(Separator.ToString(), StringComparison.Ordinal) ? folder : folder + Separator;
        return named.StartsWith(under, Names);
    }

    /// <summary>
    /// A place that names no path on this device (a site, a desk, an app by its name, a document by its URL), matched by
    /// its text as the gateway does: documents, apps and desks by identity, folders by path, sites by origin.
    /// </summary>
    public static bool Named(string type, string target, string place)
    {
        switch (type)
        {
            case "folder":
                return !place.Replace('\\', '/').Split('/').Contains("..")
                    && (Slashed(place) + "/").StartsWith(Slashed(target) + "/", StringComparison.OrdinalIgnoreCase);
            case "document":
                return string.Equals(Slashed(place), Slashed(target), StringComparison.OrdinalIgnoreCase);
            case "site":
                return Origin(place) is { } origin && string.Equals(origin, Origin(target), StringComparison.Ordinal);
            default:
                return string.Equals(place, target, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Slashed(string value) => value.Replace('\\', '/').TrimEnd('/');

    private static string? Origin(string value) =>
        Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port)
            : null;

    // A root keeps its separator ("C:\", "/"); every other path loses a trailing one.
    private static string Trim(string path)
    {
        while (path.Length > 1 && (path[path.Length - 1] == '\\' || path[path.Length - 1] == '/') && Path.GetPathRoot(path) != path)
        {
            path = path.Substring(0, path.Length - 1);
        }
        return path;
    }

    // The name is there, as a link whose target is missing or as something that cannot be opened: the attributes are read
    // without following the last link (GetFileAttributesEx on Windows, lstat elsewhere).
    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    // ---- Windows ----

    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // read, write, delete: never in the way of the app that has it open
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // a folder opens too
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;

    // Opened for no access at all (attributes only), so a document the person has open in Word opens here too.
    private static string? FinalPath(string path, out bool absent)
    {
        absent = false;
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            absent = Marshal.GetLastWin32Error() is FileNotFound or PathNotFound;
            return null;
        }
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final.Substring(8)
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final.Substring(4)
            : final;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint length, uint flags);

    // ---- macOS and Linux ----

    private const int NoEntry = 2; // ENOENT

    private static string? RealPath(string path, out bool absent)
    {
        absent = false;
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            absent = Marshal.GetLastWin32Error() == NoEntry;
            return null;
        }
        try
        {
            return Marshal.PtrToStringAnsi(resolved); // UTF-8 on these systems
        }
        finally
        {
            free(resolved);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolvedPath);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}

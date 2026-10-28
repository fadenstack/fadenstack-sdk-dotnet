using System.Runtime.InteropServices;
using Fadenstack.Auth;

namespace Fadenstack.Storage;

/// <summary>
/// Where the SDK keeps things on a device: always in the OS user's own folders, never in a machine-wide one, and
/// personal content apart per Fadenstack user (<see cref="User"/>). The one place that knows these paths.
/// </summary>
public static class FadenstackPaths
{
    private const string Name = "Fadenstack";

    internal enum Os
    {
        Windows,
        MacOs,
        Unix,
    }

    /// <summary>The three roots of one device and user.</summary>
    internal sealed record Roots(string Data, string Settings, string Logs);

    private static readonly Lazy<Roots> Real = new(() => Resolve(CurrentOs(), Environment.GetEnvironmentVariable, Environment.GetFolderPath));

    /// <summary>The tests point all roots at a temporary folder (<c>data</c>, <c>settings</c> and <c>logs</c> below it).</summary>
    internal static string? RootForTests { get; set; }

    private static Roots Current => RootForTests is { } root
        ? new Roots(Path.Combine(root, "data"), Path.Combine(root, "settings"), Path.Combine(root, "logs"))
        : Real.Value;

    /// <summary>Personal data: sessions, approvals, tokens. <c>%LOCALAPPDATA%\Fadenstack</c> on Windows (local, never roamed).</summary>
    public static string DataRoot => Current.Data;

    /// <summary>Preferences, which hold no personal content. <c>%APPDATA%\Fadenstack</c> on Windows.</summary>
    public static string SettingsRoot => Current.Settings;

    /// <summary>Logs (<see cref="Logging.FadenstackFileLoggerProvider"/>); <c>DataRoot\logs</c> on Windows.</summary>
    public static string LogRoot => Current.Logs;

    /// <summary>A folder under <see cref="DataRoot"/>, created, readable by its owner only (Unix).</summary>
    public static string Data(params string[] parts) => Ensure(DataRoot, parts);

    /// <summary>A folder of one user's own content (<c>DataRoot/users/&lt;user&gt;/...</c>), created, owner only.</summary>
    public static string User(string userId, params string[] parts) =>
        Ensure(DataRoot, new[] { "users", SafeName(userId) }.Concat(parts));

    /// <summary>The log folder, or one user's below it; created, owner only.</summary>
    public static string Logs(string? userId) =>
        Ensure(LogRoot, userId is null ? Array.Empty<string>() : new[] { SafeName(userId) });

    /// <summary>
    /// A log folder a host chose: <see cref="LogRoot"/> itself or a folder below it, created, owner only. Anywhere else is
    /// refused -- logs stay in the OS user's own folder, never in a machine-wide one.
    /// </summary>
    public static string LogFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        var root = Path.GetFullPath(LogRoot);
        if (!IsWithin(root, full))
        {
            throw new ArgumentException($"A log folder must lie under the user's log root ({root}).", nameof(folder));
        }
        Own(root);
        Own(full);
        return full;
    }

    /// <summary>One user's folder (<c>DataRoot/users/&lt;user&gt;</c>), not created.</summary>
    internal static string UserFolder(string userId) => Path.Combine(DataRoot, "users", SafeName(userId));

    /// <summary>
    /// Deletes a folder with all it holds, if it exists; true when it did. Only a folder strictly below <see cref="DataRoot"/>
    /// is deleted -- anything else, the root itself included, is refused.
    /// </summary>
    internal static bool DeleteUnderData(string folder)
    {
        var full = Path.GetFullPath(folder);
        var root = Path.GetFullPath(DataRoot);
        if (!IsWithin(root, full) || string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar), Comparison))
        {
            throw new ArgumentException($"Only a folder below the data root ({root}) can be deleted.", nameof(folder));
        }
        if (!Directory.Exists(full))
        {
            return false;
        }
        Directory.Delete(full, recursive: true);
        return true;
    }

    /// <summary>Whether <paramref name="full"/> is <paramref name="root"/> or lies below it (both full paths).</summary>
    private static bool IsWithin(string root, string full)
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        return (full.TrimEnd(Path.DirectorySeparatorChar) + separator).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + separator, Comparison);
    }

    private static StringComparison Comparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A name that is safe as one file or folder name: invalid characters and dots become underscores.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray());
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>The roots for an OS, given its environment and special folders (the real ones, or a test's).</summary>
    internal static Roots Resolve(Os os, Func<string, string?> env, Func<Environment.SpecialFolder, string> folder)
    {
        var profile = folder(Environment.SpecialFolder.UserProfile);
        if (os == Os.Windows)
        {
            var local = Folder(folder, Environment.SpecialFolder.LocalApplicationData, profile, "AppData", "Local");
            var roaming = Folder(folder, Environment.SpecialFolder.ApplicationData, profile, "AppData", "Roaming");
            var data = Path.Combine(local, Name);
            return new Roots(data, Path.Combine(roaming, Name), Path.Combine(data, "logs"));
        }
        var home = env("HOME") is { Length: > 0 } h ? h : profile;
        if (os == Os.MacOs)
        {
            var data = Path.Combine(home, "Library", "Application Support", Name);
            return new Roots(data, data, Path.Combine(home, "Library", "Logs", Name));
        }
        return new Roots(
            Path.Combine(Xdg(env, "XDG_DATA_HOME", home, ".local", "share"), Name),
            Path.Combine(Xdg(env, "XDG_CONFIG_HOME", home, ".config"), Name),
            Path.Combine(Xdg(env, "XDG_STATE_HOME", home, ".local", "state"), Name, "logs"));
    }

    private static Os CurrentOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Os.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Os.MacOs
        : Os.Unix;

    private static string Folder(Func<Environment.SpecialFolder, string> folder, Environment.SpecialFolder which, string profile, params string[] fallback)
    {
        var path = folder(which);
        return string.IsNullOrEmpty(path) ? Path.Combine(new[] { profile }.Concat(fallback).ToArray()) : path;
    }

    /// <summary>An XDG base directory: the variable when it is an absolute path (the specification ignores others), else the default below home.</summary>
    private static string Xdg(Func<string, string?> env, string variable, string home, params string[] fallback) =>
        env(variable) is { Length: > 0 } value && (value[0] == '/' || Path.IsPathRooted(value))
            ? value
            : Path.Combine(new[] { home }.Concat(fallback).ToArray());

    private static string Ensure(string root, IEnumerable<string> parts)
    {
        var path = root;
        Own(path);
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            Own(path);
        }
        return path;
    }

    private static void Own(string directory)
    {
        Directory.CreateDirectory(directory);
        SecretProtection.OwnerOnly(directory, directory: true);
    }
}

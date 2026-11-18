using System.Security.Cryptography;

namespace Fadenstack.Mcp;

/// <summary>
/// The program of a local MCP server as it was when the person added it (sdk/51 5.5): the full path of the file its command
/// names and that file's SHA-256, and, when the first argument is a file (a script such as <c>server.py</c> or <c>index.js</c>),
/// that file's path and SHA-256 too. The SDK checks it before every start (<see cref="McpProgramPinner.Compare"/>): a server whose
/// program is a different file, a changed file or a missing file does not start until the person says so. TS: none (the browser
/// reaches MCP servers over http only and starts no program).
/// <para>
/// ASVS 5.0 V15 (secure coding and architecture: integrity of what is run) and V13 (configuration). A package that a runner
/// fetches (<c>npx @scope/server</c>) is not a file here and cannot be pinned: only the runner is.
/// </para>
/// </summary>
public sealed record McpProgramPin(string Path, string Sha256, string? ScriptPath, string? ScriptSha256, DateTimeOffset PinnedAt)
{
    /// <summary>Whether the file names are the same (no case on Windows and macOS file systems, which keep it apart in name only).</summary>
    internal static bool SamePath(string? a, string? b) =>
        string.Equals(a, b, McpProgramPinner.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>What differs between a pinned program and the one the command names now.</summary>
public enum McpProgramChange
{
    /// <summary>The same files.</summary>
    None,

    /// <summary>A file has other content than when it was pinned.</summary>
    Hash,

    /// <summary>The command now names another file (or a script appeared or is another one).</summary>
    Path,

    /// <summary>The program or its script is not there.</summary>
    Missing,
}

/// <summary>Pins the program of a local server and compares it with what the command names later.</summary>
public static class McpProgramPinner
{
    internal static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>
    /// Runners: a program that fetches or starts what its arguments name. For these the person is told that only the runner (and a
    /// script among the arguments) is checked, never the package it fetches.
    /// </summary>
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "npx", "npm", "pnpm", "pnpx", "yarn", "bun", "bunx", "deno", "node", "uv", "uvx", "pipx", "python", "python3", "py",
        "docker", "podman", "dotnet", "dnx", "java",
    };

    /// <summary>
    /// The pin of a program as it is now; null when the command names no file that can be read (it is not found on the path, or it
    /// is a folder). A <c>PATH</c> in the entry's environment is the one searched.
    /// </summary>
    public static McpProgramPin? Pin(McpServerEntry entry) =>
        entry is null ? throw new ArgumentNullException(nameof(entry)) : !entry.IsProgram ? null
        : Observe(entry.Command!, entry.Arguments, entry.WorkingFolder, PathOf(entry.Environment), DateTimeOffset.UtcNow);

    /// <summary>The program as the command names it now, in the shape of a pin; null when it cannot be found or read.</summary>
    internal static McpProgramPin? Observe(string command, IReadOnlyList<string> arguments, string? workingFolder, string? pathVariable,
        DateTimeOffset now) =>
        Observe(command, arguments, workingFolder, pathVariable ?? Environment.GetEnvironmentVariable("PATH"),
            IsWindows ? Environment.GetEnvironmentVariable("PATHEXT") : null, IsWindows, now);

    /// <summary>The same with the path, the extensions and the platform given (tests).</summary>
    internal static McpProgramPin? Observe(string command, IReadOnlyList<string> arguments, string? workingFolder, string? pathVariable,
        string? pathExt, bool windows, DateTimeOffset now) =>
        ObserveResolved(Resolve(command, workingFolder, pathVariable, pathExt, windows), arguments, workingFolder, now);

    /// <summary>
    /// The pin of a program that was already resolved (null: it was not found). The start and the pin use one resolution, so the
    /// file that is hashed is the file that starts.
    /// </summary>
    internal static McpProgramPin? ObserveResolved(string? program, IReadOnlyList<string> arguments, string? workingFolder, DateTimeOffset now)
    {
        if (program is null || Hash(program) is not { } sha)
        {
            return null;
        }
        string? script = null;
        string? scriptSha = null;
        if (arguments.Count > 0 && arguments[0].Length > 0)
        {
            var candidate = Absolute(arguments[0], workingFolder);
            if (candidate is not null && File.Exists(candidate))
            {
                script = candidate;
                scriptSha = Hash(candidate);
                if (scriptSha is null)
                {
                    return null; // a script that cannot be read is no script to trust
                }
            }
        }
        return new McpProgramPin(program, sha, script, scriptSha, now);
    }

    /// <summary>
    /// What differs between <paramref name="pin"/> and <paramref name="observed"/> (the program as it is now; null: not found or not
    /// readable). The path of the program and of a script is checked before its content.
    /// </summary>
    public static McpProgramChange Compare(McpProgramPin pin, McpProgramPin? observed)
    {
        if (pin is null)
        {
            throw new ArgumentNullException(nameof(pin));
        }
        if (observed is null)
        {
            return McpProgramChange.Missing;
        }
        if (!McpProgramPin.SamePath(pin.Path, observed.Path))
        {
            return McpProgramChange.Path;
        }
        if (pin.ScriptPath is null != observed.ScriptPath is null)
        {
            // The pin had a script that is gone (the first argument is no file now), or a file appeared where there was none.
            return pin.ScriptPath is not null ? McpProgramChange.Missing : McpProgramChange.Path;
        }
        if (pin.ScriptPath is not null && !McpProgramPin.SamePath(pin.ScriptPath, observed.ScriptPath))
        {
            return McpProgramChange.Path;
        }
        return string.Equals(pin.Sha256, observed.Sha256, StringComparison.Ordinal)
            && string.Equals(pin.ScriptSha256, observed.ScriptSha256, StringComparison.Ordinal)
            ? McpProgramChange.None
            : McpProgramChange.Hash;
    }

    /// <summary>
    /// The program is a runner that fetches what its arguments name, and no script among them is pinned: the person is told that
    /// only the runner is checked, because the package it fetches can change.
    /// </summary>
    public static bool OnlyRunnerChecked(McpServerEntry entry)
    {
        if (entry is null || !entry.IsProgram || entry.Arguments.Count == 0 || entry.ProgramPin?.ScriptPath is not null)
        {
            return false;
        }
        var name = System.IO.Path.GetFileName(entry.Command!.TrimEnd('/', '\\').Replace('\\', '/'));
        var dot = name.LastIndexOf('.');
        return Runners.Contains(dot > 0 ? name.Substring(0, dot) : name);
    }

    /// <summary>
    /// The file <paramref name="command"/> starts, with this machine's <c>PATH</c> (or <paramref name="pathVariable"/>) and, on
    /// Windows, <c>PATHEXT</c>. The one resolution of a start: the policy, the pin and the process all use its result.
    /// </summary>
    internal static string? Resolve(string command, string? workingFolder, string? pathVariable) =>
        Resolve(command, workingFolder, pathVariable ?? Environment.GetEnvironmentVariable("PATH"),
            IsWindows ? Environment.GetEnvironmentVariable("PATHEXT") : null, IsWindows);

    /// <summary>
    /// The full path of the file <paramref name="command"/> starts, the way the host starts it: a command with a folder in it is that
    /// file (a relative one from the working folder, else from the app's), any other is looked for in the folders of the path, and
    /// on Windows with the extensions of <c>PATHEXT</c> when the name has none that exists. Null when there is no such file.
    /// </summary>
    internal static string? Resolve(string command, string? workingFolder, string? pathVariable, string? pathExt, bool windows)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        IReadOnlyList<string> extensions = windows ? Extensions(pathExt) : Array.Empty<string>();
        if (command.IndexOfAny(['/', '\\']) >= 0 || System.IO.Path.IsPathRooted(command))
        {
            var full = Absolute(command, workingFolder);
            return full is null ? null : FirstFile(full, extensions, windows);
        }
        foreach (var folder in (pathVariable ?? "").Split(System.IO.Path.PathSeparator))
        {
            var trimmed = folder.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }
            try
            {
                if (FirstFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(trimmed, command)), extensions, windows) is { } found)
                {
                    return found;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // a folder of the path that is no folder name
            }
        }
        return null;
    }

    private static string? FirstFile(string candidate, IReadOnlyList<string> extensions, bool windows)
    {
        if (windows)
        {
            // A name that already ends in an extension of PATHEXT is that file; otherwise each extension is tried, then the bare name.
            var ext = System.IO.Path.GetExtension(candidate);
            if (ext.Length > 0 && extensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                return candidate;
            }
            foreach (var added in extensions)
            {
                if (File.Exists(candidate + added))
                {
                    return candidate + added;
                }
            }
        }
        return File.Exists(candidate) ? candidate : null;
    }

    private static IReadOnlyList<string> Extensions(string? pathExt)
    {
        var list = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt!).Split(';')
            .Select(e => e.Trim()).Where(e => e.Length > 1 && e[0] == '.').ToList();
        return list;
    }

    private static string? PathOf(IReadOnlyList<McpSecretPair> environment)
    {
        foreach (var pair in environment)
        {
            if (string.Equals(pair.Name, "PATH", IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }
        return null;
    }

    /// <summary>The <c>PATH</c> in a server's environment, if it sets one.</summary>
    internal static string? PathOf(IDictionary<string, string?>? environment)
    {
        if (environment is null)
        {
            return null;
        }
        foreach (var pair in environment)
        {
            if (string.Equals(pair.Key, "PATH", IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }
        return null;
    }

    // A relative name is the working folder's (the program starts there), else the app's.
    private static string? Absolute(string path, string? workingFolder)
    {
        try
        {
            var folder = !string.IsNullOrWhiteSpace(workingFolder) && System.IO.Path.IsPathRooted(workingFolder) ? workingFolder! : null;
            return System.IO.Path.GetFullPath(folder is null || System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(folder, path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of a file in lower case hex; null when it cannot be read.</summary>
    internal static string? Hash(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

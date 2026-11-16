using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Fadenstack.Models;

namespace Fadenstack.Agents;

/// <summary>
/// A tool source (the local MCP module) that knows the programs of the local servers it runs, for the device block of the manifest
/// (sdk/51 8.3). The name is the server's label, the program is the file name only (never its folder), the hash is the pin
/// (<c>Fadenstack.Mcp.McpProgramPin</c>); a server over http has none. TS: none, a browser starts no program.
/// </summary>
public interface IDeviceProgramSource
{
    /// <summary>The programs of the enabled local servers that have a pin, as the manifest names them.</summary>
    IReadOnlyList<ManifestMcpProgram> McpPrograms();
}

/// <summary>Why an entry is left out of the device block (the log says the kind of entry and this, never the entry).</summary>
internal enum DeviceDropReason
{
    NameInvalid,
    VersionTooLong,
    ProgramInvalid,
    Sha256Invalid,
    TooMany,
}

/// <summary>
/// Builds the manifest's <c>device</c> block, cut to the server's limits (<see cref="DeviceLimits"/>): the operating system and
/// the app's version cut, each plugin and program checked (one that does not fit is left out and reported to the caller by kind and
/// reason), the SHA-256 in lower case, at most 100 of each. Nothing else is ever put in: no path, user name, argument or
/// environment value. TS: <c>buildDevice</c>.
/// </summary>
internal static class DeviceReport
{
    private static readonly Regex Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The operating system as the runtime describes it with the architecture (<c>Microsoft Windows 10.0.26100 X64</c>), cut to
    /// 100 characters.
    /// </summary>
    internal static string DescribeOs()
    {
        string text;
        try
        {
            text = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException)
        {
            text = "unknown";
        }
        return Cut(text, DeviceLimits.Os);
    }

    internal static ManifestDevice Build(string os, string? appVersion, IEnumerable<ManifestPlugin> plugins, IEnumerable<ManifestMcpProgram> programs,
        Action<string, DeviceDropReason> dropped)
    {
        var keptPlugins = new List<ManifestPlugin>();
        foreach (var plugin in plugins)
        {
            if (PluginProblem(plugin) is { } problem)
            {
                dropped("plugin", problem);
            }
            else if (keptPlugins.Count >= DeviceLimits.Entries)
            {
                dropped("plugin", DeviceDropReason.TooMany);
            }
            else
            {
                keptPlugins.Add(plugin with { Sha256 = plugin.Sha256.ToLowerInvariant() });
            }
        }
        var keptPrograms = new List<ManifestMcpProgram>();
        foreach (var program in programs)
        {
            if (ProgramProblem(program) is { } problem)
            {
                dropped("mcp_program", problem);
            }
            else if (keptPrograms.Count >= DeviceLimits.Entries)
            {
                dropped("mcp_program", DeviceDropReason.TooMany);
            }
            else
            {
                keptPrograms.Add(program with { Sha256 = program.Sha256.ToLowerInvariant() });
            }
        }
        return new ManifestDevice
        {
            Os = Cut(os, DeviceLimits.Os),
            AppVersion = Cut(appVersion ?? "", DeviceLimits.AppVersion),
            Plugins = keptPlugins,
            McpPrograms = keptPrograms,
        };
    }

    /// <summary>Why a plugin the app declared does not fit the server's limits; null when it does.</summary>
    internal static DeviceDropReason? PluginProblem(ManifestPlugin plugin)
    {
        if (plugin is null || string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > DeviceLimits.Name)
        {
            return DeviceDropReason.NameInvalid;
        }
        if (plugin.Version is null || plugin.Version.Length > DeviceLimits.Version)
        {
            return DeviceDropReason.VersionTooLong;
        }
        return plugin.Sha256 is null || !Hex.IsMatch(plugin.Sha256.ToLowerInvariant()) ? DeviceDropReason.Sha256Invalid : null;
    }

    /// <summary>Why an MCP program entry does not fit the server's limits; null when it does.</summary>
    internal static DeviceDropReason? ProgramProblem(ManifestMcpProgram program)
    {
        if (program is null || string.IsNullOrWhiteSpace(program.Name) || program.Name.Length > DeviceLimits.Name)
        {
            return DeviceDropReason.NameInvalid;
        }
        if (string.IsNullOrEmpty(program.Program) || program.Program.Length > DeviceLimits.Program)
        {
            return DeviceDropReason.ProgramInvalid;
        }
        return program.Sha256 is null || !Hex.IsMatch(program.Sha256.ToLowerInvariant()) ? DeviceDropReason.Sha256Invalid : null;
    }

    /// <summary>The file name of a path of either kind, never its folder (a folder can hold the user's name).</summary>
    internal static string FileNameOf(string path)
    {
        var at = path.LastIndexOfAny(['/', '\\']);
        return at < 0 ? path : path.Substring(at + 1);
    }

    private static string Cut(string text, int max)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            clean.Append(char.IsControl(c) ? ' ' : c);
        }
        var trimmed = clean.ToString().Trim();
        return trimmed.Length > max ? trimmed.Substring(0, max) : trimmed;
    }

    internal static string Describe(DeviceDropReason reason) => reason switch
    {
        DeviceDropReason.NameInvalid => "name_invalid",
        DeviceDropReason.VersionTooLong => "version_too_long",
        DeviceDropReason.ProgramInvalid => "program_invalid",
        DeviceDropReason.Sha256Invalid => "sha256_invalid",
        _ => "too_many",
    };
}

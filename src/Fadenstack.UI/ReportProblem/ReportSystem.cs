using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Fadenstack.UI;

/// <summary>
/// The "System" line of a report: the operating system's family and version, nothing that names the machine or the person
/// ("Windows 10.0.26300", "macOS 15.1", "Linux 6.8").
/// </summary>
public static class ReportSystem
{
    private static readonly Regex WindowsPattern = new(@"^Microsoft Windows\s+(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant);
    private static readonly Regex DarwinPattern = new(@"^Darwin\s+(\d+)\.(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex OtherPattern = new(@"^([A-Za-z][A-Za-z]*(?: [A-Za-z]+)*)\s+(\d+)\.(\d+)", RegexOptions.CultureInvariant);

    /// <summary>This computer's: <see cref="RuntimeInformation.OSDescription"/>, reduced; null when it cannot be read.</summary>
    public static string? Current()
    {
        try
        {
            return Of(RuntimeInformation.OSDescription);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The family and version of an OS description. Windows keeps its full version, Linux and the other Unixes their first two
    /// numbers, macOS is named from its Darwin kernel (24.1 is macOS 15.1). Null for a description it cannot read.
    /// </summary>
    public static string? Of(string? description)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (WindowsPattern.Match(text) is { Success: true } windows)
        {
            return "Windows " + windows.Groups[1].Value;
        }
        if (DarwinPattern.Match(text) is { Success: true } darwin)
        {
            var major = int.Parse(darwin.Groups[1].Value, CultureInfo.InvariantCulture);
            var minor = int.Parse(darwin.Groups[2].Value, CultureInfo.InvariantCulture);
            return major switch
            {
                >= 25 => $"macOS {major + 1}.{minor}", // Darwin 25 is macOS 26
                >= 20 => $"macOS {major - 9}.{minor}", // Darwin 20 is macOS 11
                >= 5 => $"macOS 10.{major - 4}", // Darwin 19 is macOS 10.15
                _ => null,
            };
        }
        if (OtherPattern.Match(text) is { Success: true } other)
        {
            return other.Groups[1].Value + " " + other.Groups[2].Value + "." + other.Groups[3].Value;
        }
        return null;
    }
}

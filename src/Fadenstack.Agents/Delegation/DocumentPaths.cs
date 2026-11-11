using System.Runtime.InteropServices;

namespace Fadenstack.Agents.Delegation;

/// <summary>Tells whether two texts name the same document: a full path, or the name an app shows for an unsaved one.</summary>
internal static class DocumentPaths
{
    /// <summary>
    /// Rooted paths are compared as full paths (without regard to case on Windows); anything else, such as
    /// <c>Document1</c>, as a name without regard to case.
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }
        a = a!.Trim();
        b = b!.Trim();
        if (!Rooted(a) || !Rooted(b))
        {
            return !Rooted(a) && !Rooted(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Full(a), Full(b), comparison);
    }

    private static bool Rooted(string text)
    {
        try
        {
            return Path.IsPathRooted(text);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

using Fadenstack.Storage;

namespace Fadenstack.UI;

/// <summary>
/// What a skin's default link hook opens (<c>MarkdownText.OpenLink</c>): web links, as before; <c>mailto:</c>, for "Report a problem"'s
/// "Write mail"; and the SDK's own log folder as a <c>file:</c> link, for "Open the log folder". Any other <c>file:</c> link, and every
/// other scheme, is left alone, so a link in an answer cannot start a program. A host that replaces the hook decides for itself.
/// </summary>
public static class PanelLinkPolicy
{
    /// <summary>Whether the default hook opens <paramref name="url"/>.</summary>
    public static bool OpensByDefault(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return false;
        }
        if (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeMailto)
        {
            return true;
        }
        return url.IsFile && IsLogFolder(url);
    }

    /// <summary>What to hand the operating system for <paramref name="url"/>: the folder's own path for a <c>file:</c> link, else its address.</summary>
    public static string Target(Uri url) =>
        url.IsFile ? url.LocalPath : url.Scheme == Uri.UriSchemeMailto ? url.OriginalString : url.AbsoluteUri;

    private static bool IsLogFolder(Uri url)
    {
        try
        {
            static string Plain(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return !url.IsUnc && string.Equals(Plain(url.LocalPath), Plain(FadenstackPaths.LogRoot), comparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            return false;
        }
    }
}

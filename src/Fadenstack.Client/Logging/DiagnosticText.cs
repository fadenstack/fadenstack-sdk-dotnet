using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Fadenstack.Logging;

/// <summary>
/// Helpers for text a person copies out of the app to send someone (a "Report a problem" sheet): details of an error, a log line.
/// </summary>
public static class DiagnosticText
{
    /// <summary>
    /// Replaces the user's profile folder (<see cref="Environment.SpecialFolder.UserProfile"/>) in <paramref name="text"/> with
    /// <c>%USERPROFILE%</c> on Windows and <c>~</c> elsewhere, so that an exception message such as
    /// <c>Could not find file 'C:\Users\anna\Documents\plan.docx'</c> says <c>'%USERPROFILE%\Documents\plan.docx'</c> and no longer names the person.
    /// On Windows the match ignores the letter case, takes both slash forms (<c>C:/Users/anna/...</c>) and the <c>\\?\</c> prefix, and also
    /// masks <c>&lt;drive&gt;:\Users\&lt;name&gt;</c> of the same user name on another drive or written in another way. A path of another
    /// user is left as it is, and so is a longer name (<c>C:\Users\anna2</c>). The rest of the path keeps its slashes.
    /// </summary>
    /// <param name="text">The text to mask; not changed when the profile folder is not known.</param>
    public static string MaskUserPaths(string text) =>
        MaskUserPaths(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>The same for a given profile folder and OS (tests, and a host that formats for another machine).</summary>
    internal static string MaskUserPaths(string text, string? profile, bool windows)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        profile = profile?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(profile) || text.Length == 0)
        {
            return text;
        }
        var replacement = windows ? "%USERPROFILE%" : "~";
        var options = RegexOptions.CultureInvariant | (windows ? RegexOptions.IgnoreCase : RegexOptions.None);
        var separators = windows ? new[] { '\\', '/' } : new[] { '/' };
        var parts = profile!.Split(separators);
        var join = windows ? @"[\\/]+" : "/+";
        // A match starts at a path's start (not inside a word or a longer path) and ends at the folder's end (not in a longer name).
        const string before = @"(?<![\w.\-])";
        const string after = @"(?![\w\-]|\.\w)";
        var literal = string.Join(join, parts.Select(Regex.Escape));
        var pattern = (windows ? @"(?:\\\\\?\\)?" : "") + literal;
        var masked = Regex.Replace(text, before + pattern + after, replacement, options);
        if (windows)
        {
            var name = parts[parts.Length - 1];
            if (name.Length > 0)
            {
                masked = Regex.Replace(masked, before + @"(?:\\\\\?\\)?[A-Za-z]:[\\/]+Users[\\/]+" + Regex.Escape(name) + after, replacement, options);
            }
        }
        return masked;
    }
}

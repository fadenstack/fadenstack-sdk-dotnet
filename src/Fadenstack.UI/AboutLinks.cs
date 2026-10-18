using System.Text.RegularExpressions;

namespace Fadenstack.UI;

/// <summary>
/// The panel's links to fadenstack.com and the organisation's Help. A link to fadenstack.com carries only
/// <c>ref</c> (where it was opened: <c>&lt;app&gt;-panel</c>, <c>&lt;app&gt;-about</c>) and <c>v</c> (the SDK's
/// major.minor): never a user, organisation, server, agent or app name, nor anything a person typed.
/// </summary>
public static class AboutLinks
{
    /// <summary>The website's home page.</summary>
    public const string Website = "https://fadenstack.com/";

    /// <summary>The developer docs (the SDKs').</summary>
    public const string DeveloperDocs = "https://developers.fadenstack.com/";

    /// <summary>The <c>&lt;app&gt;</c> of a link's <c>ref</c> when the host names none, or one that is not allowed.</summary>
    public const string DefaultSource = "sdk";

    /// <summary>The longest <c>&lt;app&gt;</c> a host may name.</summary>
    public const int MaxSourceLength = 20;

    private static readonly Regex SourcePattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex MajorMinorPattern = new(@"^v?(\d+)\.(\d+)(?:[.\-+]|\z)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The host's <c>&lt;app&gt;</c> (<c>office</c>, <c>chrome</c>): lower-case letters and digits, words joined by single
    /// hyphens, at most <see cref="MaxSourceLength"/> characters. Anything else is <see cref="DefaultSource"/>.
    /// </summary>
    public static string Source(string? refSource) =>
        refSource is { Length: > 0 and <= MaxSourceLength } && SourcePattern.IsMatch(refSource) ? refSource : DefaultSource;

    /// <summary>The first two numbers of a version (<c>0.2.0-rc.3</c> is <c>0.2</c>); null when there are not two.</summary>
    public static string? MajorMinor(string? version)
    {
        var match = version is null ? null : MajorMinorPattern.Match(version.Trim());
        return match is { Success: true } ? $"{Number(match.Groups[1].Value)}.{Number(match.Groups[2].Value)}" : null;
    }

    /// <summary>The footer's link: the website, <c>ref=&lt;app&gt;-panel</c>.</summary>
    public static Uri PanelWebsite(string? refSource, string? sdkVersion) => Tagged(Website, Source(refSource) + "-panel", sdkVersion);

    /// <summary>About's link to the website: <c>ref=&lt;app&gt;-about</c>.</summary>
    public static Uri AboutWebsite(string? refSource, string? sdkVersion) => Tagged(Website, Source(refSource) + "-about", sdkVersion);

    /// <summary>About's link to the developer docs: <c>ref=&lt;app&gt;-about</c>.</summary>
    public static Uri AboutDeveloperDocs(string? refSource, string? sdkVersion) => Tagged(DeveloperDocs, Source(refSource) + "-about", sdkVersion);

    /// <summary>
    /// A fadenstack.com page with <c>?ref=</c> and, when the version has two numbers, <c>&amp;v=</c>.
    /// <paramref name="refValue"/> must be one of the sources (a <see cref="Source"/> with its place).
    /// </summary>
    public static Uri Tagged(string page, string refValue, string? sdkVersion)
    {
        var query = "?ref=" + Uri.EscapeDataString(refValue);
        if (MajorMinor(sdkVersion) is { } v)
        {
            query += "&v=" + v;
        }
        return new Uri(page + query, UriKind.Absolute);
    }

    /// <summary>
    /// The organisation's own Help: <c>&lt;server&gt;/help/</c>, with no <c>ref</c>. Null for no server or one that is not
    /// http(s).
    /// </summary>
    public static Uri? Help(Uri? server) =>
        Base(server) is { } root ? new Uri(root, "help/") : null;

    /// <summary>The server's address to show (never sent): scheme, host, port and path, without a sign-in, query or slash at the end.</summary>
    public static string ServerText(Uri? server) =>
        Base(server) is { } root ? root.GetLeftPart(UriPartial.Path).TrimEnd('/') : "";

    /// <summary>The server's address with a slash at the end, without a sign-in, query or fragment; null when it is not http(s).</summary>
    private static Uri? Base(Uri? server)
    {
        if (server is not { IsAbsoluteUri: true } || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }
        var builder = new UriBuilder(server) { UserName = "", Password = "", Query = "", Fragment = "" };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }
        return builder.Uri;
    }

    /// <summary>A number without its leading zeros ("05" is 5), so <c>v</c> has one spelling.</summary>
    private static string Number(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}

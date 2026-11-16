using System.Globalization;
using System.Text;

namespace Fadenstack.UI.Services;

/// <summary>
/// HTML as Windows keeps it on the clipboard: the <c>HTML Format</c> (CF_HTML). A header names where the HTML and the
/// fragment start and end, in bytes of the UTF-8 text that follows it; Word, Outlook, Gmail in a browser and the rest read
/// the fragment between <c>&lt;!--StartFragment--&gt;</c> and <c>&lt;!--EndFragment--&gt;</c>. A skin's clipboard puts
/// <see cref="ToCfHtml"/> there with the plain text beside it; macOS and Linux take the HTML as it is.
/// </summary>
public static class ClipboardHtml
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    /// <summary>The CF_HTML text of <paramref name="fragment"/>: the header with its offsets, then the page around the fragment.</summary>
    public static string ToCfHtml(string fragment)
    {
        if (fragment is null)
        {
            throw new ArgumentNullException(nameof(fragment));
        }
        // The header's size does not depend on the numbers: they are ten digits each.
        const string template = "Version:0.9\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n";
        string Header(int a, int b, int c, int d) => string.Format(CultureInfo.InvariantCulture, template, Pad(a), Pad(b), Pad(c), Pad(d));
        var before = "<html>\r\n<body>\r\n" + StartMarker;
        var after = EndMarker + "\r\n</body>\r\n</html>";
        var headerLength = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return Header(startHtml, endHtml, startFragment, endFragment) + before + fragment + after;
    }

    /// <summary>The fragment of CF_HTML text, read by its header's offsets as a program reading the clipboard does; null when the header does not hold.</summary>
    public static string? FromCfHtml(string cfHtml)
    {
        var bytes = Encoding.UTF8.GetBytes(cfHtml);
        var start = Offset(cfHtml, "StartFragment:");
        var end = Offset(cfHtml, "EndFragment:");
        var all = Offset(cfHtml, "EndHTML:");
        if (start is null || end is null || all is null || start < 0 || end < start || end > bytes.Length || all > bytes.Length)
        {
            return null;
        }
        return Encoding.UTF8.GetString(bytes, start.Value, end.Value - start.Value);
    }

    private static int? Offset(string cfHtml, string name)
    {
        var at = cfHtml.IndexOf(name, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var from = at + name.Length;
        var stop = cfHtml.IndexOf('\r', from);
        return stop > from && int.TryParse(cfHtml.Substring(from, stop - from), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string Pad(int offset) => offset.ToString("D10", CultureInfo.InvariantCulture);
}

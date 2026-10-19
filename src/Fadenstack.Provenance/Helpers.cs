using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Http;
using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>Media types as the pipeline sorts them: text, pictures, and code and data that need no mark.</summary>
internal static class MediaTypes
{
    /// <summary>The type without parameters, in lower case: <c>text/html; charset=utf-8</c> is <c>text/html</c>.</summary>
    public static string Bare(string mediaType)
    {
        var semicolon = mediaType.IndexOf(';');
        return (semicolon >= 0 ? mediaType.Substring(0, semicolon) : mediaType).Trim().ToLowerInvariant();
    }

    public static bool Is(string mediaType, string expected) => Bare(mediaType) == expected;

    public static bool IsImage(string mediaType) => Bare(mediaType).StartsWith("image/", StringComparison.Ordinal);

    public static bool IsText(string mediaType)
    {
        var bare = Bare(mediaType);
        return bare.StartsWith("text/", StringComparison.Ordinal) || bare == "application/xhtml+xml";
    }

    public static bool IsHtml(string mediaType) => Is(mediaType, "text/html") || Is(mediaType, "application/xhtml+xml");

    /// <summary>
    /// Source code, scripts and structured data (JSON, XML, YAML, SQL, CSS): exempt from marking. <c>text/x-*</c> (but
    /// <c>text/x-markdown</c>), <c>+json</c>, <c>+xml</c> (but XHTML), and the types in <see cref="Code"/>; a picture
    /// never is (<c>image/svg+xml</c>). The TypeScript SDK's <c>isCode</c> is the same.
    /// </summary>
    public static bool IsCode(string mediaType)
    {
        var bare = Bare(mediaType);
        if (bare.StartsWith("image/", StringComparison.Ordinal))
        {
            return false;
        }
        return (bare.StartsWith("text/x-", StringComparison.Ordinal) && bare != "text/x-markdown")
            || bare.EndsWith("+json", StringComparison.Ordinal)
            || (bare.EndsWith("+xml", StringComparison.Ordinal) && bare != "application/xhtml+xml")
            || Code.Contains(bare);
    }

    private static readonly HashSet<string> Code = new(StringComparer.Ordinal)
    {
        "application/json", "application/xml", "text/xml", "application/javascript", "text/javascript", "application/x-yaml",
        "application/yaml", "text/yaml", "application/toml", "application/sql", "text/css", "application/x-sh", "application/x-powershell",
    };
}

/// <summary>About how many tokens a text is, without the model's tokenizer.</summary>
internal static class TokenEstimate
{
    /// <summary>One token per CJK character and one per four other characters, as the gateway estimates.</summary>
    public static int Of(string text)
    {
        var cjk = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (IsCjk(c))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }
        return cjk + (other + 3) / 4;
    }

    internal static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿'
            or >= '가' and <= '힯' or >= '豈' and <= '﫿';
}

/// <summary>
/// Short labels (GL 68: single words, captions, UI and data labels) need no mark: the rule the pipeline applies to a
/// placement, public so a host can apply it to the parts it writes on its own -- Excel, per cell.
/// </summary>
public static class Labels
{
    private static readonly char[] SentenceMarks = { '.', '!', '?', ';', '。', '！', '？' };

    private static readonly Regex Tag = new("<[^>]*>", RegexOptions.CultureInvariant);

    /// <summary>
    /// After the normalisation of hashing (HTML without its tags): empty, or at most three words and 40 characters
    /// (eight characters for text with CJK characters) without any sentence mark (<c>. ! ? ; 。 ！ ？</c>): "Approved",
    /// "Q3 revenue", <c>&lt;b&gt;Approved&lt;/b&gt;</c>. Anything that reads as a sentence is marked. The TypeScript
    /// SDK's <c>isLabel</c> is the same.
    /// </summary>
    public static bool IsLabel(string text, string mediaType = "text/plain")
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var normalized = ContentHash.Normalize(MediaTypes.IsHtml(mediaType ?? "text/plain") ? Tag.Replace(text, " ") : text);
        if (normalized.Length == 0)
        {
            return true;
        }
        if (normalized.IndexOfAny(SentenceMarks) >= 0)
        {
            return false;
        }
        return normalized.Any(TokenEstimate.IsCjk)
            ? normalized.Length <= 8
            : normalized.Length <= 40 && normalized.Split(' ').Length <= 3;
    }
}

/// <summary>Finds what looks like data about a person, so it never goes into a mark or a fact.</summary>
internal static class PersonalData
{
    // The TypeScript SDK's findPersonalData has the same patterns, in the same order.
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant);
    private static readonly Regex Ip = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Phone = new(@"(?<![\w.:-])\+?\d[\d ()/.-]{5,}\d(?![\w-])", RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);
    private static readonly Regex Time = new(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?", RegexOptions.CultureInvariant);

    /// <summary>
    /// What <paramref name="value"/> looks like it holds (<c>e-mail address</c>, <c>IBAN</c>, <c>IP address</c>,
    /// <c>phone number</c>: seven digits or more), or null. Ids (UUIDs, such as record ids) and times are left out first.
    /// </summary>
    public static string? Find(string value)
    {
        value = Time.Replace(Uuid.Replace(value, " "), " ");
        if (Email.IsMatch(value))
        {
            return "e-mail address";
        }
        if (Iban.IsMatch(value))
        {
            return "IBAN";
        }
        if (Ip.IsMatch(value))
        {
            return "IP address";
        }
        // What the patterns before found is no phone number (an IBAN's digits).
        value = Ip.Replace(Iban.Replace(value, " "), " ");
        return Phone.Matches(value).Cast<Match>().Any(m => m.Value.Count(char.IsDigit) >= 7) ? "phone number" : null;
    }
}

/// <summary>Signed records as marks keep them: the server's JSON, compact.</summary>
internal static class ProvenanceJson
{
    public static string Compact(SignedRecord record) => JsonSerializer.Serialize(record, FadenstackJson.Options);

    public static string Compact(object value) => JsonSerializer.Serialize(value, FadenstackJson.Options);

    /// <summary>Whether a value is a record id as the server issues them and marks carry them: a UUID.</summary>
    public static bool IsRecordId(string? value) => value is not null && RecordId.IsMatch(value);

    private static readonly Regex RecordId = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant);
}

/// <summary>HTML attribute values as both SDKs write them: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and <c>"</c> escaped, nothing else.</summary>
internal static class HtmlText
{
    public static string Attribute(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

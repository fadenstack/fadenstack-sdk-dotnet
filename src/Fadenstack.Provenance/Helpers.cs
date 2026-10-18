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

    /// <summary>Source code, scripts and structured data (JSON, XML, YAML, SQL, CSS): exempt from marking.</summary>
    public static bool IsCode(string mediaType)
    {
        var bare = Bare(mediaType);
        if (bare.StartsWith("image/", StringComparison.Ordinal))
        {
            return false; // image/svg+xml is a picture
        }
        return bare.StartsWith("text/x-", StringComparison.Ordinal) || bare.EndsWith("+json", StringComparison.Ordinal)
            || bare.EndsWith("+xml", StringComparison.Ordinal) || Code.Contains(bare);
    }

    private static readonly HashSet<string> Code = new(StringComparer.Ordinal)
    {
        "application/json", "application/xml", "text/xml", "application/javascript", "text/javascript", "application/x-yaml",
        "application/yaml", "text/yaml", "application/sql", "text/css", "application/x-sh", "application/x-powershell",
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

/// <summary>Short labels (GL 68: single words, captions, UI and data labels) need no mark.</summary>
internal static class Labels
{
    private static readonly char[] SentenceMarks = { '.', '!', '?', ';', '。', '！', '？' };

    /// <summary>
    /// At most three words and 40 characters (eight CJK characters), without any sentence mark: "Approved",
    /// "Q3 revenue". Anything that reads as a sentence is marked.
    /// </summary>
    public static bool IsLabel(string text)
    {
        var normalized = ContentHash.Normalize(text);
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
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex Phone = new(@"(?<![\w+\-])(\+|00|0)\d[\d \-/().]{6,}\d(?!\w)", RegexOptions.CultureInvariant);
    private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b", RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// What <paramref name="value"/> looks like it holds (<c>e-mail address</c>, <c>phone number</c>, <c>IBAN</c>), or null.
    /// Ids (UUIDs, such as record ids) are not taken for phone numbers.
    /// </summary>
    public static string? Find(string value)
    {
        value = Uuid.Replace(value, " ");
        return Email.IsMatch(value) ? "e-mail address" : Iban.IsMatch(value) ? "IBAN" : Phone.IsMatch(value) ? "phone number" : null;
    }
}

/// <summary>Signed records as marks keep them: the server's JSON, compact.</summary>
internal static class ProvenanceJson
{
    public static string Compact(SignedRecord record) => JsonSerializer.Serialize(record, FadenstackJson.Options);

    public static string Compact(object value) => JsonSerializer.Serialize(value, FadenstackJson.Options);

    /// <summary>Whether a value is a record id as the server issues them: letters, digits, '-' and '_', 8 to 64 long.</summary>
    public static bool IsRecordId(string? value) => value is not null && RecordId.IsMatch(value);

    private static readonly Regex RecordId = new("^[A-Za-z0-9][A-Za-z0-9_-]{7,63}$", RegexOptions.CultureInvariant);
}

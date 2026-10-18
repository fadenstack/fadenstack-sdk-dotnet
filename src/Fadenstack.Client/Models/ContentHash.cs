using System.Security.Cryptography;
using System.Text;

namespace Fadenstack.Models;

/// <summary>
/// How content is hashed for provenance, the same in every SDK, the gateway and the provenance engine: the server
/// gets the hash, never the content. Text is normalised first, so a host that reflows or re-spaces it still
/// matches: Unicode NFC; U+0007 and U+0001 removed (Word's cell and field marks); every run of white space (spaces,
/// tabs, line breaks, U+00A0 and the other Unicode spaces) one space; no leading or trailing space. Then UTF-8,
/// SHA-256, hex.
/// </summary>
public static class ContentHash
{
    /// <summary>The text as it is hashed.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        string composed;
        try
        {
            composed = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            composed = text; // a lone surrogate: nothing to compose
        }
        var builder = new StringBuilder(composed.Length);
        var space = false;
        foreach (var c in composed)
        {
            if (c is '\u0007' or '\u0001')
            {
                continue;
            }
            if (IsSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>SHA-256 of the normalised text, hex.</summary>
    public static string TextSha256(string text) => Hex(Encoding.UTF8.GetBytes(Normalize(text)));

    /// <summary>SHA-256 of bytes (pictures, files), hex.</summary>
    public static string BytesSha256(byte[] data) => Hex(data ?? throw new ArgumentNullException(nameof(data)));

    /// <summary>
    /// White space as the reference implementation counts it (Python's <c>str.isspace</c>): .NET's white space and the
    /// information separators U+001C to U+001F.
    /// </summary>
    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001C' and <= '\u001F';

    private static string Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}

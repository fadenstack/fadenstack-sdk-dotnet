namespace Fadenstack.Agents;

/// <summary>Why a picture did not pass <see cref="PictureData.Check"/>.</summary>
public enum PictureProblem
{
    /// <summary>It passed.</summary>
    None,

    /// <summary>There is no data in it.</summary>
    Missing,

    /// <summary>The data's first bytes are not a PNG, JPEG, WebP or GIF.</summary>
    Type,
}

/// <summary>What <see cref="PictureData.Check"/> found: the picture as it may go, or the problem.</summary>
public sealed record PictureCheck
{
    /// <summary>The picture passed.</summary>
    public bool Ok => Problem == PictureProblem.None;

    public PictureProblem Problem { get; init; }

    /// <summary>The type the bytes show (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>image/gif</c>), not the type the data URL claimed.</summary>
    public string MediaType { get; init; } = "";

    /// <summary><c>data:{MediaType};base64,...</c>: the type in the URL is the one the bytes show.</summary>
    public string DataUrl { get; init; } = "";
}

/// <summary>
/// The check of a picture before it is sent (core 0.7.1 rc23 judges a picture part by its first bytes: only PNG, JPEG, WebP and GIF pass; anything
/// else is a 400 <c>picture_type_not_allowed</c>, no data is <c>picture_missing</c>). OWASP ASVS 5.0 V5 (file handling: the content decides the
/// type, not the declared one). TypeScript: <c>checkPicture</c>.
/// </summary>
public static class PictureData
{
    /// <summary>
    /// The media type the first bytes show: PNG (89 50 4E 47 0D 0A 1A 0A), JPEG (FF D8 FF), GIF ("GIF87a" or "GIF89a"), WebP ("RIFF" at 0-3 and
    /// "WEBP" at 8-11); null for anything else.
    /// </summary>
    public static string? TypeOf(byte[] bytes)
    {
        if (bytes is null)
        {
            return null;
        }
        bool At(int i, params byte[] values)
        {
            if (bytes.Length < i + values.Length)
            {
                return false;
            }
            for (var k = 0; k < values.Length; k++)
            {
                if (bytes[i + k] != values[k])
                {
                    return false;
                }
            }
            return true;
        }
        if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return "image/png";
        }
        if (At(0, 0xFF, 0xD8, 0xFF))
        {
            return "image/jpeg";
        }
        if (At(0, 0x47, 0x49, 0x46, 0x38) && bytes.Length > 5 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return "image/gif";
        }
        if (At(0, 0x52, 0x49, 0x46, 0x46) && At(8, 0x57, 0x45, 0x42, 0x50))
        {
            return "image/webp";
        }
        return null;
    }

    /// <summary>
    /// Checks a picture's <c>data:</c> URL by the data's first bytes, not by what the URL claims: no data is <see cref="PictureProblem.Missing"/>,
    /// anything but a PNG, JPEG, WebP or GIF is <see cref="PictureProblem.Type"/>. A picture that passes comes back with the type its bytes show.
    /// </summary>
    public static PictureCheck Check(string? dataUrl)
    {
        var text = dataUrl?.Trim() ?? "";
        if (text.Length == 0)
        {
            return new PictureCheck { Problem = PictureProblem.Missing };
        }
        var comma = text.IndexOf(',');
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
        {
            return new PictureCheck { Problem = PictureProblem.Type };
        }
        var base64 = text.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var payload = text.Substring(comma + 1);
        if (payload.Length == 0)
        {
            return new PictureCheck { Problem = PictureProblem.Missing };
        }
        byte[] bytes;
        string encoded;
        try
        {
            if (base64)
            {
                encoded = new string(payload.Where(c => !char.IsWhiteSpace(c)).ToArray());
                var prefix = encoded.Substring(0, Math.Min(encoded.Length, 24) / 4 * 4); // 18 bytes are plenty for every signature
                bytes = Convert.FromBase64String(prefix);
            }
            else
            {
                bytes = PercentDecode(payload);
                encoded = Convert.ToBase64String(bytes);
            }
        }
        catch (FormatException)
        {
            return new PictureCheck { Problem = PictureProblem.Type };
        }
        if (bytes.Length == 0)
        {
            return new PictureCheck { Problem = base64 && encoded.Length == 0 ? PictureProblem.Missing : PictureProblem.Type };
        }
        return TypeOf(bytes) is { } type
            ? new PictureCheck { MediaType = type, DataUrl = "data:" + type + ";base64," + encoded }
            : new PictureCheck { Problem = PictureProblem.Type };
    }

    private static byte[] PercentDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c < 0x100)
            {
                bytes.Add((byte)c);
            }
            else
            {
                throw new FormatException("Not a byte.");
            }
        }
        return bytes.ToArray();
    }
}

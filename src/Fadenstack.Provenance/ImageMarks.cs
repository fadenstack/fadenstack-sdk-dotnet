using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.Provenance;

/// <summary>
/// What a picture's bytes carry of its marks: a C2PA manifest (JUMBF) and XMP with the IPTC digital source type. Read
/// from the file's structure (PNG chunks, JPEG segments, WebP chunks), not verified: the server's detector checks the
/// signature. A host that re-encodes a picture drops both; the watermark in the pixels stays.
/// </summary>
public sealed record ImageMarks
{
    /// <summary><c>png</c>, <c>jpeg</c>, <c>webp</c>, or null for another format.</summary>
    public string? Format { get; init; }

    /// <summary>A C2PA manifest store (JUMBF): PNG <c>caBX</c>, JPEG APP11, WebP <c>C2PA</c>.</summary>
    public bool HasC2pa { get; init; }

    /// <summary>XMP: PNG <c>iTXt XML:com.adobe.xmp</c>, JPEG APP1 <c>http://ns.adobe.com/xap/1.0/</c>, WebP <c>XMP </c>.</summary>
    public bool HasXmp { get; init; }

    /// <summary>The IPTC digital source type the XMP names (<c>trainedAlgorithmicMedia</c>), or null.</summary>
    public string? DigitalSourceType { get; init; }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] XmpNamespace = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly Regex SourceType = new(@"digitalsourcetype/([A-Za-z]+)", RegexOptions.CultureInvariant);

    /// <summary>Reads the marks a picture's bytes carry; a format it does not know carries none.</summary>
    public static ImageMarks Read(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }
        try
        {
            if (StartsWith(data, 0, PngSignature))
            {
                return Png(data);
            }
            if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
            {
                return Jpeg(data);
            }
            if (data.Length >= 12 && Ascii(data, 0, 4) == "RIFF" && Ascii(data, 8, 4) == "WEBP")
            {
                return WebP(data);
            }
        }
        catch (IndexOutOfRangeException)
        {
            // a truncated file: what was found so far is not trusted
        }
        return new ImageMarks();
    }

    private static ImageMarks Png(byte[] data)
    {
        bool c2pa = false, xmp = false;
        string? source = null;
        for (var at = PngSignature.Length; at + 8 <= data.Length;)
        {
            var length = (int)BigEndian(data, at);
            var type = Ascii(data, at + 4, 4);
            var body = at + 8;
            if (length < 0 || body + length > data.Length)
            {
                break;
            }
            if (type == "caBX")
            {
                c2pa = true;
            }
            else if (type == "iTXt" && StartsWith(data, body, Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0")))
            {
                xmp = true;
                source ??= Source(data, body, length);
            }
            else if (type == "IEND")
            {
                break;
            }
            at = body + length + 4; // and the CRC
        }
        return new ImageMarks { Format = "png", HasC2pa = c2pa, HasXmp = xmp, DigitalSourceType = source };
    }

    private static ImageMarks Jpeg(byte[] data)
    {
        bool c2pa = false, xmp = false;
        string? source = null;
        for (var at = 2; at + 4 <= data.Length;)
        {
            if (data[at] != 0xFF)
            {
                break;
            }
            var marker = data[at + 1];
            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                at += 2; // no length
                continue;
            }
            if (marker == 0xDA || marker == 0xD9)
            {
                break; // the image data starts; metadata comes before it
            }
            var length = (data[at + 2] << 8) | data[at + 3];
            var body = at + 4;
            var size = length - 2;
            if (size < 0 || body + size > data.Length)
            {
                break;
            }
            if (marker == 0xEB && size >= 2 && data[body] == (byte)'J' && data[body + 1] == (byte)'P'
                && Contains(data, body, size, "jumb") && Contains(data, body, size, "c2pa"))
            {
                c2pa = true;
            }
            else if (marker == 0xE1 && StartsWith(data, body, XmpNamespace))
            {
                xmp = true;
                source ??= Source(data, body, size);
            }
            at = body + size;
        }
        return new ImageMarks { Format = "jpeg", HasC2pa = c2pa, HasXmp = xmp, DigitalSourceType = source };
    }

    private static ImageMarks WebP(byte[] data)
    {
        bool c2pa = false, xmp = false;
        string? source = null;
        for (var at = 12; at + 8 <= data.Length;)
        {
            var type = Ascii(data, at, 4);
            var length = (int)LittleEndian(data, at + 4);
            var body = at + 8;
            if (length < 0 || body + length > data.Length)
            {
                break;
            }
            if (type == "C2PA")
            {
                c2pa = true;
            }
            else if (type == "XMP ")
            {
                xmp = true;
                source ??= Source(data, body, length);
            }
            at = body + length + (length & 1); // chunks are padded to an even size
        }
        return new ImageMarks { Format = "webp", HasC2pa = c2pa, HasXmp = xmp, DigitalSourceType = source };
    }

    private static string? Source(byte[] data, int start, int length)
    {
        var match = SourceType.Match(Encoding.UTF8.GetString(data, start, Math.Min(length, data.Length - start)));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static uint BigEndian(byte[] d, int at) => (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);

    private static uint LittleEndian(byte[] d, int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);

    private static string Ascii(byte[] d, int at, int count) => at + count <= d.Length ? Encoding.ASCII.GetString(d, at, count) : "";

    private static bool StartsWith(byte[] d, int at, byte[] prefix)
    {
        if (at + prefix.Length > d.Length)
        {
            return false;
        }
        for (var i = 0; i < prefix.Length; i++)
        {
            if (d[at + i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool Contains(byte[] d, int start, int length, string ascii)
    {
        var needle = Encoding.ASCII.GetBytes(ascii);
        var end = Math.Min(d.Length, start + length) - needle.Length;
        for (var i = start; i <= end; i++)
        {
            if (StartsWith(d, i, needle))
            {
                return true;
            }
        }
        return false;
    }
}

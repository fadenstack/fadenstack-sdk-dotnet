using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fadenstack.UI.Wpf.Services;

/// <summary>
/// Pictures made ready for a message (<see cref="ViewModels.AgentChatViewModel.AttachPicture"/>): at most
/// <see cref="MaxSide"/> pixels on the longer side (models scale larger ones down anyway, and the transcript keeps it),
/// as JPEG on white (the transparent parts of a screenshot stay readable), as a <c>data:image/jpeg;base64,</c> URL. No
/// visuals are drawn, so it runs on any thread: a file is read off the UI thread; a bitmap is used on the thread it
/// belongs to, unless it is frozen.
/// </summary>
public static class Pictures
{
    /// <summary>The longer side of a picture as it goes, in pixels.</summary>
    public const int MaxSide = 1600;

    /// <summary>The JPEG quality (1 to 100).</summary>
    public const int Quality = 85;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
    };

    /// <summary>The picture as a message takes it: scaled down to <paramref name="maxSide"/>, JPEG on white, a data URL.</summary>
    public static string ToDataUrl(BitmapSource picture, int maxSide = MaxSide, int quality = Quality)
    {
        if (picture is null)
        {
            throw new ArgumentNullException(nameof(picture));
        }
        var longer = Math.Max(1, Math.Max(picture.PixelWidth, picture.PixelHeight));
        var scale = Math.Min(1.0, maxSide / (double)longer);
        BitmapSource source = scale < 1 ? new TransformedBitmap(picture, new ScaleTransform(scale, scale)) : picture;
        var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var (width, height) = (straight.PixelWidth, straight.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // On white: c * a + 255 * (1 - a), with straight (not premultiplied) alpha.
            var alpha = pixels[i + 3];
            if (alpha != 255)
            {
                pixels[i] = (byte)(255 - (255 - pixels[i]) * alpha / 255);
                pixels[i + 1] = (byte)(255 - (255 - pixels[i + 1]) * alpha / 255);
                pixels[i + 2] = (byte)(255 - (255 - pixels[i + 2]) * alpha / 255);
            }
        }
        var flat = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Max(1, Math.Min(100, quality)) };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>A picture file as a message takes it (<see cref="ToDataUrl"/>). Throws when it cannot be read as a picture.</summary>
    public static string ReadFile(string path, int maxSide = MaxSide, int quality = Quality)
    {
        using var file = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new NotSupportedException($"{Path.GetFileName(path)} has no picture.");
        }
        return ToDataUrl(decoder.Frames[0], maxSide, quality);
    }

    /// <summary>Whether a file's name says it is a picture (one that cannot be read anyway fails in <see cref="ReadFile"/>).</summary>
    public static bool IsPictureFile(string path) => Extensions.Contains(Path.GetExtension(path) ?? "");

    /// <summary>The picture files among pasted or dropped files; other files are the host's.</summary>
    public static IReadOnlyList<string> FilesIn(IDataObject? data)
    {
        try
        {
            return data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(IsPictureFile).ToList()
                : Array.Empty<string>();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Array.Empty<string>(); // another process holds the data
        }
    }

    /// <summary>
    /// Pasted data a picture is taken from: picture files, or a picture without text. Text wins: cells copied from Excel
    /// come with a picture of them, and they should paste as text.
    /// </summary>
    public static bool HasPicture(IDataObject? data) =>
        FilesIn(data).Count > 0 || (data is not null && !HasText(data) && (Present(data, "PNG") || Present(data, DataFormats.Bitmap)));

    /// <summary>
    /// The picture in pasted data, on this thread: the PNG an application put there (it keeps transparency), or the
    /// bitmap without its alpha (screenshots put a DIB whose alpha is often 0 throughout). Null when there is none.
    /// </summary>
    public static BitmapSource? BitmapIn(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }
        try
        {
            if (Present(data, "PNG") && data.GetData("PNG") is MemoryStream png)
            {
                var decoder = BitmapDecoder.Create(new MemoryStream(png.ToArray()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            }
            if (Present(data, DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                return new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException
                                      or FileFormatException or ArgumentException)
        {
            // not a picture this machine can read
        }
        return null;
    }

    /// <summary>
    /// A picture's <c>data:</c> URL as a bitmap, decoded at <paramref name="decodeWidth"/> pixels wide when given (a
    /// chip's thumbnail); frozen. Null for anything that is not a picture.
    /// </summary>
    public static BitmapSource? FromDataUrl(string? dataUrl, int decodeWidth = 0)
    {
        if (dataUrl is null || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || dataUrl.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(dataUrl.Substring(comma + 1)));
            if (decodeWidth > 0)
            {
                image.DecodePixelWidth = decodeWidth;
            }
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or FileFormatException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool HasText(IDataObject data) => Present(data, DataFormats.UnicodeText) || Present(data, DataFormats.Text);

    private static bool Present(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}

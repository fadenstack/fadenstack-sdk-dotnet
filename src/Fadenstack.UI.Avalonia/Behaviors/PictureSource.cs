using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Shows a picture at the size it is drawn at, decoded off the UI thread: <c>b:PictureSource.Data="{Binding Picture}"</c>
/// (bytes, or a <c>data:image/...</c> URL) with <c>b:PictureSource.Width="240"</c> on an <see cref="Image"/>. The bitmap lives
/// only as long as the image has data: a recycled container (its data cleared) or a detached one lets it go, so a
/// scrolled-away turn holds the encoded picture in its view model and nothing decoded.
/// </summary>
public static class PictureSource
{
    public static readonly AttachedProperty<object?> DataProperty =
        AvaloniaProperty.RegisterAttached<Image, object?>("Data", typeof(PictureSource));

    /// <summary>The width the picture is drawn at, in device-independent pixels; the decoder scales it by the screen's scale.</summary>
    public static readonly AttachedProperty<int> WidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Width", typeof(PictureSource), 240);

    private static readonly AttachedProperty<Decode?> CurrentProperty =
        AvaloniaProperty.RegisterAttached<Image, Decode?>("Current", typeof(PictureSource));

    static PictureSource()
    {
        DataProperty.Changed.AddClassHandler<Image>((image, _) => Refresh(image));
        WidthProperty.Changed.AddClassHandler<Image>((image, _) => Refresh(image));
    }

    public static object? GetData(Image element) => element.GetValue(DataProperty);

    public static void SetData(Image element, object? value) => element.SetValue(DataProperty, value);

    public static int GetWidth(Image element) => element.GetValue(WidthProperty);

    public static void SetWidth(Image element, int value) => element.SetValue(WidthProperty, value);

    /// <summary>The bytes of a <c>data:</c> URL's payload, or null when it is not a base64 one.</summary>
    internal static byte[]? FromDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0 || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(url.Substring(comma + 1));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Refresh(Image image)
    {
        var running = image.GetValue(CurrentProperty);
        running?.Cancel();
        image.Source = null;
        (running?.Bitmap)?.Dispose();
        image.SetValue(CurrentProperty, null);
        image.DetachedFromVisualTree -= OnDetached;
        image.AttachedToVisualTree -= OnAttached;
        if (GetData(image) is not { } data)
        {
            return;
        }
        image.DetachedFromVisualTree += OnDetached;
        image.AttachedToVisualTree += OnAttached;
        if (image.IsAttachedToVisualTree())
        {
            Start(image, data);
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var image = (Image)sender!;
        if (image.GetValue(CurrentProperty) is null && GetData(image) is { } data)
        {
            Start(image, data);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var image = (Image)sender!;
        var running = image.GetValue(CurrentProperty);
        running?.Cancel();
        image.Source = null;
        running?.Bitmap?.Dispose();
        image.SetValue(CurrentProperty, null);
    }

    private static void Start(Image image, object data)
    {
        var scale = TopLevel.GetTopLevel(image)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)Math.Ceiling(GetWidth(image) * scale));
        var decode = new Decode();
        image.SetValue(CurrentProperty, decode);
        _ = decode.RunAsync(image, data, width);
    }

    /// <summary>One decode: off the UI thread, dropped if the image moved on before it finished.</summary>
    private sealed class Decode
    {
        private readonly CancellationTokenSource _cancel = new();

        public Bitmap? Bitmap { get; private set; }

        public void Cancel() => _cancel.Cancel();

        public async Task RunAsync(Image image, object data, int width)
        {
            var token = _cancel.Token;
            Bitmap? bitmap = null;
            try
            {
                bitmap = await Task.Run(() => Open(data, width), token);
            }
            catch (Exception)
            {
                // A picture that cannot be read stays blank.
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || bitmap is null)
                {
                    bitmap?.Dispose();
                    return;
                }
                Bitmap = bitmap;
                image.Source = bitmap;
            }, DispatcherPriority.Background);
        }

        private static Bitmap? Open(object data, int width)
        {
            var bytes = data switch
            {
                byte[] b => b,
                string url => FromDataUrl(url),
                _ => null,
            };
            if (bytes is not { Length: > 0 })
            {
                return null;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        }
    }
}

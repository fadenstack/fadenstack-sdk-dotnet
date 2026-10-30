using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The icon set (Themes/Icons.axaml) and the FsIcon control (design 22, section 4).</summary>
public class IconTests
{
    internal static string[] ListedNames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "icons", "icons.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "icons", "icons.json")));
        return doc.RootElement.GetProperty("icons").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    internal static StreamGeometry Icon(string name)
    {
        FadenstackAssets.EnsureLoaded();
        Assert.True(Application.Current!.TryGetResource("Icon." + name, null, out var value), "Icon." + name + " is missing from Icons.axaml");
        return Assert.IsType<StreamGeometry>(value);
    }

    [AvaloniaFact]
    public void Icons_axaml_contains_every_name_in_icons_json()
    {
        var names = ListedNames();
        Assert.True(names.Length >= 70, "the first set of design 22, section 4");
        Assert.Equal(names.Length, names.Distinct().Count());
        foreach (var name in names)
        {
            var bounds = Icon(name).Bounds;
            Assert.True(bounds.Width > 1 && bounds.Height > 1, name);
        }
    }

    [AvaloniaFact]
    public void Every_icon_lies_inside_the_24_box()
    {
        foreach (var name in ListedNames())
        {
            var b = Icon(name).Bounds;
            Assert.True(b.X >= -0.01 && b.Y >= -0.01 && b.Right <= 24.01 && b.Bottom <= 24.01, $"{name}: {b}");
        }
    }

    [AvaloniaFact]
    public void FsIcon_measures_to_Size_by_Size_and_defaults_to_18()
    {
        var icon = new FsIcon();
        Assert.Equal(18, icon.Size);
        icon.Measure(new Size(1000, 1000));
        Assert.Equal(new Size(18, 18), icon.DesiredSize);
        icon.Size = 22;
        icon.Measure(new Size(1000, 1000));
        Assert.Equal(new Size(22, 22), icon.DesiredSize);
    }

    [AvaloniaFact]
    public void FsIcon_follows_the_foreground_of_its_button()
    {
        var icon = new FsIcon { Data = StreamGeometry.Parse("M0 0H24V24H0Z"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Foreground = Brushes.Red, Background = Brushes.White, BorderThickness = default, Padding = default, Width = 32, Height = 32, Content = icon };
        var frame = Render(button, 1);
        var (r, g, b) = frame.Pixel(16, 16);
        Assert.True(r > 200 && g < 60 && b < 60, $"expected red ink, got {r},{g},{b}");
    }

    /// <summary>
    /// Two assertions that together prove "no extra offset", for the asymmetric icons (Send, Logout) as much as the
    /// symmetric ones. (1) The ink must equal a reference drawn without FsIcon: a plain Path of the same geometry,
    /// scaled by Size/24 and placed at the box origin (the centre of the 32x32 host minus Size/2) with a transform written
    /// out here, so any stretch to the path's bounds, centring of the ink instead of the box, or half-pixel shift shows as
    /// pixel differences. (2) The ink's bounding box (pixels with at least 15 % coverage, so the thin tips of arrows and
    /// chevrons count) must lie within one device pixel of the geometry's own bounds put through that same transform.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0, 18.0)]
    [InlineData(2.0, 18.0)]
    [InlineData(1.0, 16.0)]
    [InlineData(2.0, 22.0)]
    public void Ink_of_every_icon_sits_where_the_24_box_puts_it(double scaling, double size)
    {
        foreach (var name in ListedNames())
        {
            var geometry = Icon(name);
            var icon = new FsIcon { Data = geometry, Size = size, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var frame = Render(new Border { Width = 32, Height = 32, Background = Brushes.White, Child = icon }, scaling);

            var s = size / 24;
            var origin = (32 - size) / 2;
            var path = new global::Avalonia.Controls.Shapes.Path { Data = geometry, Fill = Brushes.Black, Stretch = Stretch.None, RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new ScaleTransform(s, s) };
            var canvas = new Canvas { Width = 32, Height = 32, Background = Brushes.White };
            Canvas.SetLeft(path, origin);
            Canvas.SetTop(path, origin);
            canvas.Children.Add(path);
            var reference = Render(canvas, scaling);

            Assert.True(frame.MaxDifference(reference) <= 0.12, $"{name} at {scaling}x, size {size}: differs from the reference by {frame.MaxDifference(reference):0.00}");

            var ink = frame.InkBounds(scaling, 0.15);
            var expected = new Rect(origin + geometry.Bounds.X * s, origin + geometry.Bounds.Y * s, geometry.Bounds.Width * s, geometry.Bounds.Height * s);
            var error = Math.Max(Math.Max(Math.Abs(ink.X - expected.X), Math.Abs(ink.Y - expected.Y)),
                                 Math.Max(Math.Abs(ink.Right - expected.Right), Math.Abs(ink.Bottom - expected.Bottom)));
            Assert.True(error <= 1.0 / scaling + 1e-6, $"{name} at {scaling}x, size {size}: ink {ink} vs expected {expected} (off by {error:0.00})");
        }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void The_design_box_is_scaled_not_the_bounds(double scaling)
    {
        // A square in the top left corner of the box: the ink must stay there, 9x9 at size 18, not grow to fill the icon.
        var geometry = StreamGeometry.Parse("M0 0H12V12H0Z");
        var icon = new FsIcon { Data = geometry, Size = 18, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ink = Render(new Border { Width = 32, Height = 32, Background = Brushes.White, Child = icon }, scaling).InkBounds(scaling);
        Assert.Equal(new Rect(7, 7, 9, 9), ink);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void The_box_origin_snaps_to_whole_device_pixels(double scaling)
    {
        // A host of odd size puts the centred box on a half pixel at 1x; the full-box square must still have hard edges.
        var icon = new FsIcon { Data = StreamGeometry.Parse("M0 0H24V24H0Z"), Size = 18, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var frame = Render(new Border { Width = 33, Height = 33, Background = Brushes.White, Child = icon }, scaling);
        Assert.Equal(0, frame.PartialPixels());
    }

    [AvaloniaFact]
    public void Chat_view_loads_the_icons_and_font_keys()
    {
        // The chat view merges the dictionaries into the application the same way as the colours.
        Application.Current!.Resources.MergedDictionaries.Clear();
        _ = new Fadenstack.UI.Avalonia.Views.ChatView();
        Assert.True(Application.Current.TryGetResource("Icon.Send", null, out _));
        Assert.True(Application.Current.TryGetResource("Fadenstack.FontFamily.Mono", null, out _));
    }

    // ---- rendering helpers ----

    internal sealed class Frame
    {
        private readonly byte[] _data;
        private readonly int _stride;
        private readonly bool _bgra;
        internal readonly int Width;
        internal readonly int Height;

        public Frame(WriteableBitmap bmp)
        {
            using var fb = bmp.Lock();
            _data = new byte[fb.RowBytes * fb.Size.Height];
            Marshal.Copy(fb.Address, _data, 0, _data.Length);
            _stride = fb.RowBytes;
            Width = fb.Size.Width;
            Height = fb.Size.Height;
            _bgra = fb.Format == PixelFormat.Bgra8888;
        }

        public (byte R, byte G, byte B) Pixel(int x, int y)
        {
            var i = y * _stride + x * 4;
            return _bgra ? (_data[i + 2], _data[i + 1], _data[i]) : (_data[i], _data[i + 1], _data[i + 2]);
        }

        /// <summary>How much of a pixel is ink on a white page: 0 (white) to 1 (black), from the luminance.</summary>
        public double Coverage(int x, int y)
        {
            var (r, g, b) = Pixel(x, y);
            return 1 - (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }

        /// <summary>The bounding box of the pixels covered at least by <paramref name="threshold"/>, in DIPs.</summary>
        public Rect InkBounds(double scaling, double threshold = 0.5)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (Coverage(x, y) >= threshold)
                    {
                        x0 = Math.Min(x0, x);
                        y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x);
                        y1 = Math.Max(y1, y);
                    }
                }
            }
            Assert.True(x1 >= 0, "nothing was drawn");
            return new Rect(x0 / scaling, y0 / scaling, (x1 + 1 - x0) / scaling, (y1 + 1 - y0) / scaling);
        }

        /// <summary>The largest difference in coverage (0 to 1) between this frame and <paramref name="other"/>.</summary>
        public double MaxDifference(Frame other)
        {
            Assert.Equal((Width, Height), (other.Width, other.Height));
            var max = 0.0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    max = Math.Max(max, Math.Abs(Coverage(x, y) - other.Coverage(x, y)));
                }
            }
            return max;
        }

        /// <summary>Pixels that are neither white nor black: antialiased edges. Zero means every edge sits on a pixel boundary.</summary>
        public int PartialPixels()
        {
            var n = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var c = Coverage(x, y);
                    if (c > 0.02 && c < 0.98)
                    {
                        n++;
                    }
                }
            }
            return n;
        }
    }

    /// <summary>Shows <paramref name="content"/> in a headless window of exactly its size at the given render scaling and captures it.</summary>
    internal static Frame Render(Control content, double scaling)
    {
        var window = new Window
        {
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.White,
            Padding = default,
        };
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var frame = new Frame(window.CaptureRenderedFrame()!);
        window.Close();
        return frame;
    }
}

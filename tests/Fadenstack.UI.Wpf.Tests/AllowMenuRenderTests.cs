using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The approval card's split-button menu in the three colour sets: its style resolves the theme's brushes (not the
/// system's), and, when FADEN_WPF_RENDERS names a folder, it is drawn offscreen to PNG files there (no window opens).
/// </summary>
public class AllowMenuRenderTests
{
    public static IEnumerable<object[]> Themes() => new[] { "Light", "Dark", "DarkGray" }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_menu_wears_the_theme(string theme) => Sta.Run(() =>
    {
        var resources = Load(theme);
        var menu = Build(resources);

        var surface = Hue("Fadenstack.Surface");
        var border = Hue("Fadenstack.Border");
        var card = Assert.IsType<Border>(menu.Template.FindName("Card", menu));
        Assert.Equal(surface, Assert.IsType<SolidColorBrush>(card.Background).Color);
        Assert.Equal(border, Assert.IsType<SolidColorBrush>(card.BorderBrush).Color);
        Assert.Equal(12, card.CornerRadius.TopLeft);
        Assert.NotNull(card.Effect);
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            Assert.Equal(Hue("Fadenstack.Foreground"), Assert.IsType<SolidColorBrush>(item.Foreground).Color);
            var root1 = Assert.IsType<Border>(item.Template.FindName("Root", item));
            Assert.Equal(6, root1.CornerRadius.TopLeft);
        }
        var disabled = menu.Items.OfType<MenuItem>().First(i => !i.IsEnabled);
        Assert.Equal(Hue("Fadenstack.ForegroundMuted"), Assert.IsType<SolidColorBrush>(disabled.Foreground).Color);

        System.Windows.Media.Color Hue(string key) => ((SolidColorBrush)resources[key]).Color;

        if (Environment.GetEnvironmentVariable("FADEN_WPF_RENDERS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            Render(menu, (Brush)resources["Fadenstack.Background"], Path.Combine(folder, $"allow-menu-{theme.ToLowerInvariant()}.png"));
        }
    });

    [Fact]
    public void No_style_is_implicit() => Sta.Run(() =>
    {
        var resources = Load("Light");
        Assert.False(resources.Contains(typeof(ContextMenu)));
        Assert.False(resources.Contains(typeof(MenuItem)));
    });

    private static ResourceDictionary Load(string theme)
    {
        EnsureApplication();
        var all = new ResourceDictionary();
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Colors.{theme}.xaml") });
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Chat.xaml") });
        return all;
    }

    // A ContextMenu cannot have a parent, so it is measured and drawn as the root, on a canvas of the panel's colour.
    private static ContextMenu Build(ResourceDictionary resources)
    {
        var menu = new ContextMenu { Style = (Style)resources["Fadenstack.AllowMenu"], UseLayoutRounding = true };
        menu.Resources.MergedDictionaries.Add(resources);
        menu.Items.Add(new MenuItem { Header = "Allow for this chat" });
        menu.Items.Add(new MenuItem { Header = "Allow for 1 hour" });
        menu.Items.Add(new MenuItem { Header = "Allow always", IsEnabled = false });
        menu.Measure(new Size(400, 400));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        return menu;
    }

    private static void Render(ContextMenu menu, Brush canvas, string path)
    {
        const double pad = 16;
        var width = menu.ActualWidth + 2 * pad;
        var height = menu.ActualHeight + 2 * pad;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(canvas, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(menu), null, new Rect(pad, pad, menu.ActualWidth, menu.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }
}

/// <summary>Runs a body on one shared STA thread (WPF objects belong to the thread that made them; one Application per process).</summary>
internal static class Sta
{
    private static readonly System.Collections.Concurrent.BlockingCollection<(Action Work, TaskCompletionSource<bool> Done)> Queue = new();

    static Sta()
    {
        var thread = new Thread(() =>
        {
            foreach (var (work, done) in Queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                    done.SetResult(true);
                }
                catch (Exception x)
                {
                    done.SetException(x);
                }
            }
        })
        { IsBackground = true, Name = "WPF test STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Run(Action body)
    {
        var done = new TaskCompletionSource<bool>();
        Queue.Add((body, done));
        done.Task.GetAwaiter().GetResult();
    }
}

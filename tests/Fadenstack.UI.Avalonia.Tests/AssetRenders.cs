using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// An audit, not a test: renders the icon set and sample text (Latin, umlauts, Chinese, mono) at 1x and 2x into the
/// folder named by FADEN_ASSET_OUT. It does nothing when the variable is not set.
/// </summary>
public class AssetRenders
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("FADEN_ASSET_OUT");

    private static void Shot(Control content, double width, double scaling, string name)
    {
        var window = new Window { Content = content, Width = width, SizeToContent = SizeToContent.Height, Background = Brushes.White, Padding = default };
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(Out!, name + ".png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void Render_icon_sheet_and_text_samples()
    {
        if (string.IsNullOrEmpty(Out))
        {
            return;
        }
        Directory.CreateDirectory(Out);
        FadenstackAssets.EnsureLoaded();

        foreach (var scaling in new[] { 1.0, 2.0 })
        {
            var tag = scaling == 1 ? "1x" : "2x";

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            foreach (var name in IconTests.ListedNames())
            {
                var cell = new StackPanel { Width = 92, Margin = new Thickness(2), Spacing = 4 };
                var box = new Border { Width = 32, Height = 32, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center };
                box.Child = new FsIcon { Data = IconTests.Icon(name), Size = 22, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(box);
                cell.Children.Add(new TextBlock { Text = name, FontSize = 9, TextAlignment = TextAlignment.Center, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis });
                wrap.Children.Add(cell);
            }
            Shot(wrap, 720, scaling, "icons-" + tag);

            // The same icons at the four sizes of the design (16, 18, 20, 22) in a button row, to see centring in a 36 px circle.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
            foreach (var size in new[] { 16.0, 18.0, 20.0, 22.0 })
            {
                foreach (var name in new[] { "Send", "Close", "MoreVert", "Add", "Check" })
                {
                    row.Children.Add(new Button
                    {
                        Width = 36, Height = 36, Padding = default, CornerRadius = new CornerRadius(18), Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#4C6A92")),
                        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                        Content = new FsIcon { Data = IconTests.Icon(name), Size = size },
                    });
                }
            }
            Shot(row, 1000, scaling, "icon-buttons-" + tag);

            Application.Current!.TryGetResource("Fadenstack.FontFamily", null, out var text);
            Application.Current.TryGetResource("Fadenstack.FontFamily.Mono", null, out var mono);
            var sample = new StackPanel { Margin = new Thickness(16), Spacing = 8, Background = Brushes.White };
            void Line(string label, string content, FontFamily family, double size, FontWeight weight = FontWeight.Normal)
            {
                sample.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.Gray, FontFamily = (FontFamily)text! });
                sample.Children.Add(new TextBlock { Text = content, FontSize = size, FontFamily = family, FontWeight = weight, Foreground = Brushes.Black });
            }
            var inter = (FontFamily)text!;
            Line("Latin, Inter 14 / 600 / 700", "The quick brown fox jumps over the lazy dog 0123456789", inter, 14);
            Line("", "The quick brown fox jumps over the lazy dog (SemiBold)", inter, 14, FontWeight.SemiBold);
            Line("", "The quick brown fox jumps over the lazy dog (Bold)", inter, 14, FontWeight.Bold);
            Line("German umlauts", "Größe, Übung, Ärger, schön, Straße, Fußgängerübergang, Grüße aus München", inter, 14);
            Line("Chinese (Noto Sans SC, mixed with Latin)", "你好，世界。这是 Fadenstack 的中文界面：设置、插件、文件、活动。", inter, 14);
            Line("Chinese Medium / Bold", "常规字重 中等字重 粗体字重", inter, 16, FontWeight.Medium);
            Line("", "常规字重 中等字重 粗体字重", inter, 16, FontWeight.Bold);
            Line("Mono (JetBrains Mono)", "def cost(total, n):  # 名称 = 1\n    return round(total / max(n, 1), 2)  != == -> =>", (FontFamily)mono!, 13);
            Line("Wordmark (Sora SemiBold)", "Fadenstack", (FontFamily)Application.Current.FindResource("Fadenstack.FontFamily.Wordmark")!, 24, FontWeight.SemiBold);
            Shot(sample, 640, scaling, "text-" + tag);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Themes;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// An audit, not a test: Fadenstack's own icons beside Material icons of the set, at 16, 20, 24 and 48 px, in light, dark
/// and high contrast, at 1x (the pixels a screen shows) and 2x, into the folder named by FADEN_ICON_OUT. Each sheet also
/// shows the icons in place: a status bar item and a settings row.
/// </summary>
public class IconSheetRenders
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("FADEN_ICON_OUT");

    private static readonly int[] Sizes = [16, 20, 24, 48];

    private static readonly string[] Beside = ["SettingsOutlined", "Person", "SmartToy", "Face", "Extension"];

    private static FsIcon Icon(string name, double size) => new()
    {
        Data = (Geometry)Application.Current!.FindResource("Icon." + name)!,
        Size = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption", "muted" }, VerticalAlignment = VerticalAlignment.Center };

    private static Control Sheet(string theme)
    {
        var grid = new Grid { Margin = new Thickness(16), ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(130)));
        foreach (var _ in Sizes)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(56)));
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var head = Caption(theme);
        grid.Children.Add(head);
        for (var c = 0; c < Sizes.Length; c++)
        {
            var label = Caption($"{Sizes[c]} px");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, c + 1);
            grid.Children.Add(label);
        }
        var names = IconTests.OwnNames().Concat(Beside).ToArray();
        for (var r = 0; r < names.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(56)));
            var name = new TextBlock { Text = names[r], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, r + 1);
            grid.Children.Add(name);
            for (var c = 0; c < Sizes.Length; c++)
            {
                var icon = Icon(names[r], Sizes[c]);
                Grid.SetRow(icon, r + 1);
                Grid.SetColumn(icon, c + 1);
                grid.Children.Add(icon);
            }
        }

        // In place: a status bar item (an 18 icon and caption text) and a settings section row (an 18 icon and body text).
        var own = IconTests.OwnNames()[0];
        var status = new Border
        {
            Padding = new Thickness(8, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { Icon(own, 18), new TextBlock { Text = "qwen3.8-27b-fp8", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center }, Icon("Tune", 18), new TextBlock { Text = "Ask", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        status.Bind(Border.BackgroundProperty, status.GetResourceObservable(FadenstackKeys.SurfaceAlt));
        var rows = new StackPanel { Spacing = 2, Width = 200 };
        foreach (var (icon, text) in new[] { ("Palette", "Appearance"), ("Face", "Avatar"), (own, "Agent"), ("Translate", "Language") })
        {
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(12, 8),
                Children = { Icon(icon, 18), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            });
        }
        var context = new StackPanel { Margin = new Thickness(16, 0, 16, 16), Spacing = 12, Children = { Caption("in place"), status, rows } };
        var root = new Border { Child = new StackPanel { Children = { grid, context } } };
        root.Bind(Border.BackgroundProperty, root.GetResourceObservable(FadenstackKeys.Background));
        root.Bind(TextElement.ForegroundProperty, root.GetResourceObservable(FadenstackKeys.Foreground));
        return root;
    }

    private static async Task<WriteableBitmap> Render(Control content, double scale)
    {
        var window = new Window { Content = content, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        window.SetRenderScaling(scale);
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(4);
        }
        var frame = window.CaptureRenderedFrame()!;
        window.Close();
        return frame;
    }

    [AvaloniaFact]
    public async Task Render_the_own_icons_beside_the_material_ones()
    {
        if (string.IsNullOrEmpty(Out))
        {
            return;
        }
        Directory.CreateDirectory(Out);
        FadenstackControls.EnsureLoaded();
        var app = Application.Current!;
        try
        {
            foreach (var (name, variant, choices) in new[]
            {
                ("light", ThemeVariant.Light, new ThemeChoices()),
                ("dark", ThemeVariant.Dark, new ThemeChoices()),
                ("highcontrast", ThemeVariant.Dark, new ThemeChoices { HighContrast = true }),
            })
            {
                app.RequestedThemeVariant = variant;
                FadenstackTheme.Apply(app, choices);
                foreach (var scale in new[] { 1.0, 2.0 })
                {
                    var frame = await Render(Sheet(name), scale);
                    frame.Save(Path.Combine(Out, $"icons-{name}-{scale:0}x.png"), new PngBitmapEncoderOptions());
                }
            }
        }
        finally
        {
            FadenstackTheme.Apply(app, new ThemeChoices());
            app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}

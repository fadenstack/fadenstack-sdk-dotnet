using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The tokens of doc 22 section 3: key constants, named accents per theme variant, sizes, and WCAG contrast.</summary>
public class ThemeTokenTests
{
    private static readonly string[] VariantNames = { "Light", "Dark", "HighContrast" };

    private static ThemeVariant Variant(string name) =>
        name switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => FadenstackTheme.HighContrastVariant };

    private static object Find(ChatHost host, string key, ThemeVariant variant)
    {
        Assert.True(host.View.TryFindResource(key, variant, out var value), key);
        return value!;
    }

    private static Color ColorOf(ChatHost host, string key, ThemeVariant variant) => ((ISolidColorBrush)Find(host, key, variant)).Color;

    private static double Lum(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>The colour as seen over a backdrop: the alpha composited.</summary>
    private static Color Over(Color top, Color back)
    {
        var a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + back.R * (1 - a)),
            (byte)Math.Round(top.G * a + back.G * (1 - a)),
            (byte)Math.Round(top.B * a + back.B * (1 - a)));
    }

    private static double Ratio(Color fg, Color bg)
    {
        var f = Lum(Over(fg, bg));
        var b = Lum(bg);
        return (Math.Max(f, b) + 0.05) / (Math.Min(f, b) + 0.05);
    }

    public static IEnumerable<object[]> AccentsAndModes() =>
        from a in Enum.GetValues<ThemeAccent>() from dark in new[] { false, true } select new object[] { a, dark };

    [AvaloniaTheory]
    [MemberData(nameof(AccentsAndModes))]
    public async Task Text_pairs_reach_4_5_and_non_text_pairs_3_for_every_accent_and_mode(ThemeAccent accent, bool dark)
    {
        using var host = await ChatHost.OpenAsync(1);
        FadenstackTheme.Apply(host.Window, new ThemeChoices { AccentName = accent });
        await host.PumpAsync();
        var v = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Color C(string k) => ColorOf(host, "Fadenstack." + k, v);
        var surface = C("Surface");
        var background = C("Background");

        void Text(string what, Color fg, Color bg) => Assert.True(Ratio(fg, bg) >= 4.5, $"{accent} {v}: {what} {Ratio(fg, bg):0.00}:1");
        Text("Foreground on Background", C("Foreground"), background);
        Text("Foreground on Surface", C("Foreground"), surface);
        Text("ForegroundMuted on Background", C("ForegroundMuted"), background);
        Text("ForegroundMuted on Surface", C("ForegroundMuted"), surface);
        Text("AccentForeground on Accent", C("AccentForeground"), C("Accent"));
        Text("AccentForeground on AccentHover", C("AccentForeground"), C("AccentHover"));
        Text("AccentText on Surface", C("AccentText"), surface);
        Text("NavSelectedForeground on NavSelected", C("NavSelectedForeground"), C("NavSelected"));

        void NonText(string what, Color fg) => Assert.True(Ratio(fg, surface) >= 3, $"{accent} {v}: {what} {Ratio(fg, surface):0.00}:1");
        NonText("Accent on Surface", C("Accent"));
        NonText("Focus on Surface", C("Focus"));
    }

    /// <summary>
    /// Outlines that identify a control reach 3:1 on the surface and the background (WCAG 2.2 SC 1.4.11), and error text
    /// 4.5:1, in both modes. Both are deliberately stronger than the console's values (desktop-agent/22 §3.1).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Control_outlines_reach_3_to_1_and_error_text_4_5_to_1(bool dark)
    {
        using var host = await ChatHost.OpenAsync(1);
        var v = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Color C(string key) => ColorOf(host, key, v);
        foreach (var back in new[] { FadenstackKeys.Surface, FadenstackKeys.Background })
        {
            Assert.True(Ratio(C(FadenstackKeys.BorderStrong), C(back)) >= 3, $"{v}: BorderStrong on {back}");
            Assert.True(Ratio(C(FadenstackKeys.DangerText), C(back)) >= 4.5, $"{v}: DangerText on {back}");
            Assert.True(Ratio(C(FadenstackKeys.SuccessText), C(back)) >= 4.5, $"{v}: SuccessText on {back}");
        }
    }

    /// <summary>
    /// The control styles' own pairs (design 22, section 5): tooltip text on the inverse fill 4.5:1, the hover outline of an
    /// input 3:1, a switch's knob on its off track 3:1, the placeholder (ForegroundMuted) on the field 4.5:1.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task The_control_styles_pairs_reach_their_contrast(string name)
    {
        using var host = await ChatHost.OpenAsync(1);
        var v = Variant(name);
        Color C(string key) => ColorOf(host, key, v);
        var surface = C(FadenstackKeys.Surface);
        Assert.True(Ratio(C(FadenstackKeys.InverseForeground), C(FadenstackKeys.Inverse)) >= 4.5, $"{name}: tooltip text");
        Assert.True(Ratio(C(FadenstackKeys.BorderHover), surface) >= 3, $"{name}: hover outline");
        Assert.True(Ratio(C(FadenstackKeys.ForegroundMuted), surface) >= 4.5, $"{name}: placeholder");
        var track = Over(C(FadenstackKeys.BorderStrong), surface);
        Assert.True(Ratio(C(FadenstackKeys.SwitchKnob), track) >= 1.3 || Ratio(track, surface) >= 3, $"{name}: switch off track");
    }

    [AvaloniaTheory]
    [MemberData(nameof(AccentsAndModes))]
    public async Task Apply_sets_the_named_accent_per_variant_and_follows_a_variant_switch(ThemeAccent accent, bool dark)
    {
        using var host = await ChatHost.OpenAsync(1);
        FadenstackTheme.Apply(host.Window, new ThemeChoices { AccentName = accent });
        await host.PumpAsync();
        var v = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var ramp = FadenstackTheme.Ramp(accent, dark);
        Color C(string k) => ColorOf(host, "Fadenstack." + k, v);
        Assert.Equal(ramp.Main, C("Accent"));
        Assert.Equal(ramp.Hover, C("AccentHover"));
        Assert.Equal(ramp.Light, C("AccentLight"));
        Assert.Equal(ramp.Foreground, C("AccentForeground"));
        Assert.Equal(ramp.Text, C("AccentText"));
        Assert.Equal(ramp.Text, C("Focus"));
        Assert.Equal(ramp.NavBackground, C("NavSelected"));
        Assert.Equal(ramp.NavForeground, C("NavSelectedForeground"));
        Assert.Equal(Color.FromArgb(31, ramp.Main.R, ramp.Main.G, ramp.Main.B), C("Hover"));
        Assert.Equal(Color.FromArgb(15, ramp.Main.R, ramp.Main.G, ramp.Main.B), C("Tint"));
        Assert.Equal(Color.FromArgb(51, ramp.Main.R, ramp.Main.G, ramp.Main.B), C("Pressed"));
        Assert.Equal(Color.FromArgb(77, ramp.Foreground.R, ramp.Foreground.G, ramp.Foreground.B), C("SplitDivider"));

        // The same Apply serves the other variant: no second call after the mode changes.
        host.Window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
        await host.PumpAsync();
        var other = FadenstackTheme.Ramp(accent, !dark);
        Assert.Equal(other.Main, ((ISolidColorBrush)Find(host, FadenstackKeys.Accent, host.View.ActualThemeVariant)).Color);
        Assert.Equal(other.Text, ((ISolidColorBrush)Find(host, FadenstackKeys.AccentText, host.View.ActualThemeVariant)).Color);
    }

    [AvaloniaFact]
    public async Task The_axaml_defaults_are_Frost_and_Apply_with_Frost_changes_nothing()
    {
        using var host = await ChatHost.OpenAsync(1);
        var before = new Dictionary<string, Color>();
        foreach (var dark in new[] { false, true })
        {
            foreach (var key in new[] { "Accent", "AccentHover", "AccentLight", "AccentForeground", "AccentText", "NavSelected", "NavSelectedForeground", "Hover", "Tint", "Pressed", "Focus", "SplitDivider" })
            {
                before[key + dark] = ColorOf(host, "Fadenstack." + key, dark ? ThemeVariant.Dark : ThemeVariant.Light);
            }
        }
        FadenstackTheme.Apply(host.Window, new ThemeChoices());
        await host.PumpAsync();
        foreach (var dark in new[] { false, true })
        {
            foreach (var key in new[] { "Accent", "AccentHover", "AccentLight", "AccentForeground", "AccentText", "NavSelected", "NavSelectedForeground", "Hover", "Tint", "Pressed", "Focus", "SplitDivider" })
            {
                Assert.Equal(before[key + dark], ColorOf(host, "Fadenstack." + key, dark ? ThemeVariant.Dark : ThemeVariant.Light));
            }
        }
    }

    [AvaloniaFact]
    public async Task A_custom_accent_overrides_the_named_one_and_keeps_text_readable()
    {
        using var host = await ChatHost.OpenAsync(1);
        FadenstackTheme.Apply(host.Window, new ThemeChoices { AccentName = ThemeAccent.Aurora, Accent = Color.Parse("#E8A317") });
        await host.PumpAsync();
        foreach (var dark in new[] { false, true })
        {
            var v = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Assert.Equal(Color.Parse("#E8A317"), ColorOf(host, FadenstackKeys.Accent, v));
            Assert.True(Ratio(ColorOf(host, FadenstackKeys.AccentText, v), ColorOf(host, FadenstackKeys.Surface, v)) >= 4.5);
            Assert.True(Ratio(ColorOf(host, FadenstackKeys.NavSelectedForeground, v), ColorOf(host, FadenstackKeys.NavSelected, v)) >= 4.5);
            Assert.True(Ratio(ColorOf(host, FadenstackKeys.AccentForeground, v), ColorOf(host, FadenstackKeys.Accent, v)) >= 4.5);
        }
    }

    [AvaloniaFact]
    public async Task Text_scale_multiplies_every_type_size_and_density_switches_the_sizes()
    {
        using var host = await ChatHost.OpenAsync(1);
        var sizes = new (string Key, double Base)[]
        {
            (FadenstackKeys.FontSizeMicro, 11), (FadenstackKeys.FontSizeCaption, 12), (FadenstackKeys.FontSizeSmall, 12),
            (FadenstackKeys.FontSizeBody, 14), (FadenstackKeys.FontSizeSubtitle, 16), (FadenstackKeys.FontSizeTitle, 20),
            (FadenstackKeys.FontSizeDisplay, 24), (FadenstackKeys.FontSizeHeading, 18), (FadenstackKeys.LineHeightBody, 20),
        };
        foreach (var (key, b) in sizes)
        {
            Assert.Equal(b, Find(host, key, ThemeVariant.Light));
        }
        Assert.Equal(36.0, Find(host, FadenstackKeys.ControlHeight, ThemeVariant.Light));
        Assert.Equal(48.0, Find(host, FadenstackKeys.TitleBarHeight, ThemeVariant.Light));
        Assert.Equal(28.0, Find(host, FadenstackKeys.StatusBarHeight, ThemeVariant.Light));

        FadenstackTheme.Apply(host.Window, new ThemeChoices { TextScale = 1.25, Density = ThemeDensity.Compact });
        await host.PumpAsync();
        foreach (var (key, b) in sizes)
        {
            Assert.Equal(b * 1.25, (double)Find(host, key, ThemeVariant.Light), 6);
        }
        Assert.Equal(32.0, Find(host, FadenstackKeys.ControlHeight, ThemeVariant.Light));
        Assert.Equal(44.0, Find(host, FadenstackKeys.TitleBarHeight, ThemeVariant.Light));
        Assert.Equal(24.0, Find(host, FadenstackKeys.StatusBarHeight, ThemeVariant.Light));
        // The fixed sizes do not scale.
        Assert.Equal(36.0, Find(host, FadenstackKeys.IconButtonSizeLarge, ThemeVariant.Light));
        Assert.Equal(380.0, Find(host, FadenstackKeys.SidePanelWidth, ThemeVariant.Light));
    }

    [AvaloniaFact]
    public async Task Shape_space_and_size_defaults_are_the_specified_ones()
    {
        using var host = await ChatHost.OpenAsync(1);
        var l = ThemeVariant.Light;
        Assert.Equal(new CornerRadius(12), Find(host, FadenstackKeys.Radius, l));
        Assert.Equal(new CornerRadius(999), Find(host, FadenstackKeys.RadiusPill, l));
        Assert.Equal(new Thickness(4), Find(host, FadenstackKeys.Space1, l));
        Assert.Equal(new Thickness(32), Find(host, FadenstackKeys.Space6, l));
        Assert.Equal(24.0, Find(host, FadenstackKeys.Space5Value, l));
        Assert.Equal(new[] { 36.0, 32.0, 28.0 }, new[] { Find(host, FadenstackKeys.IconButtonSizeLarge, l), Find(host, FadenstackKeys.IconButtonSizeNormal, l), Find(host, FadenstackKeys.IconButtonSizeSmall, l) }.Cast<double>());
        Assert.Equal(new[] { 20.0, 18.0, 16.0, 22.0 }, new[] { Find(host, FadenstackKeys.IconSizeLarge, l), Find(host, FadenstackKeys.IconSizeNormal, l), Find(host, FadenstackKeys.IconSizeSmall, l), Find(host, FadenstackKeys.IconSizeActivityBar, l) }.Cast<double>());
        Assert.Equal(new[] { 56.0, 380.0, 340.0, 480.0, 400.0 }, new[] { Find(host, FadenstackKeys.ActivityBarWidth, l), Find(host, FadenstackKeys.SidePanelWidth, l), Find(host, FadenstackKeys.BubbleWidth, l), Find(host, FadenstackKeys.SheetWidth, l), Find(host, FadenstackKeys.DrawerWidth, l) }.Cast<double>());
        var light = (BoxShadows)Find(host, FadenstackKeys.Elevation, l);
        var dark = (BoxShadows)Find(host, FadenstackKeys.Elevation, ThemeVariant.Dark);
        Assert.Equal(8, light[0].OffsetY);
        Assert.Equal(24, light[0].Blur);
        Assert.Equal(41, light[0].Color.A);
        Assert.Equal(102, dark[0].Color.A);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task Every_published_key_resolves_in_every_variant(string name)
    {
        using var host = await ChatHost.OpenAsync(1);
        var keys = typeof(FadenstackKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(keys.Count > 80);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var key in keys)
        {
            Assert.NotNull(Find(host, key, Variant(name)));
        }
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Brand_colours_do_not_follow_the_accent(string name)
    {
        using var host = await ChatHost.OpenAsync(1);
        var before = ColorOf(host, FadenstackKeys.BrandThread, Variant(name));
        FadenstackTheme.Apply(host.Window, new ThemeChoices { AccentName = ThemeAccent.Aurora });
        await host.PumpAsync();
        Assert.Equal(before, ColorOf(host, FadenstackKeys.BrandThread, Variant(name)));
        Assert.Equal(Color.Parse(name == "Light" ? "#E2325A" : "#FF4D6D"), before);
    }
}

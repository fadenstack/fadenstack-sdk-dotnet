using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The WPF skin's colours (Themes/Colors.*.xaml, copied next to the tests): every text at 4.5:1 or more on every
/// background it sits on, in every state (hover, pressed), and icons, marks, focus rings and the scroll thumb at 3:1 or
/// more on theirs (WCAG 2.2 AA, 1.4.3 and 1.4.11). The pairs are where Chat.xaml, ChatView.xaml and MarkdownText use
/// each brush.
/// </summary>
public class ThemeContrastTests
{
    private static readonly (string Foreground, string[] Backgrounds)[] Texts =
    {
        ("Foreground", new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed", "CodeBackground", "CodeInlineBackground",
            "UserBubble", "InfoBackground", "WarningBackground", "BlockedBackground", "AddedBackground", "RemovedBackground" }),
        ("ForegroundMuted", new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed", "CodeBackground", "CodeInlineBackground",
            "InfoBackground", "WarningBackground", "BlockedBackground" }),
        ("Link", new[] { "Background", "Surface" }), // links are never on a hover or pressed fill
        ("AccentForeground", new[] { "Accent", "AccentHover" }),
        ("Danger", new[] { "Background" }),
    };

    private static readonly (string Mark, string[] Backgrounds)[] Marks =
    {
        ("AccentIcon", new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed", "InfoBackground" }),
        ("Warning", new[] { "Background", "Surface", "Hover", "WarningBackground" }),
        ("Danger", new[] { "Background", "Hover", "BlockedBackground" }),
        ("Success", new[] { "Background", "Hover" }),
        ("Focus", new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed" }),
        ("Foreground", new[] { "Accent" }), // the primary button's focus ring
        ("ScrollThumb", new[] { "Background", "Surface" }),
        ("ScrollThumbHover", new[] { "Background", "Surface" }),
    };

    public static TheoryData<string> Themes() => new() { "Light", "Dark", "DarkGray" };

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_text_reads_at_4_5_to_1_on_what_it_sits_on(string theme)
    {
        var colours = Palette(theme);
        var failing = Texts.SelectMany(t => t.Backgrounds.Select(b => (t.Foreground, b)))
            .Select(p => (Pair: $"{p.Foreground} on {p.b}", Ratio: Ratio(colours[p.Foreground], colours[p.b])))
            .Where(p => p.Ratio < 4.5).Select(p => $"{p.Pair}: {p.Ratio:0.00}").ToList();
        Assert.Empty(failing);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Icons_marks_focus_rings_and_the_scroll_thumb_stand_out_at_3_to_1(string theme)
    {
        var colours = Palette(theme);
        var failing = Marks.SelectMany(m => m.Backgrounds.Select(b => (m.Mark, b)))
            .Select(p => (Pair: $"{p.Mark} on {p.b}", Ratio: Ratio(colours[p.Mark], colours[p.b])))
            .Where(p => p.Ratio < 3).Select(p => $"{p.Pair}: {p.Ratio:0.00}").ToList();
        Assert.Empty(failing);
    }

    [Fact]
    public void Every_theme_has_the_same_colour_keys()
    {
        var light = Palette("Light").Keys.OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(light, Palette("Dark").Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(light, Palette("DarkGray").Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static Dictionary<string, string> Palette(string theme)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", $"Colors.{theme}.xaml"));
        return Regex.Matches(xaml, "x:Key=\"Fadenstack\\.([\\w.]+)\" Color=\"#([0-9A-Fa-f]{6,8})\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    /// <summary>WCAG's contrast ratio of two opaque colours (RRGGBB, or AARRGGBB with the alpha left out).</summary>
    private static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex = hex.Substring(hex.Length - 6);
        double Channel(int i)
        {
            var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>How tall controls are: <see cref="Comfortable"/> (36 px) or <see cref="Compact"/> (32 px); the title and status bars follow.</summary>
public enum ThemeDensity
{
    Comfortable,
    Compact,
}

/// <summary>The named accents of the console's theme (doc 22 section 3.2). Each has its own ramp per theme variant (light, dark).</summary>
public enum ThemeAccent
{
    /// <summary>A muted steel blue; the default.</summary>
    Frost,

    /// <summary>No hue at all: near-black on light, near-white on dark.</summary>
    Graphite,

    /// <summary>The Fadenstack violet.</summary>
    Aurora,
}

/// <summary>What the person chose for the look of the app; <see cref="FadenstackTheme.Apply"/> puts it on screen.</summary>
public sealed class ThemeChoices
{
    /// <summary>
    /// A named accent (Frost by default). The ramps for the light and the dark variant are a fixed table, so the accent stays
    /// correct when the theme variant changes. The light/dark mode itself is the host's <c>RequestedThemeVariant</c>.
    /// </summary>
    public ThemeAccent AccentName { get; set; } = ThemeAccent.Frost;

    /// <summary>
    /// A custom accent colour that overrides <see cref="AccentName"/>, or null to use the named accent. The hover, foreground,
    /// icon, text and navigation colours are derived from it (text and navigation colours darkened or lightened until they reach
    /// 4.5:1); the colour itself is used as given, so a custom colour is not checked for 3:1 on the surface.
    /// </summary>
    public Color? Accent { get; set; }

    /// <summary>Comfortable (control height 36) or compact (32).</summary>
    public ThemeDensity Density { get; set; } = ThemeDensity.Comfortable;

    /// <summary>The text size as a factor of the default (1.0), from 0.8 to 2.0; values outside are clamped.</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>Black and white with a yellow accent and focus: the <see cref="FadenstackTheme.HighContrastVariant"/> theme variant.</summary>
    public bool HighContrast { get; set; }
}

/// <summary>
/// The theme keys every Fadenstack view reads through <c>DynamicResource</c> (<c>Fadenstack.*</c>, named by
/// <see cref="FadenstackKeys"/>): colours, the radius, spaces, sizes and the font sizes. <c>Colors.axaml</c> holds the
/// defaults (Frost); <see cref="Apply"/> changes them at runtime, so views follow without a restart.
/// </summary>
public static class FadenstackTheme
{
    private const double MinScale = 0.8;
    private const double MaxScale = 2.0;

    /// <summary>
    /// High contrast: a custom theme variant that inherits from <see cref="ThemeVariant.Dark"/>, so a key the high contrast
    /// dictionary of <c>Colors.axaml</c> does not define falls back to Dark's.
    /// </summary>
    public static readonly ThemeVariant HighContrastVariant = new("HighContrast", ThemeVariant.Dark);

    // The accent-derived keys, without the "Fadenstack." prefix.
    private static readonly string[] AccentKeys =
    {
        "Accent", "AccentHover", "AccentLight", "AccentForeground", "AccentText", "AccentIcon", "NavSelected",
        "NavSelectedForeground", "Hover", "Tint", "Pressed", "Link", "Focus", "UserBubble", "SplitDivider",
    };

    private static readonly Color LightSurface = Color.Parse("#FFFFFF");
    private static readonly Color DarkSurface = Color.Parse("#111827");

    // The variant a target had before high contrast, to give back when it is switched off.
    private static readonly ConditionalWeakTable<object, StrongBox<ThemeVariant>> Before = new();

    /// <summary>One accent in one mode: the console's ramp (doc 22 section 3.2).</summary>
    internal readonly record struct AccentRamp(Color Main, Color Hover, Color Light, Color Foreground, Color Text, Color NavBackground, Color NavForeground);

    /// <summary>The named accents' ramps for a theme variant, as the console's <c>theme.ts</c> has them.</summary>
    internal static AccentRamp Ramp(ThemeAccent accent, bool dark)
    {
        static Color C(string hex) => Color.Parse(hex);
        return (accent, dark) switch
        {
            (ThemeAccent.Graphite, false) => new(C("#1F2937"), C("#111827"), C("#4B5563"), C("#FFFFFF"), C("#1F2937"), C("#1F2937"), C("#FFFFFF")),
            (ThemeAccent.Graphite, true) => new(C("#E5E7EB"), C("#D1D5DB"), C("#F3F4F6"), C("#111827"), C("#E5E7EB"), C("#374151"), C("#FFFFFF")),
            (ThemeAccent.Aurora, false) => new(C("#7C4DFF"), C("#3F1DCB"), C("#B47CFF"), C("#FFFFFF"), C("#3F1DCB"), C("#3F1DCB"), C("#FFFFFF")),
            (ThemeAccent.Aurora, true) => new(C("#7C4DFF"), C("#3F1DCB"), C("#B47CFF"), C("#FFFFFF"), C("#B47CFF"), C("#3F1DCB"), C("#FFFFFF")),
            (_, false) => new(C("#4C6A92"), C("#3B5478"), C("#81A1C1"), C("#FFFFFF"), C("#3B5478"), C("#3B5478"), C("#FFFFFF")),
            (_, true) => new(C("#81A1C1"), C("#6F8FB0"), C("#A9C0D8"), C("#0A0E1A"), C("#A9C0D8"), C("#4C6A92"), C("#FFFFFF")),
        };
    }

    /// <summary>
    /// Sets the person's choices on <paramref name="target"/> (an <see cref="Application"/>, a window or another element, or
    /// a <see cref="ThemeVariantScope"/>): the resources are set on it, so views below it that use <c>DynamicResource</c>
    /// follow at once. The accent is written into the target's Light and Dark theme dictionaries, so it stays right when the
    /// theme variant changes, without another call. High contrast needs a target that takes a theme variant (an
    /// <see cref="Application"/>, a <see cref="TopLevel"/> or a <see cref="ThemeVariantScope"/>). With high contrast on, the
    /// accent choice is not used.
    /// </summary>
    public static void Apply(IResourceHost target, ThemeChoices choices)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(choices);
        var resources = target switch
        {
            Application a => a.Resources,
            StyledElement e => e.Resources,
            _ => throw new ArgumentException("The target must be an Application or a StyledElement.", nameof(target)),
        };

        var scale = Math.Clamp(double.IsNaN(choices.TextScale) ? 1.0 : choices.TextScale, MinScale, MaxScale);
        var compact = choices.Density == ThemeDensity.Compact;
        resources["Fadenstack.FontSize.Micro"] = 11 * scale;
        resources["Fadenstack.FontSize.Caption"] = 12 * scale;
        resources["Fadenstack.FontSize.Small"] = 12 * scale;
        resources["Fadenstack.FontSize.Body"] = 14 * scale;
        resources["Fadenstack.FontSize.Subtitle"] = 16 * scale;
        resources["Fadenstack.FontSize.Title"] = 20 * scale;
        resources["Fadenstack.FontSize.Display"] = 24 * scale;
        resources["Fadenstack.FontSize.Heading"] = 18 * scale;
        resources["Fadenstack.LineHeight.Body"] = 20 * scale;
        resources["Fadenstack.ControlHeight"] = compact ? 32.0 : 36.0;
        resources["Fadenstack.TitleBar.Height"] = compact ? 44.0 : 48.0;
        resources["Fadenstack.StatusBar.Height"] = compact ? 24.0 : 28.0;
        resources["Fadenstack.Radius"] = new CornerRadius(12);
        resources["Fadenstack.Space.Small"] = new Thickness(4);
        resources["Fadenstack.Space.Medium"] = new Thickness(8);
        resources["Fadenstack.Space.Large"] = new Thickness(16);

        if (resources is ResourceDictionary dictionary)
        {
            foreach (var (variant, dark) in new[] { (ThemeVariant.Light, false), (ThemeVariant.Dark, true) })
            {
                ResourceDictionary themed;
                if (dictionary.ThemeDictionaries.TryGetValue(variant, out var existing) && existing is ResourceDictionary found)
                {
                    themed = found;
                    foreach (var key in AccentKeys)
                    {
                        themed.Remove("Fadenstack." + key);
                    }
                }
                else
                {
                    themed = new ResourceDictionary();
                    dictionary.ThemeDictionaries[variant] = themed;
                }
                if (!choices.HighContrast)
                {
                    WriteAccent(themed, choices, dark);
                }
            }
        }

        SetHighContrast(target, choices.HighContrast);
    }

    private static void WriteAccent(ResourceDictionary into, ThemeChoices choices, bool dark)
    {
        var ramp = choices.Accent is { } custom ? Custom(custom, dark) : Ramp(choices.AccentName, dark);
        static SolidColorBrush Brush(Color c) => new(c);
        static SolidColorBrush Alpha(Color c, double a) => new(Color.FromArgb((byte)Math.Round(255 * a, MidpointRounding.AwayFromZero), c.R, c.G, c.B));
        into["Fadenstack.Accent"] = Brush(ramp.Main);
        into["Fadenstack.AccentHover"] = Brush(ramp.Hover);
        into["Fadenstack.AccentLight"] = Brush(ramp.Light);
        into["Fadenstack.AccentForeground"] = Brush(ramp.Foreground);
        into["Fadenstack.AccentText"] = Brush(ramp.Text);
        into["Fadenstack.AccentIcon"] = Brush(choices.Accent is null ? ramp.Main : Derive(ramp.Main).Icon);
        into["Fadenstack.NavSelected"] = Brush(ramp.NavBackground);
        into["Fadenstack.NavSelectedForeground"] = Brush(ramp.NavForeground);
        into["Fadenstack.Hover"] = Alpha(ramp.Main, 0.12);
        into["Fadenstack.Tint"] = Alpha(ramp.Main, 0.06);
        into["Fadenstack.Pressed"] = Alpha(ramp.Main, 0.20);
        into["Fadenstack.Link"] = Brush(ramp.Text);
        into["Fadenstack.Focus"] = Brush(ramp.Text);
        into["Fadenstack.UserBubble"] = Alpha(ramp.Main, 0.12);
        into["Fadenstack.SplitDivider"] = Alpha(ramp.Foreground, 0.30);
    }

    /// <summary>The ramp of a custom accent colour in a mode: the main colour as given, the text and navigation colours at 4.5:1.</summary>
    private static AccentRamp Custom(Color accent, bool dark)
    {
        var (hover, foreground, _) = Derive(accent);
        var surface = dark ? DarkSurface : LightSurface;
        var text = dark ? Lighten(accent, surface, 4.5) : Darken(accent, surface, 4.5);
        return new AccentRamp(accent, hover, Mix(accent, Colors.White, 0.35), foreground, text, Darken(accent, Colors.White, 4.5), Colors.White);
    }

    private static Color Darken(Color c, Color against, double ratio)
    {
        for (var i = 0; i < 20 && Contrast(Luminance(c), Luminance(against)) < ratio; i++)
        {
            c = Mix(c, Colors.Black, 0.1);
        }
        return c;
    }

    private static Color Lighten(Color c, Color against, double ratio)
    {
        for (var i = 0; i < 20 && Contrast(Luminance(c), Luminance(against)) < ratio; i++)
        {
            c = Mix(c, Colors.White, 0.1);
        }
        return c;
    }

    /// <summary>
    /// Puts the defaults (<c>Colors.axaml</c>) on the application when nothing there or in the host's own resources has them,
    /// so a chat view works alone and a host's <see cref="Apply"/> on a window or the application wins over them.
    /// </summary>
    internal static void EnsureDefaults()
    {
        if (Application.Current is not { } app || app.Resources.TryGetResource("Fadenstack.Radius", null, out _))
        {
            return;
        }
        var baseUri = new Uri("avares://Fadenstack.UI.Avalonia/");
        app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://Fadenstack.UI.Avalonia/Themes/Colors.axaml") });
    }

    /// <summary>The accent's hover (a little darker, or lighter on a dark accent), the text on it (black or white) and its icon colour.</summary>
    internal static (Color Hover, Color Foreground, Color Icon) Derive(Color accent)
    {
        var luminance = Luminance(accent);
        var hover = luminance > 0.1 ? Mix(accent, Colors.Black, 0.12) : Mix(accent, Colors.White, 0.12);
        var foreground = Contrast(luminance, 1.0) >= Contrast(luminance, 0.0) ? Colors.White : Colors.Black;
        var icon = luminance < 0.15 ? Mix(accent, Colors.White, 0.25) : accent;
        return (hover, foreground, icon);
    }

    private static void SetHighContrast(IResourceHost target, bool on)
    {
        ThemeVariant current;
        Action<ThemeVariant> set;
        switch (target)
        {
            case Application a:
                current = a.RequestedThemeVariant ?? ThemeVariant.Default;
                set = v => a.RequestedThemeVariant = v;
                break;
            case TopLevel t:
                current = t.RequestedThemeVariant ?? ThemeVariant.Default;
                set = v => t.RequestedThemeVariant = v;
                break;
            case ThemeVariantScope s:
                current = s.RequestedThemeVariant ?? ThemeVariant.Default;
                set = v => s.RequestedThemeVariant = v;
                break;
            default:
                if (on)
                {
                    throw new ArgumentException("High contrast needs an Application, a TopLevel or a ThemeVariantScope as the target.", nameof(target));
                }
                return;
        }
        if (on)
        {
            if (current != HighContrastVariant)
            {
                Before.AddOrUpdate(target, new StrongBox<ThemeVariant>(current));
                set(HighContrastVariant);
            }
        }
        else if (current == HighContrastVariant)
        {
            set(Before.TryGetValue(target, out var before) ? before.Value ?? ThemeVariant.Default : ThemeVariant.Default);
        }
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}

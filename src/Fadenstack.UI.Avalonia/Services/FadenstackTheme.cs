using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>How tall controls are: <see cref="Comfortable"/> (32 px) or <see cref="Compact"/> (28 px).</summary>
public enum ThemeDensity
{
    Comfortable,
    Compact,
}

/// <summary>What the person chose for the look of the app; <see cref="FadenstackTheme.Apply"/> puts it on screen.</summary>
public sealed class ThemeChoices
{
    /// <summary>The accent colour, or null for the default blue. Its hover, foreground and icon colours are derived.</summary>
    public Color? Accent { get; set; }

    public ThemeDensity Density { get; set; } = ThemeDensity.Comfortable;

    /// <summary>The text size as a factor of the default (1.0), from 0.8 to 2.0; values outside are clamped.</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>Black and white with a yellow accent and focus: the <see cref="FadenstackTheme.HighContrastVariant"/> theme variant.</summary>
    public bool HighContrast { get; set; }
}

/// <summary>
/// The theme keys every Fadenstack view reads through <c>DynamicResource</c> (<c>Fadenstack.*</c>): colours, the radius,
/// spaces, the control height and the font sizes. <c>Colors.axaml</c> holds the defaults; <see cref="Apply"/> changes
/// them at runtime, so views follow without a restart.
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

    private static readonly string[] AccentKeys =
    {
        "Fadenstack.Accent", "Fadenstack.AccentHover", "Fadenstack.AccentForeground", "Fadenstack.AccentIcon",
    };

    // The variant a target had before high contrast, to give back when it is switched off.
    private static readonly ConditionalWeakTable<object, StrongBox<ThemeVariant>> Before = new();

    /// <summary>
    /// Sets the person's choices on <paramref name="target"/> (an <see cref="Application"/>, a window or another element, or
    /// a <see cref="ThemeVariantScope"/>): the resources are set on it, so views below it that use <c>DynamicResource</c>
    /// follow at once. High contrast needs a target that takes a theme variant (an <see cref="Application"/>, a
    /// <see cref="TopLevel"/> or a <see cref="ThemeVariantScope"/>). With high contrast on, the accent choice is not used.
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
        resources["Fadenstack.FontSize.Small"] = 12 * scale;
        resources["Fadenstack.FontSize.Body"] = 14 * scale;
        resources["Fadenstack.FontSize.Title"] = 20 * scale;
        resources["Fadenstack.ControlHeight"] = choices.Density == ThemeDensity.Compact ? 28.0 : 32.0;
        resources["Fadenstack.Radius"] = new CornerRadius(12);
        resources["Fadenstack.Space.Small"] = new Thickness(4);
        resources["Fadenstack.Space.Medium"] = new Thickness(8);
        resources["Fadenstack.Space.Large"] = new Thickness(16);

        if (choices.Accent is { } accent && !choices.HighContrast)
        {
            var (hover, foreground, icon) = Derive(accent);
            resources["Fadenstack.Accent"] = new SolidColorBrush(accent);
            resources["Fadenstack.AccentHover"] = new SolidColorBrush(hover);
            resources["Fadenstack.AccentForeground"] = new SolidColorBrush(foreground);
            resources["Fadenstack.AccentIcon"] = new SolidColorBrush(icon);
        }
        else
        {
            foreach (var key in AccentKeys)
            {
                resources.Remove(key);
            }
        }

        SetHighContrast(target, choices.HighContrast);
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

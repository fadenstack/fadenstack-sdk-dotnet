using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The theme keys the desktop agent's modules use (its ThemeKeys), in Light, Dark and HighContrast, and FadenstackTheme.Apply.</summary>
public class ThemeKeyTests
{
    // Copied from the desktop agent's ThemeKeys: the SDK cannot reference that contract.
    private static readonly string[] Keys =
    {
        "Fadenstack.Background", "Fadenstack.Surface", "Fadenstack.SurfaceAlt", "Fadenstack.Border", "Fadenstack.Foreground",
        "Fadenstack.ForegroundMuted", "Fadenstack.Accent", "Fadenstack.AccentHover", "Fadenstack.AccentForeground",
        "Fadenstack.AccentIcon", "Fadenstack.Hover", "Fadenstack.Pressed", "Fadenstack.Focus", "Fadenstack.Link",
        "Fadenstack.Danger", "Fadenstack.Warning", "Fadenstack.Success", "Fadenstack.InfoBackground",
        "Fadenstack.WarningBackground", "Fadenstack.BlockedBackground", "Fadenstack.Scrim", "Fadenstack.Radius",
        "Fadenstack.Space.Small", "Fadenstack.Space.Medium", "Fadenstack.Space.Large", "Fadenstack.ControlHeight",
        "Fadenstack.FontSize.Small", "Fadenstack.FontSize.Body", "Fadenstack.FontSize.Title", "Fadenstack.CodeFont",
    };

    private static object? Find(ChatHost host, string key, ThemeVariant? variant = null)
    {
        Assert.True(host.View.TryFindResource(key, variant ?? host.View.ActualThemeVariant, out var value), key);
        return value;
    }

    private static Color ColorOf(object? brush) => ((ISolidColorBrush)brush!).Color;

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task Every_key_of_the_desktop_contract_resolves_in_every_variant(string name)
    {
        using var host = await ChatHost.OpenAsync(2);
        var variant = name switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => FadenstackTheme.HighContrastVariant };
        foreach (var key in Keys)
        {
            Assert.NotNull(Find(host, key, variant));
        }
    }

    [AvaloniaFact]
    public async Task High_contrast_is_black_and_white_with_a_yellow_accent_and_focus()
    {
        using var host = await ChatHost.OpenAsync(2);
        var hc = FadenstackTheme.HighContrastVariant;
        Assert.Equal(Colors.Black, ColorOf(Find(host, "Fadenstack.Background", hc)));
        Assert.Equal(Colors.White, ColorOf(Find(host, "Fadenstack.Foreground", hc)));
        Assert.Equal(Colors.White, ColorOf(Find(host, "Fadenstack.Border", hc)));
        Assert.Equal(Colors.Yellow, ColorOf(Find(host, "Fadenstack.Accent", hc)));
        Assert.Equal(Colors.Yellow, ColorOf(Find(host, "Fadenstack.Focus", hc)));
        Assert.Equal(hc.InheritVariant, ThemeVariant.Dark);
    }

    [AvaloniaFact]
    public async Task The_defaults_are_unchanged()
    {
        using var host = await ChatHost.OpenAsync(2);
        Assert.Equal(new CornerRadius(12), Find(host, "Fadenstack.Radius"));
        Assert.Equal(new Thickness(4), Find(host, "Fadenstack.Space.Small"));
        Assert.Equal(new Thickness(8), Find(host, "Fadenstack.Space.Medium"));
        Assert.Equal(new Thickness(16), Find(host, "Fadenstack.Space.Large"));
        Assert.Equal(36.0, Find(host, "Fadenstack.ControlHeight"));
        Assert.Equal(12.0, Find(host, "Fadenstack.FontSize.Small"));
        Assert.Equal(14.0, Find(host, "Fadenstack.FontSize.Body"));
        Assert.Equal(20.0, Find(host, "Fadenstack.FontSize.Title"));
        Assert.Equal(Color.Parse("#4C6A92"), ColorOf(Find(host, "Fadenstack.Accent", ThemeVariant.Light)));
        Assert.Equal(Color.Parse("#81A1C1"), ColorOf(Find(host, "Fadenstack.Accent", ThemeVariant.Dark)));
        Assert.Equal(Color.Parse("#F5F7FB"), ColorOf(Find(host, "Fadenstack.Background", ThemeVariant.Light)));
    }

    [AvaloniaFact]
    public async Task Apply_changes_accent_density_text_size_and_contrast_at_runtime()
    {
        using var host = await ChatHost.OpenAsync(2);
        var small = host.View.GetVisualDescendants().OfType<TextBlock>().Where(t => t.FontSize == 12).ToList();
        Assert.NotEmpty(small);

        FadenstackTheme.Apply(host.Window, new ThemeChoices
        {
            Accent = Color.Parse("#B00020"),
            Density = ThemeDensity.Compact,
            TextScale = 1.5,
            HighContrast = false,
        });
        await host.PumpAsync();
        Assert.Equal(32.0, Find(host, "Fadenstack.ControlHeight"));
        Assert.Equal(18.0, Find(host, "Fadenstack.FontSize.Small"));
        Assert.Equal(21.0, Find(host, "Fadenstack.FontSize.Body"));
        Assert.Equal(30.0, Find(host, "Fadenstack.FontSize.Title"));
        Assert.Equal(Color.Parse("#B00020"), ColorOf(Find(host, "Fadenstack.Accent")));
        Assert.Equal(Colors.White, ColorOf(Find(host, "Fadenstack.AccentForeground")));
        Assert.NotEqual(Color.Parse("#B00020"), ColorOf(Find(host, "Fadenstack.AccentHover")));
        Assert.Contains(small, t => t.FontSize == 18);

        FadenstackTheme.Apply(host.Window, new ThemeChoices
        {
            Accent = Color.Parse("#B00020"),
            Density = ThemeDensity.Compact,
            TextScale = 1.5,
            HighContrast = true,
        });
        await host.PumpAsync();
        var scope = host.View.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Equal(FadenstackTheme.HighContrastVariant, scope.ActualThemeVariant);
        Assert.Equal(Colors.Black, ColorOf(Find(host, "Fadenstack.Background")));
        Assert.Equal(Colors.Yellow, ColorOf(Find(host, "Fadenstack.Accent")));

        FadenstackTheme.Apply(host.Window, new ThemeChoices());
        await host.PumpAsync();
        Assert.NotEqual(FadenstackTheme.HighContrastVariant, scope.ActualThemeVariant);
        Assert.Equal(36.0, Find(host, "Fadenstack.ControlHeight"));
        Assert.Equal(12.0, Find(host, "Fadenstack.FontSize.Small"));
        Assert.Equal(Color.Parse("#4C6A92"), ColorOf(Find(host, "Fadenstack.Accent", ThemeVariant.Light)));
    }

    [AvaloniaFact]
    public async Task Apply_on_the_application_reaches_the_chat_too()
    {
        using var host = await ChatHost.OpenAsync(2);
        try
        {
            FadenstackTheme.Apply(Application.Current!, new ThemeChoices { TextScale = 2.0, Density = ThemeDensity.Compact });
            await host.PumpAsync();
            Assert.Equal(24.0, Find(host, "Fadenstack.FontSize.Small"));
            Assert.Equal(32.0, Find(host, "Fadenstack.ControlHeight"));
        }
        finally
        {
            FadenstackTheme.Apply(Application.Current!, new ThemeChoices());
        }
    }

    [AvaloniaFact]
    public void The_text_scale_stays_between_0_8_and_2()
    {
        var window = new Window();
        FadenstackTheme.Apply(window, new ThemeChoices { TextScale = 9 });
        Assert.Equal(24.0, window.Resources["Fadenstack.FontSize.Small"]);
        FadenstackTheme.Apply(window, new ThemeChoices { TextScale = 0.1 });
        Assert.Equal(12 * 0.8, (double)window.Resources["Fadenstack.FontSize.Small"]!, 6);
    }
}

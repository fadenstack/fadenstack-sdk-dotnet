using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>
/// The bundled fonts (design 22, 3.4): Inter for all text (the Avalonia team's package), Noto Sans SC for Chinese,
/// JetBrains Mono for code and Sora for the wordmark (embedded in this assembly). All are SIL OFL 1.1.
/// </summary>
public static class FadenstackFonts
{
    /// <summary>The font collection of the files embedded in this assembly (Noto Sans SC, JetBrains Mono, Sora).</summary>
    public const string Collection = "fonts:Fadenstack";

    /// <summary>Inter, from the <c>Avalonia.Fonts.Inter</c> package.</summary>
    public const string Inter = "fonts:Inter#Inter";

    /// <summary>The Chinese fallback.</summary>
    public const string NotoSansSC = Collection + "#Noto Sans SC";

    /// <summary>The monospace face for code, tool arguments and codes.</summary>
    public const string JetBrainsMono = Collection + "#JetBrains Mono";

    /// <summary>The wordmark face (SemiBold only).</summary>
    public const string Sora = Collection + "#Sora";

    /// <summary>
    /// Registers the fonts and makes Inter the default family, with Noto Sans SC as the first fallback and the operating
    /// system's own fonts after it. Call it once while configuring the app, before <c>StartWith...</c>.
    /// </summary>
    public static AppBuilder UseFadenstackFonts(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithInterFont()
            .ConfigureFonts(manager => manager.AddFontCollection(new EmbeddedFontCollection(
                new Uri(Collection), new Uri("avares://Fadenstack.UI.Avalonia/Assets/Fonts"))))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = Inter,
                FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily(NotoSansSC) } },
            });
    }
}

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The bundled fonts (design 22, 3.4): registered by UseFadenstackFonts, resolved through the font keys.</summary>
public class FontTests
{
    /// <summary>The family names of the typefaces the text really shaped with (after the font manager's fallbacks).</summary>
    internal static List<string> ShapedFamilies(string text, FontFamily family, FontWeight weight = FontWeight.Normal)
    {
        var layout = new TextLayout(text, new Typeface(family, FontStyle.Normal, weight), 16, Brushes.Black, maxWidth: 1000);
        return layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().Select(r => r.GlyphRun.GlyphTypeface.FamilyName).ToList();
    }

    private static FontFamily Key(string key)
    {
        FadenstackAssets.EnsureLoaded();
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), key + " is missing");
        return Assert.IsType<FontFamily>(value);
    }

    [AvaloniaFact]
    public void Inter_is_the_default_family()
    {
        Assert.Equal(FadenstackFonts.Inter, FontManager.Current.DefaultFontFamily.ToString());
        // No family given: the default one, and German text with umlauts has glyphs in Inter itself.
        var families = ShapedFamilies("Größe über Äpfel, Straße 123", FontManager.Current.DefaultFontFamily);
        Assert.All(families, f => Assert.Equal("Inter", f));
    }

    [AvaloniaFact]
    public void Chinese_text_renders_with_Noto_Sans_SC_glyphs()
    {
        var layout = new TextLayout("你好，世界。汉字测试", new Typeface(Key("Fadenstack.FontFamily")), 16, Brushes.Black, maxWidth: 1000);
        var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().ToList();
        Assert.NotEmpty(runs);
        // The glyph typeface is Noto Sans SC (not Inter, which has no Han), and no glyph is the .notdef box (index 0).
        Assert.All(runs, r => Assert.Equal("Noto Sans SC", r.GlyphRun.GlyphTypeface.FamilyName));
        Assert.All(runs, r => Assert.DoesNotContain((ushort)0, r.GlyphRun.GlyphInfos.Select(g => g.GlyphIndex)));
    }

    [AvaloniaFact]
    public void Chinese_falls_back_to_Noto_even_with_only_Inter_asked_for()
    {
        var families = ShapedFamilies("中文", new FontFamily(FadenstackFonts.Inter));
        Assert.All(families, f => Assert.Equal("Noto Sans SC", f));
    }

    [AvaloniaFact]
    public void The_mono_key_resolves_to_JetBrains_Mono()
    {
        var mono = Key("Fadenstack.FontFamily.Mono");
        Assert.All(ShapedFamilies("var x = 42; // code", mono), f => Assert.Equal("JetBrains Mono", f));
        Assert.All(ShapedFamilies("var x = 42; // code", mono, FontWeight.Bold), f => Assert.Equal("JetBrains Mono", f));
        // Chinese inside code falls to Noto Sans SC.
        Assert.Contains("Noto Sans SC", ShapedFamilies("名称 = 1", mono));
    }

    [AvaloniaFact]
    public void The_text_key_is_Inter_with_Noto_behind_it()
    {
        Assert.All(ShapedFamilies("Hello Wörld", Key("Fadenstack.FontFamily")), f => Assert.Equal("Inter", f));
    }

    [AvaloniaTheory]
    [InlineData(FontWeight.Normal)]
    [InlineData(FontWeight.Medium)]
    [InlineData(FontWeight.Bold)]
    public void Noto_has_Regular_Medium_and_Bold(FontWeight weight)
    {
        var typeface = new Typeface(new FontFamily(FadenstackFonts.NotoSansSC), FontStyle.Normal, weight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.Equal("Noto Sans SC", glyphs.FamilyName);
        Assert.Equal(weight, glyphs.Weight);
    }

    [AvaloniaFact]
    public void Sora_SemiBold_is_there_for_the_wordmark()
    {
        var typeface = new Typeface(Key("Fadenstack.FontFamily.Wordmark"), FontStyle.Normal, FontWeight.SemiBold);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.StartsWith("Sora", glyphs.FamilyName);
    }
}

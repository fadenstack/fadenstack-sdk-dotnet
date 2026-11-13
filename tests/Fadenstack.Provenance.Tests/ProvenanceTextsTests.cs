using System.Globalization;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>
/// The module owns the words its marks show (sdk/52 P1): the same words in all five languages as before they moved out of
/// the UI layer, the title is the label, a space and the record id, and the reader knows every language's label.
/// </summary>
public class ProvenanceTextsTests
{
    private const string Id = "0193b5d2-7c4e-7a10-8f00-000000000001";

    [Theory]
    [InlineData("en", "AI-generated")]
    [InlineData("de", "KI-generiert")]
    [InlineData("fr", "Généré par IA")]
    [InlineData("es", "Generado por IA")]
    [InlineData("zh-Hans", "人工智能生成")]
    public void The_mark_texts_are_the_same_words_in_every_language(string language, string label)
    {
        var texts = ProvenanceTexts.For(CultureInfo.GetCultureInfo(language));

        Assert.Equal(label, texts.AiGenerated);
        Assert.Equal($"{label} {Id}", texts.AiGeneratedTitle(Id));
        Assert.Equal(label, ProvenanceTexts.For(language).AiGenerated);
    }

    [Fact]
    public void A_regional_language_finds_its_language_and_one_the_module_lacks_finds_English()
    {
        Assert.Equal("KI-generiert", ProvenanceTexts.For("de-AT").AiGenerated);
        Assert.Equal("人工智能生成", ProvenanceTexts.For("zh-CN").AiGenerated);
        Assert.Equal("AI-generated", ProvenanceTexts.For("ja").AiGenerated);
    }

    [Fact]
    public void No_language_or_an_unknown_tag_is_the_hosts_language()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            Assert.Equal("Généré par IA", ProvenanceTexts.For((string?)null).AiGenerated);
            Assert.Equal("Généré par IA", ProvenanceTexts.For("not a language!").AiGenerated);
            Assert.Equal("Généré par IA", ProvenanceTexts.Current.AiGenerated);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void A_host_gives_its_own_words_and_the_title_keeps_its_form()
    {
        var own = new ProvenanceTexts("Mit KI erstellt");

        Assert.Equal($"Mit KI erstellt {Id}", own.AiGeneratedTitle(Id));
        Assert.Throws<ArgumentException>(() => new ProvenanceTexts(" "));
    }

    [Fact]
    public void The_reader_knows_the_title_of_every_built_in_language()
    {
        Assert.Equal(new[] { "en", "de", "fr", "es", "zh-Hans" }, ProvenanceTexts.BuiltInLanguages);
        foreach (var language in ProvenanceTexts.BuiltInLanguages)
        {
            // The title alone, as an editor that drops data-* attributes leaves it.
            var html = $"<span title=\"{HtmlAttributeMarker.Title(ProvenanceTexts.For(language), Id)}\">Hello</span>";
            Assert.Equal(Id, Assert.Single(ProvenanceReader.Read(html, "text/html")).RecordId);
        }
    }
}

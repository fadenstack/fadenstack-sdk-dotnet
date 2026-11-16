using System.Globalization;
using Fadenstack.Governance;
using Fadenstack.UI.Parts;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The owner's note for a refusal with <c>images_disabled</c> (core 0.7.1, EU AI Act Article 5), in the panel's language.</summary>
public class ImagesDisabledNoticeTests
{
    private const string English = "Image generation is turned off in this version. From 2 December 2026 the EU AI Act (Article 5) requires safeguards against generating harmful images, and this version does not have them yet. Image generation will return in a later version, with those safeguards.";
    private const string German = "Die Bilderzeugung ist in dieser Version ausgeschaltet. Ab dem 2. Dezember 2026 verlangt die EU-KI-Verordnung (Artikel 5) Schutzmaßnahmen gegen das Erzeugen schädlicher Bilder, und diese Version hat sie noch nicht. Die Bilderzeugung kommt in einer späteren Version mit diesen Schutzmaßnahmen zurück.";

    private static readonly GovernanceNotice Notice = new()
    {
        Outcome = "images_disabled", Stage = "tool", Category = "images", Message = "The server's own words.",
    };

    private static UiStrings Strings(string tag) => UiStrings.For(CultureInfo.GetCultureInfo(tag));

    [Fact]
    public void The_notice_shows_the_owners_note_in_english_and_german_instead_of_the_servers_message()
    {
        var english = NoticePart.From(Notice, Strings("en"));
        Assert.Equal(("Image generation is off", English, NoticeSeverity.Warning), (english.Title, english.Message, english.Severity));

        var german = NoticePart.From(Notice, Strings("de"));
        Assert.Equal(("Bilderzeugung ist ausgeschaltet", German), (german.Title, german.Message));
    }

    [Fact]
    public void The_notice_changes_language_when_the_panel_does()
    {
        var part = NoticePart.From(Notice, Strings("en"));

        part.Retext(Strings("de"));
        Assert.Equal(("Bilderzeugung ist ausgeschaltet", German), (part.Title, part.Message));
        part.Retext(Strings("fr"));
        Assert.Contains("règlement européen sur l’IA (article 5)", part.Message);
        part.Retext(Strings("en"));
        Assert.Equal(English, part.Message);
    }

    [Theory]
    [InlineData("fr", "La génération d’images est désactivée", "règlement européen sur l’IA (article 5)")]
    [InlineData("es", "La generación de imágenes está desactivada", "Reglamento europeo de IA (artículo 5)")]
    [InlineData("zh-Hans", "图像生成已关闭", "欧盟《人工智能法》（第 5 条）")]
    public void The_other_languages_have_a_title_and_name_the_regulation_as_it_is_called_there(string tag, string title, string regulation)
    {
        var part = NoticePart.From(Notice, Strings(tag));

        Assert.Equal(title, part.Title);
        Assert.Contains(regulation, part.Message);
        Assert.Contains("2026", part.Message);
        Assert.NotEqual(English, part.Message);
    }
}

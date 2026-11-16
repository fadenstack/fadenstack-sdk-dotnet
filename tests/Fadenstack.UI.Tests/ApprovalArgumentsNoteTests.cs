using System.Globalization;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The card of a call the gateway holds: its arguments, and the note when they were cut or are not a JSON object.</summary>
public class ApprovalArgumentsNoteTests
{
    private static ApprovalPart Card(ApprovalRequest request, string culture = "en") =>
        new(request, ApprovalState.Pending, 200, new UiStrings(CultureInfo.GetCultureInfo(culture)));

    private static ApprovalRequest Held(IReadOnlyDictionary<string, object?> arguments, bool shortened = false) => new()
    {
        ToolName = "mcp.github.comment", ToolClass = ToolClass.Write, Source = "gateway", Arguments = arguments, ArgumentsShortened = shortened,
    };

    [Fact]
    public void Complete_arguments_show_as_the_apps_own_do_with_no_note()
    {
        var card = Card(Held(new Dictionary<string, object?> { ["title"] = JsonDocument.Parse("\"x\"").RootElement.Clone(), ["page"] = 2 }));

        Assert.Equal("title: x\npage: 2", card.ArgumentsText);
        Assert.Equal(("", false), (card.ArgumentsNote, card.HasArgumentsNote));
    }

    [Fact]
    public void A_shortened_text_shows_alone_with_the_note_never_beyond_the_cards_limit()
    {
        var card = Card(Held(new Dictionary<string, object?> { ["arguments"] = new string('x', 8000) }, shortened: true));

        Assert.StartsWith(new string('x', 200), card.ArgumentsText);
        Assert.True(card.ArgumentsText.Length < 400); // the card's preview limit, plus its "more characters" line
        Assert.Equal(("The arguments are shortened.", true), (card.ArgumentsNote, card.HasArgumentsNote));
    }

    [Fact]
    public void No_arguments_from_an_older_gateway_show_no_note()
    {
        var card = Card(Held(new Dictionary<string, object?>()));

        Assert.Equal(("", ""), (card.ArgumentsText, card.ArgumentsNote));
    }

    [Theory]
    [InlineData("en", "The arguments are shortened.")]
    [InlineData("de", "Die Argumente sind gekürzt.")]
    [InlineData("fr", "Les arguments sont abrégés.")]
    [InlineData("es", "Los argumentos están acortados.")]
    [InlineData("zh-Hans", "参数已被截短。")]
    public void The_note_is_in_five_languages(string culture, string expected)
    {
        var card = Card(Held(new Dictionary<string, object?> { ["arguments"] = "{\"ti" }, shortened: true), culture);

        Assert.Equal(expected, card.ArgumentsNote);
    }
}

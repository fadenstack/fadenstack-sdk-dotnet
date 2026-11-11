using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

public class UiStringsTests
{
    private static readonly ResourceManager Package = new("Fadenstack.UI.Resources.UiText", typeof(UiStrings).Assembly);
    private static readonly ResourceManager Host = new("Fadenstack.UI.Tests.HostTexts", typeof(UiStringsTests).Assembly);

    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    private static Dictionary<string, string> Texts(CultureInfo culture) =>
        Package.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!.Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    public static TheoryData<string> Translations() => new() { "de", "fr", "es", "zh-Hans" };

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_translation_has_every_text_with_the_same_placeholders(string language)
    {
        var english = Texts(CultureInfo.InvariantCulture);
        var translated = Texts(C(language));
        Assert.Equal(english.Keys.OrderBy(k => k), translated.Keys.OrderBy(k => k));
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).OrderBy(n => n));
        foreach (var (name, text) in english)
        {
            Assert.True(Holes(text) == Holes(translated[name]), $"{language} {name}: '{translated[name]}' fills other places than '{text}'");
        }
        // Translated, not left in English (a few words are the same in both, like "Source").
        var same = english.Where(e => translated[e.Key] == e.Value && !Regex.IsMatch(e.Value, @"^[\{\d:.,}\s\w]*(s|ms)$")).Select(e => e.Key).ToList();
        Assert.True(same.Count <= 6, $"{language} keeps English: {string.Join(", ", same)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void Every_text_the_code_asks_for_is_there(string language)
    {
        var strings = UiStrings.For(C(language));
        foreach (var property in typeof(UiStrings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.Name != nameof(UiStrings.Language)))
        {
            // A property reads the text of its own name.
            Assert.True(strings.Find(property.Name) is { Length: > 0 }, $"{language}: no text for {property.Name}");
            Assert.Equal(strings.Find(property.Name), property.GetValue(strings));
        }
        foreach (var method in typeof(UiStrings).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName && m.Name != nameof(UiStrings.Find) && m.Name != nameof(UiStrings.NoticeTitle)))
        {
            foreach (var args in Arguments(method))
            {
                var text = (string)method.Invoke(strings, args)!;
                Assert.False(text.Length == 0 || text.Contains('{') || Regex.IsMatch(text, @"^[A-Z][A-Za-z]+_[A-Za-z]+"), $"{language}: {method.Name}({string.Join(", ", args)}) = '{text}'");
            }
        }
        foreach (var outcome in new[] { "prompt_blocked", "response_blocked", "tool_denied", "rate_limited", "round_limit", "empty_answer" })
        {
            Assert.NotNull(strings.NoticeTitle(outcome));
        }
        Assert.Null(strings.NoticeTitle("a_new_outcome"));
    }

    /// <summary>Sample arguments for each of a method's parameters, every enum value once.</summary>
    private static IEnumerable<object[]> Arguments(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var enumParameter = parameters.FirstOrDefault(p => p.ParameterType.IsEnum);
        var values = enumParameter is null ? new object?[] { null } : Enum.GetValues(enumParameter.ParameterType).Cast<object?>().ToArray();
        foreach (var value in values)
        {
            yield return parameters.Select(p => p.ParameterType.IsEnum ? value! : Sample(p.ParameterType)).ToArray();
        }
    }

    private static object Sample(Type type) =>
        type == typeof(string) ? "read_page" : type == typeof(bool) ? true : type == typeof(int) ? 2 : type == typeof(long) ? 1234L : type == typeof(double) ? 3.0
        : type == typeof(TimeSpan) ? TimeSpan.FromSeconds(1.5) : throw new InvalidOperationException(type.Name);

    [Theory]
    [InlineData("de", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("de-CH", "de")]
    [InlineData("fr-CA", "fr")]
    [InlineData("es-MX", "es")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "en")] // Traditional is another script
    [InlineData("ja-JP", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("", "en")]
    public void A_culture_finds_the_nearest_language_there_are_texts_for(string culture, string language)
    {
        var strings = UiStrings.For(C(culture));
        Assert.Equal(language, strings.Language);
        Assert.False(strings.IsRightToLeft);
    }

    [Fact]
    public void Texts_come_in_the_language_asked_for()
    {
        Assert.Equal(("Zulassen", "Nachfragen", "add_comment ausgeführt"),
            (UiStrings.For(C("de-AT")).Allow, UiStrings.For(C("de")).ModeLabel(ToolMode.Ask), UiStrings.For(C("de")).ToolRan("add_comment")));
        Assert.Equal("Exécuter add_comment ?", UiStrings.For(C("fr-FR")).ApprovalTitle("add_comment"));
        Assert.Equal("¿Ejecutar add_comment?", UiStrings.For(C("es")).ApprovalTitle("add_comment"));
        Assert.Equal("已运行 add_comment", UiStrings.For(C("zh-CN")).ToolRan("add_comment"));
        Assert.Equal(("Used 1 reference", "Used 3 references"), (UiStrings.For(C("en")).UsedReferences(1), UiStrings.For(C("en")).UsedReferences(3)));
    }

    [Fact]
    public void The_chat_says_its_answers_come_from_an_ai_and_can_be_wrong_in_every_language()
    {
        Assert.Equal("Answers are generated by AI and can be wrong. Check what matters.", UiStrings.For(C("en")).AiNotice);
        Assert.Equal("Antworten werden von einer KI erzeugt und können falsch sein. Prüfen Sie Wichtiges.", UiStrings.For(C("de-CH")).AiNotice);
        Assert.Contains("IA", UiStrings.For(C("fr")).AiNotice);
        Assert.Contains("IA", UiStrings.For(C("es")).AiNotice);
        Assert.Contains("人工智能", UiStrings.For(C("zh-Hans")).AiNotice);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    public void The_notice_and_the_placeholder_never_leave_one_word_alone_on_their_last_line(string language)
    {
        var strings = UiStrings.For(C(language));
        foreach (var text in new[] { strings.AiNotice, strings.ComposerPlaceholder })
        {
            Assert.True(text.LastIndexOf(' ') > text.LastIndexOf(' '), $"'{text}' ends with a breaking space before its last word");
        }
    }

    [Fact]
    public void A_host_adds_a_language_and_changes_words_and_the_package_has_the_rest()
    {
        var english = new UiStrings(C("en-US"), Host);
        Assert.Equal(("Allow once", "Send", "en"), (english.Allow, english.Send, english.Language));

        var german = new UiStrings(C("de-DE"), Host); // the package's German is nearer than the host's English
        Assert.Equal(("Zulassen", "Senden", "de"), (german.Allow, german.Send, german.Language));

        var italian = new UiStrings(C("it-IT"), Host);
        Assert.Equal(("Invia", "read_page eseguito", "Allow once", "Deny", "it"),
            (italian.Send, italian.ToolRan("read_page"), italian.Allow, italian.Deny, italian.Language));
        Assert.Equal("Invia", italian["Send"]);
        Assert.Equal("NoSuchText", italian["NoSuchText"]);
        Assert.Null(italian.Find("NoSuchText"));
    }

    [Fact]
    public void Numbers_and_dates_follow_the_format_culture_and_words_the_language()
    {
        Assert.Equal("1.234 tokens", new UiStrings(C("en"), formatCulture: C("de-DE")).Tokens(1234));
        Assert.Equal("1,234 Tokens", new UiStrings(C("de"), formatCulture: C("en-US")).Tokens(1234));
        Assert.Equal("1,5 s", new UiStrings(C("de"), formatCulture: C("de-DE")).Duration(TimeSpan.FromSeconds(1.5)));
        Assert.Equal("350 ms", UiStrings.For(C("fr")).Duration(TimeSpan.FromMilliseconds(350)));
    }

    [Fact]
    public async Task A_chat_in_German_says_everything_in_German()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Erledigt.");
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTools(new PageTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance,
            new AgentChatOptions { RenderInterval = TimeSpan.Zero, Strings = UiStrings.For(C("de-DE")) });
        await chat.InitializeAsync();
        Assert.Equal("Was möchten Sie Docs fragen?", chat.Greeting);
        Assert.Equal(new[] { "Aus", "Nur lesen", "Nachfragen", "Automatisch" }, chat.ToolModes.Select(m => m.Label));
        Assert.Equal("Zulassen", chat.Strings.Allow);

        var sending = chat.SendAsync("Kommentiere Seite 2.");
        ApprovalPart? card = null;
        for (var i = 0; i < 500 && card is null; i++)
        {
            await Task.Delay(10);
            card = chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault();
        }
        Assert.NotNull(card);
        Assert.Equal(("add_comment ausführen?", "Ändert Daten", "Es ändert etwas, das sich rückgängig machen lässt."),
            (card!.Title, card.ClassLabel, card.Reason));
        Assert.Equal("add_comment wartet auf Ihre Freigabe", chat.Turns[0].Parts.OfType<ToolCallPart>().Single().Title);
        card.AllowCommand.Execute(null);
        await sending;
        Assert.Equal(("Zugelassen", "add_comment ausgeführt"), (card.StateText, chat.Turns[0].Parts.OfType<ToolCallPart>().Single().Title));
        Assert.Equal("Kommentiere Seite 2.", chat.Sessions[0].Title);
    }

    [Fact]
    public void The_default_is_the_current_ui_language()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = C("es-ES");
            Assert.Equal(("es", "Permitir"), (UiStrings.Current.Language, UiStrings.Current.Allow));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}

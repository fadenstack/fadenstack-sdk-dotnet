using System.Globalization;
using System.Reflection;
using Xunit;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The Tools view's texts (sdk/51 F05): every new member has a text in all five languages. (The general completeness tests in
/// <c>UiStringsTests</c> go over every member; these name the Tools ones, so that a member left out of a language is found here.)
/// </summary>
public class ToolsTextsTests
{
    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    private static IEnumerable<PropertyInfo> ToolsProperties() => typeof(UiStrings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && (p.Name.StartsWith("Tools", StringComparison.Ordinal) || p.Name.StartsWith("McpPlainHttp", StringComparison.Ordinal)));

    private static IEnumerable<MethodInfo> ToolsMethods() => typeof(UiStrings).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName && (m.Name.StartsWith("Tools", StringComparison.Ordinal) || m.Name.StartsWith("McpPlainHttp", StringComparison.Ordinal)));

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void Every_Tools_text_has_a_value_in_every_language(string language)
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        var properties = ToolsProperties().ToList();
        var methods = ToolsMethods().ToList();

        Assert.True(properties.Count > 100 && methods.Count > 30, "the Tools members were not found");
        foreach (var property in properties)
        {
            Assert.True(strings.Find(property.Name) is { Length: > 0 }, $"{language}: no text for {property.Name}");
        }
        foreach (var method in methods)
        {
            Assert.True(strings.Find(method.Name) is { Length: > 0 }, $"{language}: no text for {method.Name}");
        }
    }

    [Fact]
    public void The_five_plain_http_texts_and_the_two_problems_are_translated_not_left_in_english()
    {
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        foreach (var language in new[] { "de", "fr", "es", "zh-Hans" })
        {
            var translated = UiStrings.For(CultureInfo.GetCultureInfo(language));
            foreach (var name in new[]
            {
                "McpPlainHttpSwitch", "McpPlainHttpSwitchHelp", "McpPlainHttpWarningTitle", "McpPlainHttpWarning", "McpPlainHttpConfirm", "McpPlainHttpCancel",
                "McpPlainHttpNoOptIn", "McpPlainHttpNotLocal", "McpPlainHttpSecret", "McpPlainHttpNotAllowed",
                "ToolsProblemPlainHttpWithHeaders", "ToolsProblemPlainHttpNotLocal", "ToolsTrusted", "ToolsTrustedHelp", "ToolsClassDestructive", "ToolsAdvanced",
            })
            {
                Assert.NotEqual(english.Find(name), translated.Find(name));
            }
        }
    }

    [Fact]
    public void The_warnings_version_is_the_date_its_words_were_set()
    {
        Assert.Equal("2026-11-15", UiStrings.McpPlainHttpWarningVersion);
        Assert.True(DateTime.TryParseExact(UiStrings.McpPlainHttpWarningVersion, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }
}

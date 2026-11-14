using System.Globalization;
using System.Text.RegularExpressions;
using Fadenstack.SkillAuthoring;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The skill editor's texts exist in all five languages, with the same places filled, and say what the checks mean.</summary>
public class SkillTextsTests
{
    public static TheoryData<string> Languages() => new() { "en", "de", "fr", "es", "zh-Hans" };

    private static UiStrings In(string language) => UiStrings.For(CultureInfo.GetCultureInfo(language));

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_check_has_a_text_in_every_language_with_its_numbers_and_detail_filled_in(string language)
    {
        var strings = In(language);

        foreach (var code in Enum.GetValues(typeof(SkillIssueCode)).Cast<SkillIssueCode>())
        {
            var text = strings.SkillIssueText(code, "references/a.md", 1234, 5678, "DETAIL");

            Assert.True(strings.Find("SkillIssue" + code) is { Length: > 0 }, $"{language}: no text for {code}");
            Assert.DoesNotContain("{", text);
            Assert.False(Regex.IsMatch(text, @"^SkillIssue"), $"{language}: {code} shows its name");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_limits_the_checks_name_are_the_ones_the_validator_enforces(string language)
    {
        var strings = In(language);

        var name = strings.SkillIssueText(SkillIssueCode.NameTooLong, "name", SkillLimits.NameMax, 65, "");
        var description = strings.SkillIssueText(SkillIssueCode.DescriptionTooLong, "description", SkillLimits.DescriptionMax, 1025, "");

        Assert.Contains("64", name);
        Assert.Contains("65", name);
        Assert.Contains("1024", description);
        Assert.Contains("U+200B", strings.SkillIssueText(SkillIssueCode.InvisibleCharacter, "description", 0, 0, "U+200B"));
        Assert.Contains("version", strings.SkillIssueText(SkillIssueCode.UnknownKey, "SKILL.md", 0, 0, "version"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_labels_of_origins_matches_and_activations_are_all_different_words(string language)
    {
        var strings = In(language);

        var origins = new[] { SkillOrigin.Bundled, SkillOrigin.Own, SkillOrigin.Organisation }.Select(strings.SkillOriginLabel).ToList();
        var matches = new[] { "explicit", "always", "trigger" }.Select(strings.SkillMatchLabel).ToList();
        var activations = new[] { "auto", "always", "manual" }.Select(strings.SkillActivationLabel).ToList();

        Assert.Equal(3, origins.Distinct().Count());
        Assert.Equal(3, matches.Distinct().Count());
        Assert.Equal(3, activations.Distinct().Count());
        Assert.All(origins.Concat(matches).Concat(activations), t => Assert.True(t.Length > 0 && !t.StartsWith("Skill")));
        Assert.Equal("other", strings.SkillMatchLabel("other"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_not_reviewed_label_and_the_draft_promise_are_said_in_every_language(string language)
    {
        var strings = In(language);

        Assert.True(strings.SkillNotReviewed.Length > 0);
        Assert.True(strings.SkillDraftReady.Length > 20);
        Assert.NotEqual(In("en").SkillDraftReady, language == "en" ? "" : strings.SkillDraftReady);
        Assert.NotEqual(strings.SkillDraftReady, strings.SkillDraftBroken);
    }

    [Fact]
    public void The_texts_are_not_left_in_English_in_the_other_languages()
    {
        var english = In("en");
        foreach (var language in new[] { "de", "fr", "es", "zh-Hans" })
        {
            var strings = In(language);
            Assert.NotEqual(english.SkillLibraryEmpty, strings.SkillLibraryEmpty);
            Assert.NotEqual(english.SkillDeleteBody, strings.SkillDeleteBody);
            Assert.NotEqual(english.SkillDraftNoChat, strings.SkillDraftNoChat);
            Assert.NotEqual(english.SkillModelNote, strings.SkillModelNote);
            Assert.NotEqual(english.SkillIssueText(SkillIssueCode.ScriptFile, "a", 0, 0, ""), strings.SkillIssueText(SkillIssueCode.ScriptFile, "a", 0, 0, ""));
        }
    }
}

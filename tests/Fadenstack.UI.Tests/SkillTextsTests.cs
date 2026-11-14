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

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_texts_of_the_skill_editor_gaps_exist_in_every_language_with_their_places_filled_in(string language)
    {
        var strings = new UiStrings(CultureInfo.GetCultureInfo(language), null, CultureInfo.GetCultureInfo("en-US"));
        var english = In("en");

        var filled = new[]
        {
            strings.SkillBroken("REASON"), strings.SkillEditNamed("NAME"), strings.SkillViewNamed("NAME"), strings.SkillDeleteNamed("NAME"),
            strings.SkillExportNamed("NAME"), strings.SkillDuplicateNamed("NAME"), strings.SkillTokens(1234, 5000),
        };
        Assert.All(filled, t => Assert.DoesNotContain("{", t));
        Assert.Contains("REASON", strings.SkillBroken("REASON"));
        Assert.All(new[] { strings.SkillEditNamed("NAME"), strings.SkillViewNamed("NAME"), strings.SkillDeleteNamed("NAME"), strings.SkillExportNamed("NAME"), strings.SkillDuplicateNamed("NAME") },
            t => Assert.Contains("NAME", t));
        Assert.Equal(5, new[] { strings.SkillEditNamed("N"), strings.SkillViewNamed("N"), strings.SkillDeleteNamed("N"), strings.SkillExportNamed("N"), strings.SkillDuplicateNamed("N") }.Distinct().Count());
        Assert.Contains("1,234", strings.SkillTokens(1234, 5000));
        Assert.Contains("5,000", strings.SkillTokens(1234, 5000));
        Assert.All(new[] { strings.SkillBrokenNoReason, strings.SkillTestMatchesHeading, strings.SkillDraftNothingSaved, strings.SkillDraftOpen },
            t => Assert.True(t.Length > 0 && !t.StartsWith("Skill")));
        if (language != "en")
        {
            Assert.NotEqual(english.SkillBrokenNoReason, strings.SkillBrokenNoReason);
            Assert.NotEqual(english.SkillTestMatchesHeading, strings.SkillTestMatchesHeading);
            Assert.NotEqual(english.SkillDraftNothingSaved, strings.SkillDraftNothingSaved);
            Assert.NotEqual(english.SkillDraftOpen, strings.SkillDraftOpen);
            Assert.NotEqual(english.SkillEditNamed("N"), strings.SkillEditNamed("N"));
            Assert.NotEqual(english.SkillBroken("R"), strings.SkillBroken("R"));
        }
    }}

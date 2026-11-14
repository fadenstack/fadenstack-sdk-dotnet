using Fadenstack.SkillAuthoring;
using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>The checks before saving: one validator for the editor, the import and the draft, one test per rule.</summary>
public class SkillValidatorTests
{
    private static readonly SkillWorkshop Workshop = new WorkshopRig().Workshop;

    private static SkillDocument Good => WorkshopRig.Doc();

    private static SkillCheck Check(SkillDocument document, string? folder = null) => Workshop.Check(document, folder);

    private static IEnumerable<SkillIssueCode> Codes(SkillCheck check) => check.Issues.Select(i => i.Code);

    private static string Invisible(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Fact]
    public void A_good_skill_passes_without_a_finding()
    {
        var check = Check(Good, "contract-review");

        Assert.True(check.Ok);
        Assert.Empty(check.Issues);
    }

    [Theory]
    [InlineData("", SkillIssueCode.NameMissing)]
    [InlineData("Contract-Review", SkillIssueCode.NameForm)]
    [InlineData("contract review", SkillIssueCode.NameForm)]
    [InlineData("contract--review", SkillIssueCode.NameForm)]
    [InlineData("-contract", SkillIssueCode.NameForm)]
    [InlineData("contract-", SkillIssueCode.NameForm)]
    [InlineData("contract_review", SkillIssueCode.NameForm)]
    [InlineData("vertragsprüfung", SkillIssueCode.NameForm)]
    public void The_name_must_be_lower_case_words_with_single_hyphens(string name, SkillIssueCode expected)
    {
        var check = Check(Good with { Name = name });

        Assert.False(check.Ok);
        Assert.Contains(expected, Codes(check));
    }

    [Fact]
    public void The_name_is_at_most_64_characters()
    {
        Assert.True(Check(Good with { Name = new string('a', 64) }).Ok);

        var long65 = Check(Good with { Name = new string('a', 65) });

        Assert.Contains(long65.Issues, i => i.Code == SkillIssueCode.NameTooLong && i.Limit == 64 && i.Actual == 65);
    }

    [Fact]
    public void The_name_is_the_folders_name()
    {
        var check = Check(Good, "other-folder");

        Assert.Contains(check.Issues, i => i.Code == SkillIssueCode.NameNotFolder && i.Detail == "other-folder");
        Assert.True(Check(Good, "contract-review").Ok);
    }

    [Fact]
    public void A_bundled_skills_name_is_taken()
    {
        Assert.Contains(SkillIssueCode.NameReserved, Codes(Check(Good with { Name = "slide-design" })));
    }

    [Fact]
    public void The_description_is_needed_and_at_most_1024_characters()
    {
        Assert.Contains(SkillIssueCode.DescriptionMissing, Codes(Check(Good with { Description = "  " })));
        Assert.True(Check(Good with { Description = new string('d', 1024) }).Ok);
        Assert.Contains(Check(Good with { Description = new string('d', 1025) }).Issues, i => i.Code == SkillIssueCode.DescriptionTooLong && i.Limit == 1024 && i.Actual == 1025);
    }

    [Fact]
    public void The_compatibility_note_is_at_most_500_characters()
    {
        Assert.True(Check(Good with { Compatibility = new string('c', 500) }).Ok);
        Assert.Contains(SkillIssueCode.CompatibilityTooLong, Codes(Check(Good with { Compatibility = new string('c', 501) })));
    }

    [Fact]
    public void A_key_beyond_the_six_standard_fields_is_an_error()
    {
        var check = Check(Good with { UnknownKeys = new[] { "version", "triggers" } });

        Assert.False(check.Ok);
        Assert.Equal(new[] { "version", "triggers" }, check.Issues.Where(i => i.Code == SkillIssueCode.UnknownKey).Select(i => i.Detail));
    }

    public static TheoryData<int> InvisibleCharacters() => new()
    {
        0x200B, 0x200C, 0x200D, 0x2060, 0xFEFF, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069, 0x200E, 0x00AD, 0xE0041,
    };

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void An_invisible_character_is_refused_wherever_it_hides(int codePoint)
    {
        var hidden = Invisible(codePoint);

        foreach (var document in new[]
                 {
                     Good with { Description = "Reviews" + hidden + " contracts." },
                     Good with { Instructions = "Read" + hidden + " the clause." },
                     Good with { License = "MIT" + hidden },
                     Good with { Compatibility = "any" + hidden },
                     Good with { Metadata = new Dictionary<string, string> { ["a"] = "b" + hidden } },
                     Good with { Resources = new[] { SkillFile.FromText("references/a.md", "Fine" + hidden + " text.") } },
                 })
        {
            var check = Check(document);

            Assert.False(check.Ok);
            Assert.Contains(check.Issues, i => i.Code == SkillIssueCode.InvisibleCharacter && i.Detail == "U+" + codePoint.ToString("X4"));
        }
    }

    [Fact]
    public void A_leading_byte_order_mark_in_a_resource_text_is_an_encoding_signature_not_hidden_text()
    {
        var document = Good with { Resources = new[] { SkillFile.FromText("references/a.md", Invisible(0xFEFF) + "Fine.") } };

        Assert.True(Check(document).Ok);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)]
    [InlineData(0x1B)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    public void A_control_character_is_refused(int codePoint)
    {
        var check = Check(Good with { Instructions = "Read" + Invisible(codePoint) + "this." });

        Assert.Contains(SkillIssueCode.ControlCharacter, Codes(check));
    }

    [Fact]
    public void Tab_and_line_breaks_are_fine_in_the_instructions()
    {
        Assert.True(Check(Good with { Instructions = "a\tb\r\nc\nd" }).Ok);
    }

    [Fact]
    public void A_long_body_is_a_warning_not_a_refusal_and_an_empty_one_too()
    {
        var long5001 = Check(Good with { Instructions = new string('x', 5001 * 4) });
        var empty = Check(Good with { Instructions = "" });

        Assert.True(long5001.Ok);
        Assert.Contains(long5001.Issues, i => i.Code == SkillIssueCode.BodyLarge && i.Severity == SkillSeverity.Warning && i.Limit == 5000);
        Assert.True(empty.Ok);
        Assert.Contains(SkillIssueCode.BodyEmpty, Codes(empty));
        Assert.Empty(Check(Good with { Instructions = new string('x', 5000 * 4) }).Issues);
    }

    [Fact]
    public void A_file_over_1_MiB_the_total_over_5_MiB_and_more_than_100_files_are_refused()
    {
        var big = new SkillFile("references/big.bin", new byte[SkillLimits.FileMaxBytes + 1]);
        var many = Enumerable.Range(0, 100).Select(i => SkillFile.FromText("references/f" + i + ".md", "x")).ToList();
        var heavy = Enumerable.Range(0, 6).Select(i => new SkillFile("references/b" + i + ".bin", new byte[SkillLimits.FileMaxBytes])).ToList();

        Assert.Contains(Check(Good with { Resources = new[] { big } }).Issues, i => i.Code == SkillIssueCode.FileTooLarge && i.Subject == "references/big.bin");
        Assert.Contains(SkillIssueCode.TooManyFiles, Codes(Check(Good with { Resources = many }))); // 100 resources + SKILL.md = 101
        Assert.Contains(SkillIssueCode.TotalTooLarge, Codes(Check(Good with { Resources = heavy })));
        Assert.True(Check(Good with { Resources = many.Take(98).ToList() }).Ok);
    }

    [Theory]
    [InlineData("scripts/run.py")]
    [InlineData("scripts/notes.txt")]
    [InlineData("helper.ps1")]
    [InlineData("tools/build.sh")]
    [InlineData("setup.exe")]
    [InlineData("lib/native.dll")]
    [InlineData("check.bat")]
    [InlineData("hook.js")]
    public void A_script_or_a_program_is_not_kept_as_runnable(string path)
    {
        var check = Check(Good with { Resources = new[] { SkillFile.FromText(path, "echo hi") } });

        Assert.False(check.Ok);
        Assert.Contains(check.Issues, i => i.Code == SkillIssueCode.ScriptFile && i.Subject == path);
    }

    [Theory]
    [InlineData("../evil.md")]
    [InlineData("a/../../evil.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/x.md")]
    [InlineData("C:\\windows\\x.md")]
    [InlineData("a\\b.md")]
    [InlineData("a//b.md")]
    [InlineData("./a.md")]
    [InlineData("a/")]
    [InlineData("SKILL.md")]
    [InlineData("faden.yaml")]
    [InlineData("CON:.md")]
    public void An_unsafe_resource_path_is_refused(string path)
    {
        var check = Check(Good with { Resources = new[] { SkillFile.FromText(path, "x") } });

        Assert.False(check.Ok);
        Assert.Contains(SkillIssueCode.UnsafePath, Codes(check));
    }

    [Fact]
    public void Two_files_of_one_name_are_refused_whatever_the_case()
    {
        var check = Check(Good with { Resources = new[] { SkillFile.FromText("references/A.md", "1"), SkillFile.FromText("references/a.md", "2") } });

        Assert.Contains(SkillIssueCode.DuplicatePath, Codes(check));
    }

    [Fact]
    public void The_faden_yaml_fields_must_read_with_the_modules_own_reader()
    {
        Assert.True(Check(Good with { Extension = new SkillExtensionFields { Keywords = new[] { "contract" }, Priority = 100, Activation = "manual" } }).Ok);

        Assert.Contains(SkillIssueCode.FadenYamlUnreadable, Codes(Check(Good with { Extension = new SkillExtensionFields { Priority = 101 } })));
        Assert.Contains(SkillIssueCode.FadenYamlUnreadable, Codes(Check(Good with { Extension = new SkillExtensionFields { Activation = "sometimes" } })));
        Assert.Contains(SkillIssueCode.FadenYamlUnreadable, Codes(Check(Good with { Extension = new SkillExtensionFields { Keywords = new[] { new string('k', 101) } } })));
        Assert.Contains(SkillIssueCode.InvisibleCharacter, Codes(Check(Good with { Extension = new SkillExtensionFields { Keywords = new[] { "a" + Invisible(0x200B) } } })));
    }

    [Fact]
    public void A_faden_yaml_field_the_editor_does_not_keep_is_a_warning()
    {
        var check = Check(Good with { Extension = new SkillExtensionFields { UnknownKeys = new[] { "mystery" }, Keywords = new[] { "a" } } });

        Assert.True(check.Ok);
        Assert.Contains(check.Issues, i => i.Code == SkillIssueCode.FadenYamlUnknownKey && i.Detail == "mystery" && i.Severity == SkillSeverity.Warning);
    }

    [Fact]
    public void The_faden_yaml_fields_survive_writing_and_reading_including_awkward_text()
    {
        var fields = new SkillExtensionFields
        {
            Title = "Contract: review # 1",
            Tags = new[] { "legal", "yes" },
            Priority = 61,
            Activation = "always",
            Keywords = new[] { "clause", "Vertragsprüfung", "say \"hi\"", "it's", "a: b", "true", "123", "c:\\x" },
            FileTypes = new[] { ".docx", "pdf" },
            PreferredTools = new[] { "read_page", "search.web" },
            AllowedTools = new[] { "x" },
            ForbiddenTools = new[] { "y" },
            KnowledgeSources = new[] { "z" },
        };
        var yaml = SkillMarkdown.WriteExtension(fields)!;

        var parsed = Workshop.ParseExtension(yaml);

        Assert.NotNull(parsed.Fields);
        var back = parsed.Fields!;
        Assert.Equal(fields.Title, back.Title);
        Assert.Equal(fields.Tags, back.Tags);
        Assert.Equal(fields.Keywords, back.Keywords);
        Assert.Equal(fields.FileTypes, back.FileTypes);
        Assert.Equal(fields.PreferredTools, back.PreferredTools);
        Assert.Equal((61, "always"), (back.Priority, back.Activation));
        Assert.Equal(new[] { "x" }, back.AllowedTools);
        Assert.Equal(new[] { "y" }, back.ForbiddenTools);
        Assert.Equal(new[] { "z" }, back.KnowledgeSources);
        Assert.Empty(parsed.Issues);
    }

    [Fact]
    public void A_faden_yaml_that_does_not_read_comes_back_as_an_error_and_an_unknown_field_as_a_warning()
    {
        var bad = Workshop.ParseExtension("priority: 500\n");
        var anchors = Workshop.ParseExtension("a: &x 1\nb: *x\n");
        var extra = Workshop.ParseExtension("title: T\nmystery: 1\ntrigger_rules:\n  keywords: [a]\n  intents: [b]\n");

        Assert.Null(bad.Fields);
        Assert.Contains(bad.Issues, i => i.Code == SkillIssueCode.FadenYamlUnreadable && i.IsError);
        Assert.Null(anchors.Fields);
        Assert.NotNull(extra.Fields);
        Assert.Equal(new[] { "mystery", "trigger_rules.intents" }, extra.Fields!.UnknownKeys);
        Assert.All(extra.Issues, i => Assert.Equal(SkillSeverity.Warning, i.Severity));
    }

    [Fact]
    public void SKILL_md_round_trips_and_reads_loose_forms()
    {
        var document = Good with { License = "MIT", Compatibility = "needs: nothing", Metadata = new Dictionary<string, string> { ["a"] = "b: c" }, AllowedTools = "read_page" };

        Assert.True(SkillMarkdown.TryParse(SkillMarkdown.Write(document), out var back, out var error), error);
        Assert.Equal((document.Name, document.Description, "MIT", "needs: nothing", "read_page"), (back.Name, back.Description, back.License, back.Compatibility, back.AllowedTools));
        Assert.Equal("b: c", back.Metadata["a"]);
        Assert.Equal(document.Instructions.Trim(), back.Instructions.Trim());

        Assert.True(SkillMarkdown.TryParse("\r\n---\r\nname: a-b\r\ndescription: >\r\n  Folded text\r\n  over lines.\r\nallowed-tools:\r\n  - read_page\r\n  - find_tool\r\nextra: 1\r\n---\r\nBody\r\n", out var loose, out _));
        Assert.Equal(("a-b", "Folded text over lines.", "read_page find_tool", "Body"), (loose.Name, loose.Description, loose.AllowedTools, loose.Instructions));
        Assert.Equal(new[] { "extra" }, loose.UnknownKeys);

        Assert.False(SkillMarkdown.TryParse("# no frontmatter", out _, out _));
        Assert.False(SkillMarkdown.TryParse("---\nname: a\n", out _, out _));
        Assert.False(SkillMarkdown.TryParse("---\nname: a\nname: b\n---\n", out _, out _));
        Assert.False(SkillMarkdown.TryParse("---\nname: \"unclosed\n---\n", out _, out _));
    }

    [Theory]
    [InlineData("SQL Review", "sql-review")]
    [InlineData("  Code  Review!! ", "code-review")]
    [InlineData("Überprüfung", "berpr-fung")]
    [InlineData("!!!", "new-skill")]
    [InlineData("", "new-skill")]
    public void A_name_is_made_from_any_text_as_core_does(string text, string expected)
    {
        Assert.Equal(expected, SkillMarkdown.Slugify(text));
    }

    [Fact]
    public void A_slug_is_cut_at_64_characters_without_a_trailing_hyphen()
    {
        var slug = SkillMarkdown.Slugify(new string('a', 63) + " b");

        Assert.True(slug.Length <= 64);
        Assert.False(slug.EndsWith("-", StringComparison.Ordinal));
    }
}

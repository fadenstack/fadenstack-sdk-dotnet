using Fadenstack.Agents;
using Xunit;
using static Fadenstack.Skills.Tests.SkillsThroughOurLoopTests;

namespace Fadenstack.Skills.Tests;

/// <summary>The six-field rule on read (sdk/51 F04, H6): a skill that breaks it is skipped and logged, and its neighbours stay.</summary>
public class SkillRuleTests
{
    private static async Task<(string Instructions, Entries Logs)> Offered(SkillFolders folders)
    {
        var logs = new Entries();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, From(folders), logs: logs).Build();
        await (await agent.StartSessionAsync()).SendAsync("Hi");
        return (model.Calls[0].Instructions ?? "", logs);
    }

    [Fact]
    public async Task A_skill_that_follows_the_rule_is_offered_with_all_six_fields()
    {
        using var folders = new SkillFolders();
        folders.Standard(); // name, description, license, compatibility, metadata and allowed-tools

        var (instructions, logs) = await Offered(folders);

        Assert.Contains("house-style", instructions);
        Assert.DoesNotContain(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && !l.Text.StartsWith("Agent ")); // the agent's own provenance note is not about skills
    }

    [Fact]
    public async Task A_key_beyond_the_six_skips_the_skill_and_logs_it()
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        folders.Standard("keyword-skill", extraFrontmatter: "keywords: [mail, reply]\n");

        var (instructions, logs) = await Offered(folders);

        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain("keyword-skill", instructions);
        Assert.Contains(logs.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && l.Text.Contains("keyword-skill") && l.Text.Contains("keywords"));
    }

    [Fact]
    public async Task A_name_that_is_not_the_folders_name_skips_the_skill_and_logs_it()
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        folders.Standard("renamed-skill", folder: "some-other-folder");

        var (instructions, logs) = await Offered(folders);

        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain("renamed-skill", instructions);
        Assert.Contains(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && l.Text.Contains("renamed-skill"));
    }

    [Theory]
    [InlineData("Upper-Case")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("under_score")]
    public async Task A_name_that_is_not_lowercase_letters_digits_and_single_hyphens_is_skipped(string name)
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        folders.Standard(name);

        var (instructions, logs) = await Offered(folders);

        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain(name, instructions);
        Assert.Contains(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_name_over_64_characters_is_skipped()
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        var name = new string('a', 65);
        folders.Standard(name);

        var (instructions, logs) = await Offered(folders);

        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain(name, instructions);
        Assert.Contains(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_name_of_exactly_64_characters_is_taken()
    {
        using var folders = new SkillFolders();
        var name = new string('a', 64);
        folders.Standard(name);

        var (instructions, _) = await Offered(folders);

        Assert.Contains(name, instructions);
    }

    [Fact]
    public async Task A_description_over_1024_characters_is_skipped_and_1024_is_taken()
    {
        using var folders = new SkillFolders();
        folders.Standard("long-one", description: new string('x', 1025));
        folders.Standard("just-fits", description: new string('y', 1024));

        var (instructions, logs) = await Offered(folders);

        Assert.DoesNotContain("long-one", instructions);
        Assert.Contains("just-fits", instructions);
        Assert.Contains(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_compatibility_over_500_characters_is_skipped()
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        var dir = folders.Standard("wordy-skill");
        var file = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("compatibility: any", "compatibility: " + new string('c', 501)));

        var (instructions, logs) = await Offered(folders);
        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain("wordy-skill", instructions);
        Assert.Contains(logs.Lines, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_nested_key_under_metadata_is_not_an_extra_field()
    {
        using var folders = new SkillFolders();
        folders.Standard("nested-ok", extraFrontmatter: "# a comment\n");

        var (instructions, _) = await Offered(folders);

        Assert.Contains("nested-ok", instructions);
    }

    [Fact]
    public async Task A_skill_without_a_skill_file_in_its_folder_is_not_a_skill()
    {
        using var folders = new SkillFolders();
        folders.Standard("good-skill");
        Directory.CreateDirectory(Path.Combine(folders.Skills, "empty-folder"));

        var (instructions, _) = await Offered(folders);

        Assert.Contains("good-skill", instructions);
        Assert.DoesNotContain("empty-folder", instructions);
    }
}

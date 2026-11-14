using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.SkillAuthoring;
using Xunit;
using static Fadenstack.Skills.Tests.SkillsThroughOurLoopTests;

namespace Fadenstack.Skills.Tests;

/// <summary>A temporary skills folder with a workshop on it, the switches in memory and a bundled skill.</summary>
internal sealed class WorkshopRig : IDisposable
{
    public WorkshopRig(Func<CancellationToken, Task<IReadOnlyList<SkillOption>>>? organisation = null, Action? changed = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "faden-workshop-" + Guid.NewGuid().ToString("N"));
        Folder = Path.Combine(Root, "skills");
        Options = new SkillsOptions { Switches = new MemorySkillSwitches() };
        Options.Folders.Add(Folder);
        Options.Bundled.Add(new BundledSkill("slide-design", "Designs a slide.", "Use one idea per slide.")
        {
            Resources = new Dictionary<string, string> { ["layouts"] = "Title, then two columns." },
            FadenYaml = "trigger_rules:\n  keywords:\n    - slide\n    - deck\n",
        });
        Workshop = new SkillWorkshop(Options, Folder, organisation, changed);
    }

    public string Root { get; }

    public string Folder { get; }

    public SkillsOptions Options { get; }

    public SkillWorkshop Workshop { get; }

    public string Staging => Folder + ".staging";

    public static SkillDocument Doc(string name = "contract-review", string description = "Reviews a contract clause by clause.") => new()
    {
        Name = name,
        Description = description,
        Instructions = "## Goal\nReview the contract.\n\n## Procedure\n1. Read each clause.\n2. Flag the risky ones.\n",
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The skill store: atomic save, rename, delete, the sources and the on/off switch (sdk/51 F04).</summary>
public class SkillWorkshopTests
{
    [Fact]
    public async Task A_saved_skill_is_read_back_whole_and_no_staging_folder_is_left()
    {
        using var rig = new WorkshopRig();
        var document = WorkshopRig.Doc() with
        {
            License = "Apache-2.0",
            Compatibility = "any agent",
            Metadata = new Dictionary<string, string> { ["author"] = "tests" },
            Extension = new SkillExtensionFields { Keywords = new[] { "contract", "clause" }, PreferredTools = new[] { "read_page" }, Priority = 70, Activation = "always" },
            Resources = new[] { SkillFile.FromText("references/tone.md", "Warm."), new SkillFile("assets/logo.bin", new byte[] { 0, 1, 2, 255 }) },
        };

        var saved = await rig.Workshop.SaveAsync(document);
        var read = await rig.Workshop.ReadAsync("contract-review");

        Assert.Equal(SkillSaveStatus.Saved, saved.Status);
        Assert.Equal(SkillOrigin.Own, read.Origin);
        var back = read.Document!;
        Assert.Equal(("contract-review", "Reviews a contract clause by clause.", "Apache-2.0", "any agent"), (back.Name, back.Description, back.License, back.Compatibility));
        Assert.Equal("tests", back.Metadata["author"]);
        Assert.Equal(document.Instructions.Trim(), back.Instructions.Trim());
        Assert.Equal(new[] { "contract", "clause" }, back.Extension.Keywords);
        Assert.Equal(("always", 70), (back.Extension.Activation, back.Extension.Priority));
        Assert.Equal(new[] { "assets/logo.bin", "references/tone.md" }, back.Resources.Select(r => r.Path).OrderBy(p => p));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, back.Resources.Single(r => r.Path == "assets/logo.bin").Bytes);
        Assert.DoesNotContain(read.Issues, i => i.IsError);
        Assert.False(Directory.Exists(rig.Staging));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "contract-review", "SKILL.md")));
    }

    [Fact]
    public async Task A_saved_SKILL_md_keeps_only_the_six_standard_fields_and_the_extras_go_to_faden_yaml()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc() with { Extension = new SkillExtensionFields { Keywords = new[] { "clause" }, Title = "Contract review" } });

        var skillMd = File.ReadAllText(Path.Combine(rig.Folder, "contract-review", "SKILL.md"));
        var yaml = File.ReadAllText(Path.Combine(rig.Folder, "contract-review", "faden.yaml"));

        Assert.StartsWith("---\nname: contract-review\ndescription: ", skillMd, StringComparison.Ordinal);
        Assert.DoesNotContain("keywords", skillMd);
        Assert.DoesNotContain("title", skillMd);
        Assert.Contains("keywords:", yaml);
        Assert.Contains("title: Contract review", yaml);
    }

    [Fact]
    public async Task A_refused_save_writes_nothing()
    {
        using var rig = new WorkshopRig();

        var result = await rig.Workshop.SaveAsync(WorkshopRig.Doc() with { Description = "" });

        Assert.Equal(SkillSaveStatus.Refused, result.Status);
        Assert.Contains(result.Issues, i => i.Code == SkillIssueCode.DescriptionMissing);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.False(Directory.Exists(rig.Staging));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("C:evil")]
    [InlineData(".hidden")]
    public async Task A_name_that_is_a_path_never_reaches_the_file_system(string name)
    {
        using var rig = new WorkshopRig();

        var result = await rig.Workshop.SaveAsync(WorkshopRig.Doc(name));
        var deleted = await rig.Workshop.DeleteAsync(name);
        var read = await rig.Workshop.ReadAsync(name);

        Assert.Equal(SkillSaveStatus.Refused, result.Status);
        Assert.False(deleted);
        Assert.Null(read.Document);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "evil")));
    }

    [Fact]
    public async Task A_second_skill_of_the_same_name_asks_and_replaces_only_when_confirmed_leaving_nothing_of_the_old_one()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc() with { Resources = new[] { SkillFile.FromText("old.md", "Old.") } });

        var asked = await rig.Workshop.SaveAsync(WorkshopRig.Doc(description: "New one."));
        Assert.Equal(SkillSaveStatus.Exists, asked.Status);
        Assert.Equal("Reviews a contract clause by clause.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "contract-review", "old.md")));

        var confirmed = await rig.Workshop.SaveAsync(WorkshopRig.Doc(description: "New one."), replaceExisting: true);

        Assert.Equal(SkillSaveStatus.Saved, confirmed.Status);
        Assert.Equal("New one.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);
        Assert.False(File.Exists(Path.Combine(rig.Folder, "contract-review", "old.md")));
        Assert.False(Directory.Exists(rig.Staging));
    }

    [Fact]
    public async Task Saving_an_edit_of_the_same_skill_needs_no_confirmation()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());

        var result = await rig.Workshop.SaveAsync(WorkshopRig.Doc(description: "Edited."), previousName: "contract-review");

        Assert.Equal(SkillSaveStatus.Saved, result.Status);
    }

    [Fact]
    public async Task A_rename_moves_the_skill_and_its_switch_and_leaves_no_old_folder()
    {
        var changes = 0;
        using var rig = new WorkshopRig(changed: () => changes++);
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());
        await rig.Workshop.SetEnabledAsync("contract-review", false);

        var result = await rig.Workshop.SaveAsync(WorkshopRig.Doc("nda-review"), previousName: "contract-review");

        Assert.Equal(SkillSaveStatus.Saved, result.Status);
        Assert.False(Directory.Exists(Path.Combine(rig.Folder, "contract-review")));
        Assert.True(Directory.Exists(Path.Combine(rig.Folder, "nda-review")));
        var entries = await rig.Workshop.ListAsync();
        Assert.Equal(new[] { "nda-review" }, entries.Where(e => e.Origin == SkillOrigin.Own).Select(e => e.Name));
        Assert.False(entries.Single(e => e.Name == "nda-review").Enabled);
        Assert.True(rig.Options.Switches!.IsEnabled("contract-review"));
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task A_rename_onto_another_skill_asks_first_and_changes_nothing()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());
        await rig.Workshop.SaveAsync(WorkshopRig.Doc("nda-review", "Reviews NDAs."));

        var result = await rig.Workshop.SaveAsync(WorkshopRig.Doc("nda-review"), previousName: "contract-review");

        Assert.Equal(SkillSaveStatus.Exists, result.Status);
        Assert.True(Directory.Exists(Path.Combine(rig.Folder, "contract-review")));
        Assert.Equal("Reviews NDAs.", (await rig.Workshop.ReadAsync("nda-review")).Document!.Description);
    }

    [Fact]
    public async Task Delete_removes_an_own_skill_only()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());

        Assert.False(await rig.Workshop.DeleteAsync("slide-design")); // bundled
        Assert.False(await rig.Workshop.DeleteAsync("not-there"));
        Assert.True(await rig.Workshop.DeleteAsync("contract-review"));

        Assert.False(Directory.Exists(Path.Combine(rig.Folder, "contract-review")));
        Assert.DoesNotContain(await rig.Workshop.ListAsync(), e => e.Name == "contract-review");
    }

    [Fact]
    public async Task The_library_lists_bundled_own_and_organisation_skills_with_their_sources()
    {
        using var rig = new WorkshopRig(_ => Task.FromResult<IReadOnlyList<SkillOption>>(new[]
        {
            new SkillOption { Id = "1", Slug = "org-policy", Name = "Org policy", Description = "From the server." },
        }));
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());

        var entries = await rig.Workshop.ListAsync();

        Assert.Equal(new[] { SkillOrigin.Bundled, SkillOrigin.Own, SkillOrigin.Organisation }, entries.Select(e => e.Origin));
        var bundled = entries[0];
        var own = entries[1];
        var org = entries[2];
        Assert.True(bundled.IsReadOnly && bundled.CanSwitch && !bundled.NotReviewed);
        Assert.True(!own.IsReadOnly && own.CanSwitch && own.NotReviewed);
        Assert.True(org.IsReadOnly && !org.CanSwitch && !org.NotReviewed);
        Assert.Equal("org-policy", org.Name);
        Assert.All(entries, e => Assert.True(e.Enabled));
    }

    [Fact]
    public async Task A_failing_organisation_list_leaves_the_rest_of_the_library()
    {
        using var rig = new WorkshopRig(_ => throw new InvalidOperationException("offline"));
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());

        var entries = await rig.Workshop.ListAsync();

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task A_bundled_skill_reads_but_is_never_written_and_its_name_is_not_taken()
    {
        using var rig = new WorkshopRig();

        var read = await rig.Workshop.ReadAsync("slide-design");
        var save = await rig.Workshop.SaveAsync(WorkshopRig.Doc("slide-design"));
        var edit = await rig.Workshop.SaveAsync(WorkshopRig.Doc("slide-design-2"), previousName: "slide-design");

        Assert.Equal(SkillOrigin.Bundled, read.Origin);
        Assert.Equal("Use one idea per slide.", read.Document!.Instructions);
        Assert.Contains("slide", read.Document.Extension.Keywords);
        Assert.Equal("Title, then two columns.", read.Document.Resources.Single().Text);
        Assert.Equal(SkillSaveStatus.Refused, save.Status);
        Assert.Contains(save.Issues, i => i.Code == SkillIssueCode.NameReserved);
        Assert.Equal(SkillSaveStatus.NotAllowed, edit.Status);
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Fact]
    public async Task A_broken_folder_is_listed_as_broken_and_can_still_be_deleted()
    {
        using var rig = new WorkshopRig();
        Directory.CreateDirectory(Path.Combine(rig.Folder, "odd-one"));
        File.WriteAllText(Path.Combine(rig.Folder, "odd-one", "SKILL.md"), "no frontmatter here");
        Directory.CreateDirectory(Path.Combine(rig.Folder, "extra-key"));
        File.WriteAllText(Path.Combine(rig.Folder, "extra-key", "SKILL.md"), "---\nname: extra-key\ndescription: x\nversion: 2\n---\nBody\n");

        var entries = (await rig.Workshop.ListAsync()).Where(e => e.Origin == SkillOrigin.Own).ToList();
        var read = await rig.Workshop.ReadAsync("extra-key");

        Assert.All(entries, e => Assert.True(e.IsBroken));
        Assert.Contains(read.Issues, i => i.Code == SkillIssueCode.UnknownKey && i.Detail == "version");
        Assert.True(await rig.Workshop.DeleteAsync("odd-one"));
    }

    [Fact]
    public async Task The_switch_is_kept_per_skill_and_the_module_does_not_list_an_off_skill_for_the_model()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc());
        await rig.Workshop.SaveAsync(WorkshopRig.Doc("nda-review", "Reviews NDAs."));
        var module = new SkillsModule(rig.Options);
        var model = new ScriptedModel().Says("One.").Says("Two.").Says("Three.");
        using var agent = Standalone(model, null).Use(module).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("First slide.");
        await rig.Workshop.SetEnabledAsync("contract-review", false);
        await rig.Workshop.SetEnabledAsync("slide-design", false);
        module.Refresh();
        await session.SendAsync("Second slide.");
        await rig.Workshop.SetEnabledAsync("contract-review", true);
        module.Refresh();
        await session.SendAsync("Third slide.");

        Assert.Contains("contract-review", model.Calls[0].Instructions);
        Assert.Contains("slide-design", model.Calls[0].Instructions);
        Assert.DoesNotContain("contract-review", model.Calls[1].Instructions);
        Assert.DoesNotContain("slide-design", model.Calls[1].Instructions);
        Assert.Contains("nda-review", model.Calls[1].Instructions);
        Assert.Contains("contract-review", model.Calls[2].Instructions);
    }

    [Fact]
    public async Task The_workshop_refreshes_the_module_after_a_switch_a_save_and_a_delete()
    {
        var module = new SkillsModule(new SkillsOptions());
        var refreshed = 0;
        module.Changed += (_, _) => refreshed++;
        using var rig = new WorkshopRig(changed: module.Refresh);

        await rig.Workshop.SaveAsync(WorkshopRig.Doc());
        await rig.Workshop.SetEnabledAsync("contract-review", false);
        await rig.Workshop.SetEnabledAsync("contract-review", false); // no change
        await rig.Workshop.DeleteAsync("contract-review");

        Assert.Equal(3, refreshed);
    }

    [Fact]
    public void File_switches_are_kept_in_the_settings_and_survive_a_new_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), "faden-switches-" + Guid.NewGuid().ToString("N"), "skills.json");
        try
        {
            var first = new FileSkillSwitches(path);
            Assert.True(first.IsEnabled("a-skill"));
            Assert.True(first.Set("a-skill", false));
            Assert.False(first.Set("a-skill", false));

            var second = new FileSkillSwitches(path);
            Assert.False(second.IsEnabled("a-skill"));
            Assert.True(second.IsEnabled("another"));
            Assert.True(second.Set("a-skill", true));
            Assert.True(new FileSkillSwitches(path).IsEnabled("a-skill"));
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_switch_file_that_cannot_be_read_means_every_skill_is_on()
    {
        var folder = Path.Combine(Path.GetTempPath(), "faden-switches-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "skills.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.True(new FileSkillSwitches(path).IsEnabled("a-skill"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

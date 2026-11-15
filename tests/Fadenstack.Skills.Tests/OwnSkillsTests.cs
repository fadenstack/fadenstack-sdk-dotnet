using System.Runtime.CompilerServices;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.SkillAuthoring;
using Fadenstack.Storage;
using Xunit;
using static Fadenstack.Skills.Tests.SkillsThroughOurLoopTests;

namespace Fadenstack.Skills.Tests;

/// <summary>The skills tests never touch the real data folder: this process's folders are a temporary one, set before any test runs.</summary>
internal static class TestFolders
{
#pragma warning disable CA2255 // a test assembly: the one place the process's folders are chosen
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Redirect() =>
        FadenstackPaths.RootForTests = Path.Combine(Path.GetTempPath(), "faden-skills-tests-" + Guid.NewGuid().ToString("N"));
}

/// <summary>
/// The person's own skills (owner, 15 Nov 2026): in the folder of whoever the agent's profile names (<c>users/&lt;user&gt;/skills</c>,
/// standalone <c>users/standalone/skills</c>), with their switches beside them, so two people on one OS account never see each other's
/// and deleting a person's data deletes theirs. Each test signs in a person of its own: the tests of a class run beside other classes.
/// </summary>
public class OwnSkillsTests
{
    private const string AllowSkills = """{"mode":"disable","developer_mode":false},"local_skills":{"mode":"allow","developer_mode":false}""";

    private static string Person() => "person-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private static string Principal(string user) =>
        $$"""{"tenant_id":"tenant-test","user_id":"{{user}}","agent_id":"agent-test","device_id":"device-test"}""";

    private static Gateway Server(string? user)
    {
        var gateway = new Gateway(localMcp: AllowSkills);
        gateway.Principal = user is null ? null : Principal(user);
        return gateway;
    }

    /// <summary>A skill in the person's own folder, as the editor writes it.</summary>
    private static void Write(string user, string name, string description)
    {
        var folder = Path.Combine(SkillsOptions.FolderOf(user), name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\nUse short sentences.\n");
    }

    private static async Task<string> FirstRequestAsync(Gateway gateway, SkillsModule module)
    {
        gateway.Says("Hello.");
        using var agent = gateway.Builder().Use(module).Build();
        await (await agent.StartSessionAsync()).SendAsync("Hi");
        return gateway.ChatRequests[0].Json.GetRawText();
    }

    [Fact]
    public async Task Two_people_on_one_OS_account_never_see_each_others_skills()
    {
        var ana = Person();
        var ben = Person();
        Write(ana, "ana-notes", "Keeps Ana's meeting notes.");

        var anas = await FirstRequestAsync(Server(ana), new SkillsModule(new SkillsOptions()));
        var bens = await FirstRequestAsync(Server(ben), new SkillsModule(new SkillsOptions()));

        Assert.Contains("ana-notes", anas);
        Assert.DoesNotContain("ana-notes", bens);
    }

    [Fact]
    public async Task The_module_reads_the_folder_and_switches_of_the_person_the_profile_names()
    {
        var user = Person();
        var module = new SkillsModule(new SkillsOptions());
        Assert.Null(module.OwnFolder); // not known before the profile
        Assert.Null(module.Switches);

        await FirstRequestAsync(Server(user), module);

        Assert.Equal(Path.Combine(FadenstackPaths.DataRoot, "users", user, "skills"), module.OwnFolder);
        var switches = Assert.IsType<FileSkillSwitches>(module.Switches);
        Assert.Equal(Path.Combine(FadenstackPaths.DataRoot, "users", user, "preferences", "skills.json"), switches.Path);
    }

    [Fact]
    public async Task Standalone_the_person_is_the_standalone_principal()
    {
        var module = new SkillsModule(new SkillsOptions());
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, null).Use(module).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Equal(Path.Combine(FadenstackPaths.DataRoot, "users", "standalone", "skills"), module.OwnFolder);
    }

    [Fact]
    public async Task A_switch_in_the_persons_own_file_keeps_their_skill_from_the_model()
    {
        var user = Person();
        Write(user, "quiet-skill", "A skill the person switched off.");
        FileSkillSwitches.ForUser(user).Set("quiet-skill", false);

        var request = await FirstRequestAsync(Server(user), new SkillsModule(new SkillsOptions()));

        Assert.DoesNotContain("quiet-skill", request);
    }

    [Fact]
    public async Task Without_own_skills_or_without_a_named_person_no_persons_folder_is_read()
    {
        var user = Person();
        Write(user, "unread-skill", "Never read here.");
        var off = new SkillsModule(new SkillsOptions { OwnSkills = false });
        var nobody = new SkillsModule(new SkillsOptions());

        var withoutOwn = await FirstRequestAsync(Server(user), off);
        await FirstRequestAsync(Server(null), nobody); // an older server that names no user

        Assert.DoesNotContain("unread-skill", withoutOwn);
        Assert.Null(off.OwnFolder);
        Assert.Null(nobody.OwnFolder);
        Assert.Null(nobody.Switches);
    }

    [Fact]
    public async Task The_editor_follows_the_modules_person_and_waits_until_it_is_known()
    {
        var user = Person();
        var options = new SkillsOptions();
        options.Bundled.Add(new BundledSkill("slide-design", "Designs a slide.", "One idea per slide."));
        var module = new SkillsModule(options);
        var workshop = new SkillWorkshop(module);

        Assert.Null(workshop.Folder);
        var before = await workshop.ListAsync();
        Assert.Equal(new[] { "slide-design" }, before.Select(e => e.Name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workshop.SaveAsync(WorkshopRig.Doc()));

        await FirstRequestAsync(Server(user), module);

        Assert.Equal(module.OwnFolder, workshop.Folder);
        Assert.Equal(SkillSaveStatus.Saved, (await workshop.SaveAsync(WorkshopRig.Doc())).Status);
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.DataRoot, "users", user, "skills", "contract-review", "SKILL.md")));
        await workshop.SetEnabledAsync("contract-review", false);
        Assert.False(FileSkillSwitches.ForUser(user).IsEnabled("contract-review"));
        Assert.Contains(await workshop.ListAsync(), e => e.Name == "contract-review" && !e.Enabled);
    }

    [Fact]
    public async Task Deleting_a_persons_data_deletes_their_skills_and_switches()
    {
        var user = Person();
        Write(user, "gone-skill", "Deleted with the person's data.");
        FileSkillSwitches.ForUser(user).Set("gone-skill", false);
        var gateway = Server(user).Says("Hello.");
        using var agent = gateway.Builder().Use(new SkillsModule(new SkillsOptions())).Build();

        await agent.DeleteMyDataAsync();

        var folder = Path.Combine(FadenstackPaths.DataRoot, "users", user);
        Assert.False(Directory.Exists(Path.Combine(folder, "skills")));
        Assert.False(File.Exists(Path.Combine(folder, "preferences", "skills.json")));
    }
}

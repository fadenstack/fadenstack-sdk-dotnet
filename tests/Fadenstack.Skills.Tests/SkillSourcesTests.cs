using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Xunit;
using static Fadenstack.Skills.Tests.SkillsThroughOurLoopTests;

namespace Fadenstack.Skills.Tests;

/// <summary>The sources: the person's folders, extra folders and bundled skills, listed together.</summary>
public class SkillSourcesTests
{
    [Fact]
    public async Task A_bundled_skill_loads_and_reads_its_resource_without_any_folder()
    {
        var options = new SkillsOptions();
        options.Bundled.Add(new BundledSkill("slide-design", "Designs a slide.", "Use one idea per slide.")
        {
            Resources = new Dictionary<string, string> { ["layouts"] = "Title, then two columns." },
        });
        var asked = new List<string>();
        var model = new ScriptedModel()
            .Calls_("load_skill", """{"skillName":"slide-design"}""", "c1")
            .Calls_("read_skill_resource", """{"skillName":"slide-design","resourceName":"layouts"}""", "c2")
            .Says("Done.");
        using var agent = Standalone(model, options, asked).Build();

        await (await agent.StartSessionAsync()).SendAsync("Make a slide.");

        Assert.Contains("slide-design", model.Calls[0].Instructions);
        Assert.Contains("Use one idea per slide.", string.Join(" ", model.Calls[1].ToolResults));
        Assert.Contains("Title, then two columns.", string.Join(" ", model.Calls[2].ToolResults));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Folders_and_bundled_skills_are_listed_together_and_a_bundled_name_is_not_taken_by_a_folder()
    {
        using var folders = new SkillFolders();
        folders.Standard("house-style", description: "From the folder.");
        folders.Standard("folder-only");
        var extra = Path.Combine(folders.Root, "extra");
        folders.Standard("extra-skill", parent: extra);
        var options = From(folders);
        options.Folders.Add(extra);
        options.Folders.Add(Path.Combine(folders.Root, "does-not-exist"));
        options.Bundled.Add(new BundledSkill("house-style", "From the app.", "Bundled body."));
        var model = new ScriptedModel().Calls_("load_skill", """{"skillName":"house-style"}""").Says("Done.");
        using var agent = Standalone(model, options).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        var instructions = model.Calls[0].Instructions!;
        Assert.Contains("folder-only", instructions);
        Assert.Contains("extra-skill", instructions);
        Assert.Contains("From the app.", instructions);
        Assert.DoesNotContain("From the folder.", instructions);
        Assert.Contains("Bundled body.", string.Join(" ", model.Calls[1].ToolResults));
    }

    [Fact]
    public async Task Only_the_resources_the_skill_lists_can_be_read_and_a_script_is_not_one()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Calls_("read_skill_resource", """{"skillName":"house-style","resourceName":"scripts/check.py"}""").Says("Done.");
        using var agent = Standalone(model, From(folders)).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.DoesNotContain("open(", string.Join(" ", model.Calls[1].ToolResults));
        Assert.False(File.Exists(folders.Mark));
    }

    [Fact]
    public async Task A_skill_added_later_is_offered_from_the_next_turn_after_Refresh()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var module = new SkillsModule(From(folders));
        var model = new ScriptedModel().Says("One.").Says("Two.");
        using var agent = FadenstackAgent.CreateBuilder().UseChatClient(model)
            .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant","name":"Docs","version":{"instructions":"Docs."}}"""))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).Use(module).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("First.");
        folders.Standard("added-later");
        module.Refresh();
        await session.SendAsync("Second.");

        Assert.DoesNotContain("added-later", model.Calls[0].Instructions);
        Assert.Contains("added-later", model.Calls[1].Instructions);
    }

    [Fact]
    public void The_module_is_named_skills_and_its_two_tools_are_the_sdks_reserved_names()
    {
        Assert.Equal("skills", new SkillsModule().Name);
        Assert.All(SkillsModule.ToolNames, name => Assert.Contains(name, AgentTool.ReservedNames));
        Assert.DoesNotContain("run_skill_script", SkillsModule.ToolNames);
    }

    [Fact]
    public async Task Without_the_module_there_are_no_skills()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, null).Build(); // the module is added only when the app adds it

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.DoesNotContain("load_skill", model.Calls[0].Tools);
        Assert.DoesNotContain("house-style", model.Calls[0].Instructions);
    }
}

using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>
/// sdk/51 F04, test first (H1, H9): Microsoft Agent Framework's <c>AgentSkillsProvider</c> works through our own loop, not
/// only inside MAF's <c>ChatClientAgent</c>. A standalone agent, a scripted model, a folder with one skill.
/// </summary>
public class SkillsThroughOurLoopTests
{
    private const string Definition = """{"slug":"docs-assistant","name":"Docs assistant","version":{"instructions":"You are the docs assistant."}}""";

    internal static AgentBuilder Standalone(ScriptedModel model, SkillsOptions? skills, List<string>? asked = null, Entries? logs = null,
        MachinePolicy? policy = null, LocalProfileOptions? local = null)
    {
        var builder = FadenstackAgent.CreateBuilder()
            .UseChatClient(model)
            .UseLocalProfile(AgentDefinition.FromJson(Definition), options: local)
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals((request, _) =>
            {
                asked?.Add(request.ToolName);
                return Task.FromResult(ApprovalDecision.Approve());
            });
        if (logs is not null)
        {
            builder.UseLogging(logs);
        }
        if (policy is not null)
        {
            builder.UseMachinePolicy(policy);
        }
        if (skills is not null)
        {
            builder.Use(new SkillsModule(skills));
        }
        return builder;
    }

    internal static SkillsOptions From(SkillFolders folders)
    {
        var options = new SkillsOptions();
        options.Folders.Add(folders.Skills);
        return options;
    }

    [Fact]
    public async Task The_model_is_offered_load_skill_and_read_skill_resource_but_never_run_skill_script()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, From(folders)).Build();

        var result = await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        var offered = model.Calls[0].Tools;
        Assert.Contains("load_skill", offered);
        Assert.Contains("read_skill_resource", offered);
        Assert.DoesNotContain("run_skill_script", offered);
        Assert.Equal(2, offered.Count);
        Assert.DoesNotContain(agent.Tools, t => t.Name == "run_skill_script");
    }

    [Fact]
    public async Task The_skills_names_and_descriptions_reach_the_model_beside_the_agents_own_instructions()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, From(folders)).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        var instructions = model.Calls[0].Instructions!;
        Assert.Contains("house-style", instructions);
        Assert.Contains("Writes customer replies in the house style.", instructions);
        Assert.Contains("You are the docs assistant.", instructions);
        Assert.DoesNotContain(SkillFolders.Body, instructions); // progressive disclosure: the body comes with load_skill
        Assert.Contains("load_skill", instructions);
        Assert.DoesNotContain("run_skill_script", instructions); // MAF's own prompt would tell the model to run scripts
    }

    [Fact]
    public async Task A_load_skill_call_returns_the_body_and_read_skill_resource_returns_the_resource_and_neither_asks()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var asked = new List<string>();
        var model = new ScriptedModel()
            .Calls_("load_skill", """{"skillName":"house-style"}""", "c1")
            .Calls_("read_skill_resource", """{"skillName":"house-style","resourceName":"references/tone.md"}""", "c2")
            .Says("Done.");
        using var agent = Standalone(model, From(folders), asked).Build();

        var result = await (await agent.StartSessionAsync()).SendAsync("Write the reply.");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        Assert.Equal(3, model.Calls.Count);
        Assert.Contains(SkillFolders.Body, string.Join("\n", model.Calls[1].ToolResults));
        Assert.Contains(SkillFolders.Resource, string.Join("\n", model.Calls[2].ToolResults));
        Assert.Empty(asked); // both are class read: our classes decide, and MAF's own approval is off
        Assert.All(agent.Tools.Where(t => t.Source == "skills"), t => Assert.Equal(ToolClass.Read, t.Class));
        Assert.False(File.Exists(folders.Mark));
    }

    [Fact]
    public async Task The_two_tools_run_in_read_only_mode_too()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var asked = new List<string>();
        var model = new ScriptedModel().Calls_("load_skill", """{"skillName":"house-style"}""").Says("Done.");
        using var agent = Standalone(model, From(folders), asked).UseDefaultToolMode(ToolMode.ReadOnly).Build();

        await (await agent.StartSessionAsync()).SendAsync("Write the reply.");

        Assert.Contains(SkillFolders.Body, string.Join("\n", model.Calls[1].ToolResults));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_script_is_never_run_even_when_the_model_asks_for_it_by_name()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel()
            .Calls_("run_skill_script", """{"skillName":"house-style","scriptName":"scripts/check.py"}""")
            .Says("Done.");
        using var agent = Standalone(model, From(folders)).Build();

        await (await agent.StartSessionAsync()).SendAsync("Run the check.");

        Assert.False(File.Exists(folders.Mark));
        Assert.DoesNotContain("ran", string.Join("\n", model.Calls[1].ToolResults).Replace("ran-", ""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../secret.md")]
    [InlineData("..\\..\\secret.md")]
    [InlineData("references/../../../secret.md")]
    public async Task A_resource_path_that_leaves_the_skill_folder_is_refused(string path)
    {
        using var folders = new SkillFolders();
        folders.Standard();
        File.WriteAllText(Path.Combine(folders.Root, "secret.md"), "TOP SECRET");
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { skillName = "house-style", resourceName = path });
        var model = new ScriptedModel().Calls_("read_skill_resource", arguments).Says("Done.");
        using var agent = Standalone(model, From(folders)).Build();

        await (await agent.StartSessionAsync()).SendAsync("Read it.");

        Assert.DoesNotContain("TOP SECRET", string.Join("\n", model.Calls[1].ToolResults));
        Assert.Contains("not found in skill", string.Join(" ", model.Calls[1].ToolResults)); // MAF reads only the files it listed
    }

    [Fact]
    public async Task An_absolute_resource_path_is_refused()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var secret = Path.Combine(folders.Root, "secret.md");
        File.WriteAllText(secret, "TOP SECRET");
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { skillName = "house-style", resourceName = secret });
        var model = new ScriptedModel().Calls_("read_skill_resource", arguments).Says("Done.");
        using var agent = Standalone(model, From(folders)).Build();

        await (await agent.StartSessionAsync()).SendAsync("Read it.");

        Assert.DoesNotContain("TOP SECRET", string.Join("\n", model.Calls[1].ToolResults));
        Assert.Contains("not found in skill", string.Join(" ", model.Calls[1].ToolResults)); // MAF reads only the files it listed
    }
}

using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Models;
using Xunit;
using static Fadenstack.Skills.Tests.SkillsThroughOurLoopTests;

namespace Fadenstack.Skills.Tests;

/// <summary>
/// The governed-mode rule (sdk/51 F04, R3-7, D10): with a server local skills are on only when the profile's
/// <c>local_skills.mode</c> is <c>allow</c> or its <c>developer_mode</c> is true; absent or unknown is off. Standalone they are on
/// unless the person or the machine policy turned them off, and a machine policy that lists folders keeps only those.
/// </summary>
public class SkillsPolicyTests
{
    private static LocalSkillsDecision Decide(string? mode, bool developer) =>
        CoreLocalSkillsPolicy.Instance.Decide(new AgentProfile { LocalSkills = new LocalSkillsSettings { Mode = mode!, DeveloperMode = developer } });

    [Theory]
    [InlineData("allow", false, true)]
    [InlineData("allow", true, true)]
    [InlineData("disable", true, true)] // developer mode, the owner's rule
    [InlineData("disable", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    [InlineData("mystery", false, false)] // unknown is off
    [InlineData("mystery", true, true)] // as local MCP: the person's developer mode is their own
    public void The_core_policy_reads_mode_and_developer_mode_as_local_mcp_does(string? mode, bool developer, bool allowed) =>
        Assert.Equal(allowed, Decide(mode, developer).Allowed);

    [Fact]
    public void A_profile_that_says_nothing_about_skills_is_off()
    {
        Assert.False(CoreLocalSkillsPolicy.Instance.Decide(new AgentProfile()).Allowed);
    }

    private static async Task<bool> GovernedOffers(string localSkills)
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var localMcp = """{"mode":"disable","developer_mode":false}""" + (localSkills.Length == 0 ? "" : ",\"local_skills\":" + localSkills);
        var gateway = new Gateway(localMcp: localMcp).Says("Hello.");
        using var agent = gateway.Builder().Use(new SkillsModule(From(folders))).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        var offered = agent.Tools.Any(t => t.Name is "load_skill" or "read_skill_resource");
        var body = gateway.ChatRequests[0].Json;
        var told = body.GetRawText().Contains("house-style");
        Assert.Equal(offered, told); // what the module offers is what the model is told
        return offered;
    }

    [Theory]
    [InlineData("""{"mode":"allow","developer_mode":false}""", true)]
    [InlineData("""{"mode":"disable","developer_mode":true}""", true)]
    [InlineData("""{"mode":"disable","developer_mode":false}""", false)]
    [InlineData("""{"mode":"mystery","developer_mode":false}""", false)]
    [InlineData("""{"developer_mode":false}""", false)]
    [InlineData("", false)] // an older server: the profile has no local_skills at all
    public async Task With_a_server_local_skills_follow_the_profile(string localSkills, bool on)
    {
        Assert.Equal(on, await GovernedOffers(localSkills));
    }

    [Fact]
    public async Task Standalone_skills_are_on_by_default_and_off_when_the_person_turned_them_off()
    {
        using var folders = new SkillFolders();
        folders.Standard();

        using var on = Standalone(new ScriptedModel(), From(folders)).Build();
        await on.StartAsync();
        Assert.Contains(on.Tools, t => t.Name == "load_skill");

        using var off = Standalone(new ScriptedModel(), From(folders), local: new LocalProfileOptions { LocalSkills = false }).Build();
        await off.StartAsync();
        Assert.DoesNotContain(off.Tools, t => t.Name is "load_skill" or "read_skill_resource");
    }

    [Fact]
    public async Task A_machine_policy_that_forbids_skills_turns_them_off_standalone()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, From(folders), policy: new MachinePolicy { Skills = PolicyRule.Forbidden }).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.DoesNotContain(agent.Tools, t => t.Name is "load_skill" or "read_skill_resource");
        Assert.DoesNotContain("house-style", model.Calls[0].Instructions);
        Assert.DoesNotContain("load_skill", model.Calls[0].Tools);
    }

    [Fact]
    public async Task A_machine_policy_that_lists_folders_keeps_only_those()
    {
        using var folders = new SkillFolders();
        var listed = Path.Combine(folders.Root, "listed");
        var other = Path.Combine(folders.Root, "other");
        folders.Standard("listed-skill", parent: listed);
        folders.Standard("other-skill", parent: other);
        var options = new SkillsOptions();
        options.Folders.Add(listed);
        options.Folders.Add(other);
        var model = new ScriptedModel().Says("Hello.");
        var policy = new MachinePolicy { Skills = new PolicyRule(PolicyAccess.Listed, new[] { listed + Path.DirectorySeparatorChar }) };
        using var agent = Standalone(model, options, policy: policy).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Contains("listed-skill", model.Calls[0].Instructions);
        Assert.DoesNotContain("other-skill", model.Calls[0].Instructions);
    }

    [Fact]
    public async Task A_machine_policy_that_lists_nothing_turns_skills_off()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var policy = new MachinePolicy { Skills = new PolicyRule(PolicyAccess.Listed, Array.Empty<string>()) };
        using var agent = Standalone(new ScriptedModel(), From(folders), policy: policy).Build();

        await agent.StartAsync();

        Assert.DoesNotContain(agent.Tools, t => t.Name is "load_skill" or "read_skill_resource");
    }

    [Fact]
    public async Task Skills_off_leaves_the_model_call_as_it_was()
    {
        using var folders = new SkillFolders();
        folders.Standard();
        var model = new ScriptedModel().Says("Hello.");
        using var agent = Standalone(model, From(folders), local: new LocalProfileOptions { LocalSkills = false }).Build();

        await (await agent.StartSessionAsync()).SendAsync("Hi");

        Assert.Empty(model.Calls[0].Tools);
        Assert.Equal("You are the docs assistant.", model.Calls[0].Instructions);
    }
}

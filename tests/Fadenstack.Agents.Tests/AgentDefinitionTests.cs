using System.Text.Json;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>sdk/51 F03, I03 commit 1: the agent definition an app ships, read in place of the server's profile.</summary>
public class AgentDefinitionTests
{
    /// <summary>Core's templates (release/0.7.1): the apps' own <c>contracts/*.json</c>, with <c>local_skills_mode</c> added.</summary>
    public static TheoryData<string> Templates() => new()
    {
        "word-assistant", "excel-assistant", "powerpoint-assistant", "outlook-assistant", "browser-assistant",
    };

    private static string Template(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Definitions", name + ".json"));

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_template_reads_with_its_contract_and_instructions(string name)
    {
        var json = Template(name);
        using var raw = JsonDocument.Parse(json);
        var version = raw.RootElement.GetProperty("version");

        var definition = AgentDefinition.FromJson(json);

        Assert.Equal(name, definition.Slug);
        Assert.Equal(raw.RootElement.GetProperty("name").GetString(), definition.Name);
        Assert.Equal(version.GetProperty("instructions").GetString(), definition.Instructions);
        Assert.Equal(version.GetProperty("host_tool_contract").GetArrayLength(), definition.HostToolContract.Count);
        Assert.All(definition.HostToolContract, e => Assert.NotEqual(ToolClass.Unknown, ToolModes.ParseClass(e.Risk)));
        Assert.Equal("disable", definition.LocalMcpMode);
        Assert.Equal("disable", definition.LocalSkillsMode);
        Assert.NotNull(definition.Ui);
        Assert.True(definition.Ui!.ContainsKey("greeting"));
    }

    [Fact]
    public void The_browser_template_keeps_its_server_tools()
    {
        var definition = AgentDefinition.FromJson(Template("browser-assistant"));

        Assert.Contains("current_time", definition.ServerTools!);
    }

    [Fact]
    public void An_unknown_or_missing_risk_is_unclassified_and_never_throws()
    {
        var definition = AgentDefinition.FromJson("""
            {"slug":"a","version":{"host_tool_contract":[
              {"name":"wipe","risk":"catastrophic"},
              {"name":"peek"},
              {"name":"note","risk":"  Write "},
              {"risk":"read"},
              {"name":"","risk":"read"}
            ]}}
            """);

        Assert.Equal(new[] { "wipe", "peek", "note" }, definition.HostToolContract.Select(e => e.Name));
        Assert.Equal(ToolClass.Unknown, ToolModes.ParseClass(definition.HostToolContract[0].Risk));
        Assert.Equal(ToolClass.Unknown, ToolModes.ParseClass(definition.HostToolContract[1].Risk));
        Assert.Equal(ToolClass.Write, ToolModes.ParseClass(definition.HostToolContract[2].Risk));
    }

    [Theory]
    [InlineData("\"allow\"", "allow")]
    [InlineData("\"ALLOW\"", "allow")]
    [InlineData("\"disable\"", "disable")]
    [InlineData("\"developer\"", "disable")]
    [InlineData("1", "disable")]
    [InlineData("null", "disable")]
    public void A_mode_other_than_allow_is_disable(string value, string expected)
    {
        var definition = AgentDefinition.FromJson(
            "{\"slug\":\"a\",\"version\":{\"local_mcp_mode\":" + value + ",\"local_skills_mode\":" + value + "}}");

        Assert.Equal(expected, definition.LocalMcpMode);
        Assert.Equal(expected, definition.LocalSkillsMode);
    }

    [Fact]
    public void A_definition_without_the_skills_mode_as_the_apps_ship_it_is_disable()
    {
        var definition = AgentDefinition.FromJson("""{"slug":"a","version":{"local_mcp_mode":"allow"}}""");

        Assert.Equal("allow", definition.LocalMcpMode);
        Assert.Equal("disable", definition.LocalSkillsMode);
    }

    [Fact]
    public void Unknown_fields_are_kept_and_model_alias_is_ignored()
    {
        const string json = """
            {"slug":"helper","name":"Helper","future":{"x":1},"version":{"instructions":"Be brief.","model_alias":"big",
             "ui":{"greeting":"Hi","languages":{"de":{"greeting":"Hallo"}},"reasoning":{"default":"low"},"delegates":["word-assistant"]},
             "next_field":[1,2]}}
            """;

        var definition = AgentDefinition.FromJson(json);

        using var expected = JsonDocument.Parse(json);
        using var written = JsonDocument.Parse(definition.ToJson());
        Assert.True(JsonElement.DeepEquals(expected.RootElement, written.RootElement));
        Assert.Equal(1, definition.Json.GetProperty("future").GetProperty("x").GetInt32());
        Assert.Equal("Hallo", definition.Ui!["languages"].GetProperty("de").GetProperty("greeting").GetString());
        Assert.True(definition.Ui.ContainsKey("reasoning"));
        Assert.True(definition.Ui.ContainsKey("delegates"));
        Assert.Equal("Be brief.", definition.Instructions);
    }

    [Fact]
    public void Without_a_name_or_instructions_the_slug_and_nothing_stand_in()
    {
        var definition = AgentDefinition.FromJson("""{"slug":"bare"}""");

        Assert.Equal("bare", definition.Name);
        Assert.Equal("", definition.Instructions);
        Assert.Empty(definition.HostToolContract);
        Assert.Null(definition.Ui);
        Assert.Null(definition.ServerTools);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"word-assistant\"")]
    [InlineData("{}")]
    [InlineData("{\"slug\":\"\"}")]
    [InlineData("{\"slug\":\"Word\"}")]
    [InlineData("{\"slug\":\"-word\"}")]
    [InlineData("{\"slug\":\"word-\"}")]
    [InlineData("{\"slug\":\"../word\"}")]
    [InlineData("{\"slug\":7}")]
    [InlineData("{\"slug\":\"word\\n\"}")] // a JSON "\n" at the end: .NET's $ would let it through
    [InlineData("not json")]
    public void A_body_without_a_valid_slug_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => AgentDefinition.FromJson(json));
    }

    [Fact]
    public void A_slug_of_64_characters_is_the_longest()
    {
        Assert.Equal(64, AgentDefinition.FromJson($$"""{"slug":"{{new string('a', 64)}}"}""").Slug.Length);
        Assert.Throws<FormatException>(() => AgentDefinition.FromJson($$"""{"slug":"{{new string('a', 65)}}"}"""));
    }
}

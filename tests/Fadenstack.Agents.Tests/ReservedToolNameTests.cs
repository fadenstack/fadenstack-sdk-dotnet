using System.ComponentModel;
using Fadenstack.Client.Tests;
using Fadenstack.Mcp;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The SDK keeps five tool names for itself (sdk/52 §10.5, the owner, 13 and 14 Nov 2026): <c>find_tool</c>, the tool search
/// a Fadenstack gateway takes as read (it only switches tools on; what it finds asks on its own calls), and the names of the
/// SDK's coming tools, <c>load_skill</c>, <c>read_skill_resource</c>, <c>run_skill_script</c> and <c>web_search</c>, whose
/// class the gateway sets too. So no app's tool may carry one, and only the SDK's own sources bring them. A tool source's
/// tool that loses its name to another is logged with the reason.
/// </summary>
public class ReservedToolNameTests
{
    private const string DeveloperMode = """{"mode":"disable","developer_mode":true}""";

    public static TheoryData<string> Reserved => new() { "find_tool", "load_skill", "read_skill_resource", "run_skill_script", "web_search" };

    private sealed class Tools
    {
        [AgentTool(ToolClass.Destructive, Name = "find_tool"), Description("Deletes what it finds.")]
        public string DeleteFound(string query) => "deleted " + query;
    }

    private sealed class SearchTools
    {
        [AgentTool(ToolClass.Read, Name = "web_search", OpenWorld = true), Description("Searches the web.")]
        public string Search(string query) => "results for " + query;
    }

    private sealed class AppSource(params AgentTool[] tools) : IToolSource
    {
        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>(tools);
    }

    private sealed class CoreSource(params AgentTool[] tools) : ICoreToolSource
    {
        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>(tools);
    }

    private static AgentTool Tool(string name, ToolClass toolClass) =>
        new(AIFunctionFactory.Create(() => name + " ran", name, "A test tool."), toolClass, "mcp", "demo");

    [Fact]
    public void The_sdk_keeps_five_names()
    {
        Assert.Equal(new[] { "find_tool", "load_skill", "read_skill_resource", "run_skill_script", "web_search" }, AgentTool.ReservedNames);
        Assert.Equal("find_tool", AgentTool.ToolSearchName);
    }

    [Theory]
    [MemberData(nameof(Reserved))]
    public void An_apps_tool_cannot_take_a_name_the_sdk_keeps(string name)
    {
        var builder = new Gateway().Builder();
        var function = AIFunctionFactory.Create((string query) => "ran " + query, name, "An app's tool.");

        var refused = Assert.Throws<ArgumentException>(() => builder.AddTool(function, ToolClass.Destructive));
        Assert.Contains($"'{name}' is a name the SDK keeps for its own tools", refused.Message);
        Assert.Throws<ArgumentException>(() => builder.AddTool(new AgentTool(function, ToolClass.Read)));
    }

    [Fact]
    public void An_attributed_method_cannot_take_a_name_the_sdk_keeps()
    {
        Assert.Throws<ArgumentException>(() => new Gateway().Builder().AddTools(new Tools()));
        Assert.Throws<ArgumentException>(() => new Gateway().Builder().AddTools(new SearchTools()));
        Assert.Throws<ArgumentException>(() => new Gateway().Builder().AddTools<SearchTools>());
        Assert.Throws<ArgumentException>(() => new Gateway().Builder().Use(new ModuleAdding(new SearchTools())));
    }

    private sealed class ModuleAdding(object tools) : IFadenstackModule
    {
        public string? Name => null;

        public void Configure(AgentBuilder builder) => builder.AddTools(tools);
    }

    [Theory]
    [MemberData(nameof(Reserved))]
    public async Task An_apps_source_has_a_tool_under_a_name_the_sdk_keeps_left_out_and_logged(string name)
    {
        var logs = new CapturingLoggerFactory();
        using var app = new Gateway().Says("Hi.").Builder()
            .AddToolSource(new AppSource(Tool(name, ToolClass.Destructive), Tool("other", ToolClass.Read))).UseLogging(logs).Build();
        await app.StartAsync();

        Assert.Equal(new[] { "other" }, app.Tools.Select(t => t.Name));
        Assert.Equal($"A tool source's tool {name} was left out: the name is the SDK's own", Assert.Single(logs.Entries, e => e.EventId == 2016).Message);
    }

    [Theory]
    [MemberData(nameof(Reserved))]
    public async Task Only_the_sdks_own_source_brings_a_tool_under_that_name(string name)
    {
        using var core = new Gateway().Says("Hi.").Builder()
            .AddToolSource(new AppSource(Tool(name, ToolClass.Destructive))).AddToolSource(new CoreSource(Tool(name, ToolClass.Read))).Build();
        await core.StartAsync();

        var tool = Assert.Single(core.Tools, t => t.Name == name);
        Assert.Equal(ToolClass.Read, tool.Class); // the SDK's own, though the app's source came first
    }

    [Fact]
    public async Task A_source_tool_that_loses_its_name_is_logged_with_what_won()
    {
        var logs = new CapturingLoggerFactory();
        var own = AIFunctionFactory.Create(() => "the app's", "read_page", "The app's own tool.");
        using var agent = new Gateway().Says("Hi.").Builder()
            .AddTool(own, ToolClass.Read)
            .AddToolSource(new AppSource(Tool("read_page", ToolClass.Destructive), Tool("notes", ToolClass.Read)))
            .AddToolSource(new AppSource(Tool("notes", ToolClass.Destructive)))
            .UseLogging(logs).Build();
        await agent.StartAsync();

        Assert.Equal(new[] { "read_page", "notes" }, agent.Tools.Select(t => t.Name));
        Assert.Equal(ToolClass.Read, agent.Tools.Single(t => t.Name == "read_page").Class); // the app's tool wins
        Assert.Equal(ToolClass.Read, agent.Tools.Single(t => t.Name == "notes").Class); // then the first source
        Assert.Equal(new[]
        {
            "A tool source's tool read_page was left out: the app's own tool has that name",
            "A tool source's tool notes was left out: an earlier tool source has that name",
        }, logs.Entries.Where(e => e.EventId == 2017).Select(e => e.Message));
    }

    [Fact]
    public async Task A_local_mcp_servers_tool_under_a_name_the_sdk_keeps_is_left_out_and_never_found()
    {
        // The module is one of the SDK's own sources: what a server brings under such a name would pass for the SDK's tool.
        var logs = new CapturingLoggerFactory();
        var rig = new McpRig
        {
            Tools =
            {
                McpRig.Tool("web_search", "Searches the web.", (string query) => "results"),
                McpRig.Tool("load_skill", "Loads a skill.", (string name) => "skill"),
                McpRig.Tool("find_tool", "Finds tools.", (string query) => "found"),
                McpRig.Tool("echo", "Echoes text back.", (string text) => text),
            },
        };
        var second = new McpRig { Tools = { McpRig.Tool("echo", "Echoes text back too.", (string text) => "two") } };
        using var module = new LocalMcpModule(new[] { rig.Server("web"), second.Server("other") });
        var gateway = new Gateway(localMcp: DeveloperMode)
            .Calls(("c0", "find_tool", """{"query":"search the web load a skill echo text"}"""))
            .Says("Done.");
        using var agent = gateway.Builder().AddLocalMcp(module).UseLogging(logs).Build();
        await agent.StartAsync();

        Assert.Equal(new[] { "find_tool", "echo" }, agent.Tools.Select(t => t.Name));
        Assert.Equal(ToolClass.Read, agent.Tools.Single(t => t.Name == "find_tool").Class); // the module's own search
        Assert.Equal("web", agent.Tools.Single(t => t.Name == "echo").Server);

        var session = await agent.StartSessionAsync(ToolMode.Auto);
        await session.SendAsync("Search the web");
        var found = McpRig.LastToolResult(gateway.ChatRequests[1]);
        Assert.Contains("echo: Echoes text back.", found);
        Assert.DoesNotContain("web_search", found);
        Assert.DoesNotContain("load_skill", found);
        Assert.DoesNotContain("Echoes text back too.", found);

        var reserved = logs.Entries.Where(e => e.EventId == 3007).Select(e => e.Message).Distinct().ToArray();
        Assert.Equal(new[] { "MCP server web brought a tool under a name the SDK keeps for its own tools; it was left out" }, reserved);
        Assert.Contains(logs.Entries, e => e.EventId == 3008
            && e.Message == "MCP server other brought a tool whose name MCP server web already brings; the first server's is kept");
        Assert.DoesNotContain(logs.Entries, e => e.Category == "Fadenstack.Mcp" && e.Message.Contains("web_search"));
    }
}

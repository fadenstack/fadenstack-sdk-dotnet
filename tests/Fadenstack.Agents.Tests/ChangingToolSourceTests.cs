using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Tool sources that change between turns: asked again at the start of the next turn, never inside one.</summary>
public class ChangingToolSourceTests
{
    /// <summary>A source whose tools the test sets; every ask is counted.</summary>
    private sealed class Source : IChangingToolSource
    {
        private IReadOnlyList<AgentTool> _tools;

        public Source(params string[] names) => _tools = names.Select(Tool).ToList();

        public int Asked { get; private set; }

        public bool Fails { get; set; }

        public event EventHandler? Changed;

        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken)
        {
            Asked++;
            if (Fails && Asked > 1)
            {
                throw new InvalidOperationException("the source is down");
            }
            return Task.FromResult(_tools);
        }

        public void Set(params string[] names)
        {
            _tools = names.Select(Tool).ToList();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        private static AgentTool Tool(string name) =>
            new(AIFunctionFactory.Create(() => name + " ran", name, "A test tool."), ToolClass.Read, "mcp", "demo");
    }

    private sealed class Fixed : IToolSource
    {
        private readonly AgentTool _tool;

        public Fixed(AgentTool tool) => _tool = tool;

        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>(new[] { _tool });
    }

    private static List<string> ToolNames(Recorded request) =>
        request.Json.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList()
            : new List<string>();

    private static string LastToolResult(Recorded request) =>
        request.Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;

    [Fact]
    public async Task A_source_that_changed_between_two_turns_shows_in_the_second_turns_tools()
    {
        var source = new Source("alpha");
        var gateway = new Gateway().Says("One.").Says("Two.");
        using var agent = gateway.Builder().AddToolSource(source).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("first");
        source.Set("alpha", "beta");
        Assert.Equal(new[] { "alpha" }, agent.Tools.Select(t => t.Name)); // not before the next turn starts
        await session.SendAsync("second");

        Assert.Equal(new[] { "alpha" }, ToolNames(gateway.ChatRequests[0]));
        Assert.Equal(new[] { "alpha", "beta" }, ToolNames(gateway.ChatRequests[1]));
        Assert.Equal(new[] { "alpha", "beta" }, agent.Tools.Select(t => t.Name));
        Assert.Equal(2, source.Asked); // once at start, once after the change
    }

    [Fact]
    public async Task A_source_is_not_asked_again_while_it_says_nothing()
    {
        var source = new Source("alpha");
        var gateway = new Gateway().Says("One.").Says("Two.").Says("Three.");
        using var agent = gateway.Builder().AddToolSource(source).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("a");
        await session.SendAsync("b");
        await session.SendAsync("c");
        Assert.Equal(1, source.Asked);
    }

    [Fact]
    public async Task A_change_inside_a_turn_waits_for_the_next_turn()
    {
        var source = new Source("alpha");
        var flip = AIFunctionFactory.Create(() =>
        {
            source.Set("alpha", "beta"); // while the turn runs
            return "changed";
        }, "flip", "Changes the source.");
        var gateway = new Gateway().Calls(("c1", "flip", "{}")).Says("Done.").Says("Next.");
        using var agent = gateway.Builder().AddToolSource(source).AddToolSource(new Fixed(new AgentTool(flip, ToolClass.Read))).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("flip it");
        await session.SendAsync("again");

        Assert.Equal(new[] { "alpha", "flip" }, ToolNames(gateway.ChatRequests[0]));
        Assert.Equal(new[] { "alpha", "flip" }, ToolNames(gateway.ChatRequests[1])); // the same turn, after the change: unchanged
        Assert.Equal(new[] { "alpha", "beta", "flip" }, ToolNames(gateway.ChatRequests[2]));
    }

    [Fact]
    public async Task A_tool_that_went_away_is_refused_as_no_longer_available()
    {
        var source = new Source("alpha", "beta");
        var gateway = new Gateway().Says("One.").Calls(("c1", "beta", "{}")).Says("Gone.");
        using var agent = gateway.Builder().AddToolSource(source).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("first");

        source.Set("alpha");
        await session.SendAsync("second");

        Assert.Equal("beta is no longer available.", LastToolResult(gateway.ChatRequests[2]));
    }

    [Fact]
    public async Task A_name_that_was_never_offered_is_still_unknown()
    {
        var gateway = new Gateway().Calls(("c1", "made_up", "{}")).Says("No.");
        using var agent = gateway.Builder().AddToolSource(new Source("alpha")).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("go");
        Assert.Equal("Tool 'made_up' does not exist here.", LastToolResult(gateway.ChatRequests[1]));
    }

    [Fact]
    public async Task A_source_that_fails_when_asked_again_keeps_its_tools()
    {
        var source = new Source("alpha") { Fails = true };
        var gateway = new Gateway().Says("One.").Says("Two.");
        using var agent = gateway.Builder().AddToolSource(source).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("first");
        source.Raise();
        await session.SendAsync("second");
        Assert.Equal(new[] { "alpha" }, ToolNames(gateway.ChatRequests[1]));
    }

    [Fact]
    public async Task Changes_from_many_threads_settle_on_the_newest_list()
    {
        var source = new Source("alpha");
        var gateway = new Gateway().Says("One.").Says("Two.");
        using var agent = gateway.Builder().AddToolSource(source).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("first");

        Parallel.For(0, 200, _ => source.Raise());
        source.Set("alpha", "beta", "gamma");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(source.Raise)));
        await session.SendAsync("second");

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, ToolNames(gateway.ChatRequests[1]));
    }

    [Fact]
    public async Task A_host_tool_still_wins_over_a_source_tool_of_the_same_name()
    {
        var gateway = new Gateway().Says("Hi.");
        using var agent = gateway.Builder().AddTools(new PageTools()).AddToolSource(new Source("read_page")).Build();
        await agent.StartAsync();
        Assert.Equal("host", agent.Tools.Single(t => t.Name == "read_page").Source);
    }
}

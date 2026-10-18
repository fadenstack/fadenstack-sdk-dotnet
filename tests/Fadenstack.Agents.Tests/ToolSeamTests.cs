using System.ComponentModel;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>What a tool says it writes, what a call gets besides the model's arguments, and what a stage may record.</summary>
public class ToolSeamTests
{
    /// <summary>A service a tool gets from its call, never from the model.</summary>
    [ToolService]
    public interface IStamp
    {
        string Turn { get; }

        string Stamp(string text);
    }

    private sealed class Stamp : IStamp
    {
        public Stamp(string turn) => Turn = turn;

        public string Turn { get; }

        string IStamp.Stamp(string text) => $"[{Turn.Substring(0, 4)}] {text}";
    }

    public sealed class WritingTools
    {
        public List<IStamp?> Got { get; } = new();

        [AgentTool(ToolClass.Write), Emits("text", "text/markdown"), Description("Inserts a reply.")]
        public string InsertReply(string text, IStamp stamp)
        {
            Got.Add(stamp);
            return stamp.Stamp(text);
        }

        [AgentTool(ToolClass.Write), Emits("text", "text/plain", EditKind.Rewritten, EditKindFrom = nameof(KindOf))]
        public string Improve(string text, bool light, IStamp? stamp = null)
        {
            Got.Add(stamp);
            return "improved";
        }

        private static EditKind KindOf(IReadOnlyDictionary<string, object?> arguments) =>
            arguments.TryGetValue("light", out var v) && v?.ToString() is "true" or "True" ? EditKind.Corrected : EditKind.Rewritten;

        [AgentTool(ToolClass.Read)]
        public string ReadPage(int page) => $"page {page}";
    }

    public sealed class BadTools
    {
        [AgentTool(ToolClass.Write), Emits("body", "text/plain")]
        public string Send(string text) => "sent";
    }

    private const string Contract = """[{"name":"insert_reply","risk":"write"},{"name":"improve","risk":"write"},{"name":"read_page","risk":"read"}]""";

    [Fact]
    public async Task A_tool_says_what_it_writes_and_the_agent_keeps_it_under_the_contracts_class()
    {
        var gateway = new Gateway(contract: Contract);
        using var agent = gateway.Builder().AddTools(new WritingTools()).Build();
        await agent.StartAsync();

        var insert = agent.Tools.Single(t => t.Name == "insert_reply");
        var output = Assert.Single(insert.Outputs);
        Assert.Equal(("text", "text/markdown", EditKind.Generated), (output.Argument, output.MediaType, output.EditKind));
        Assert.Empty(agent.Tools.Single(t => t.Name == "read_page").Outputs);
        var improve = Assert.Single(agent.Tools.Single(t => t.Name == "improve").Outputs);
        Assert.Equal(EditKind.Corrected, improve.EditKindFor(new Dictionary<string, object?> { ["light"] = true }));
        Assert.Equal(EditKind.Rewritten, improve.EditKindFor(new Dictionary<string, object?> { ["light"] = false }));

        var manifest = await Until(() => gateway.Manifests.FirstOrDefault());
        var tools = manifest.Json.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        var said = tools["insert_reply"].GetProperty("outputs")[0];
        Assert.Equal(("text", "text/markdown", "generated"), (said.GetProperty("argument").GetString(), said.GetProperty("media_type").GetString(),
            said.GetProperty("edit_kind").GetString()));
        Assert.False(tools["read_page"].TryGetProperty("outputs", out _));
    }

    [Fact]
    public void An_output_must_name_one_of_the_tools_parameters()
    {
        var e = Assert.Throws<ArgumentException>(() => AgentTool.From(new BadTools()));
        Assert.Contains("'body'", e.Message);
    }

    [Fact]
    public void A_tool_service_parameter_is_not_in_the_tools_schema()
    {
        var insert = AgentTool.From(new WritingTools()).Single(t => t.Name == "insert_reply");

        var properties = insert.Function.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(new[] { "text" }, properties);
    }

    private sealed class LookingStage : IToolStage
    {
        public List<IStamp?> Saw { get; } = new();

        public Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            Saw.Add(invocation.GetService<IStamp>());
            return next(invocation, cancellationToken);
        }
    }

    [Fact]
    public async Task Each_call_gets_its_own_service_and_its_stages_see_the_same_one()
    {
        var gateway = new Gateway(contract: Contract)
            .Calls(("call-1", "insert_reply", """{"text":"Dear Ms Huber,"}""")).Calls(("call-2", "insert_reply", """{"text":"Thanks."}""")).Says("Done.");
        var tools = new WritingTools();
        var stage = new LookingStage();
        var made = 0;
        using var agent = gateway.Builder().AddTools(tools).AddToolStage(StageOrder.Custom, stage)
            .AddToolService<IStamp>(i => { made++; return new Stamp(i.TurnId); })
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("Answer Ms Huber.");

        Assert.Equal(2, made);
        Assert.Equal(2, tools.Got.Count);
        Assert.NotSame(tools.Got[0], tools.Got[1]);
        Assert.Equal(stage.Saw, tools.Got); // the same instance in the stage and the tool
        Assert.Equal(result.TurnId, tools.Got[0]!.Turn);
        var completed = session.Transcript.Where(e => e.Type == Sessions.EntryTypes.ToolComplete).ToList();
        Assert.StartsWith($"[{result.TurnId.Substring(0, 4)}] Dear Ms Huber,", completed[0].Data.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Without_the_service_a_tool_gets_its_default_or_the_call_fails_with_a_reason()
    {
        var gateway = new Gateway(contract: Contract)
            .Calls(("call-1", "improve", """{"text":"teh","light":true}"""), ("call-2", "insert_reply", """{"text":"Hi"}""")).Says("Done.");
        var tools = new WritingTools();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Fix it.");

        Assert.Equal(new IStamp?[] { null }, tools.Got); // improve ran with its default; insert_reply never ran
        var results = session.Transcript.Where(e => e.Type == Sessions.EntryTypes.ToolComplete)
            .ToDictionary(e => e.Data.GetProperty("name").GetString()!, e => (e.Data.GetProperty("outcome").GetString(), e.Data.GetProperty("result").GetString()));
        Assert.Equal("ok", results["improve"].Item1);
        Assert.Equal("error", results["insert_reply"].Item1);
        Assert.Contains("needs IStamp", results["insert_reply"].Item2);
    }

    private sealed class RecordingStage : IToolStage
    {
        private readonly string _name;
        private readonly List<string> _order;

        public RecordingStage(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public async Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            _order.Add(_name);
            if (_name == "provenance")
            {
                Assert.Equal(Gateway.Slug, invocation.Agent!.Slug);
                await invocation.RecordAsync("example.checked", new { tool = invocation.Tool.Name, outputs = invocation.Tool.Outputs.Count });
                await invocation.NoticeAsync(new GovernanceNotice { Outcome = "example_notice", Stage = "tool", Message = "Checked." });
                await Assert.ThrowsAsync<ArgumentException>(() => invocation.RecordAsync(Sessions.EntryTypes.ToolComplete, new { }));
            }
            return await next(invocation, cancellationToken);
        }
    }

    [Fact]
    public async Task Stages_run_in_their_slots_and_may_record_entries_and_notices_in_the_turn()
    {
        var gateway = new Gateway(contract: Contract).Calls(("call-1", "read_page", """{"page":2}""")).Says("Page 2.");
        var order = new List<string>();
        using var agent = gateway.Builder().AddTools(new WritingTools())
            .AddToolStage(StageOrder.Custom, new RecordingStage("custom", order))
            .AddToolStage(StageOrder.Provenance, new RecordingStage("provenance", order))
            .AddToolStage(StageOrder.Pii, new RecordingStage("pii", order)).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("Read page 2.");

        Assert.Equal(new[] { "pii", "provenance", "custom" }, order);
        Assert.True(StageOrder.Pii < StageOrder.Provenance && StageOrder.Provenance < StageOrder.Custom);
        var entry = session.Transcript.Single(e => e.Type == "example.checked");
        Assert.Equal((result.TurnId, "read_page", 0), (entry.TurnId, entry.Data.GetProperty("tool").GetString(), entry.Data.GetProperty("outputs").GetInt32()));
        Assert.Equal("example_notice", Assert.Single(result.Notices).Outcome);
        Assert.Equal("Page 2.", result.Text); // the history ignores the module's entries
    }

    private static async Task<T> Until<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 200; i++)
        {
            if (find() is { } found)
            {
                return found;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }
}

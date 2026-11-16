using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Mcp;
using Fadenstack.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;
using Card = Fadenstack.Agents.Tests.BypassTests.Card;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The approval floor, "the never-skip list" (sdk/38 §4.3; sdk/51 F08 §2, owner, 16 Nov): each item holds at every level, Ask,
/// Auto and Bypass. It records the rule the SDK and the gateway already follow; these tests change no behaviour. Bypass is real:
/// the server's grant with the second sign-in (<see cref="ChatSession.StartBypassAsync"/>, as <c>BypassTests</c> does), and every
/// case checks that the turn went to the server in the level it names.
/// </summary>
public class ApprovalFloorTests
{
    private const string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":60,"evidence":"calls"}}""";

    private const string DeveloperMode = """{"mode":"disable","developer_mode":true}""";

    private static (Gateway Gateway, FakeGrants Grants) Server(string? contract = null, string localMcp = """{"mode":"disable","developer_mode":false}""")
    {
        var gateway = new Gateway(contract: contract, localMcp: localMcp) { MaxLevel = "\"bypass\"" };
        var grants = gateway.UseGrants(Offered);
        return (gateway, grants);
    }

    /// <summary>A session at the level; in Bypass the conversation's own grant is made through the card and the second sign-in.</summary>
    private static async Task<ChatSession> OpenAsync(FadenstackAgent agent, ToolMode level)
    {
        if (level != ToolMode.Bypass)
        {
            var plain = await agent.StartSessionAsync(level);
            Assert.Equal(level, plain.EffectiveToolMode);
            return plain;
        }
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        Assert.NotNull(await session.StartBypassAsync());
        Assert.Equal((ToolMode.Bypass, ToolMode.Bypass), (session.ToolMode, session.EffectiveToolMode));
        return session;
    }

    /// <summary>What the server saw: every request of the turn carried the level, so the turn really ran in it.</summary>
    private static void RanIn(Gateway gateway, ToolMode level)
    {
        Assert.NotEmpty(gateway.ChatRequests);
        Assert.All(gateway.ChatRequests, r => Assert.Equal(level.ToWire(), r.Header("x-faden-tool-mode")));
    }

    /// <summary>The gateway says the conversation's Bypass grant covers the call (only in Bypass), as the real one would.</summary>
    private static string[] Covering(ChatSession session, ToolMode level, string callId, string tool) =>
        level == ToolMode.Bypass
            ? new[] { Gateway.Custom("faden.grant_used", new { toolCallId = callId, toolCallName = tool, grantId = session.Bypass!.Id }) }
            : Array.Empty<string>();

    private static Gateway Script(Gateway gateway, IEnumerable<string> events, string callId, string tool, string arguments) =>
        gateway.Then(events.Concat(new[] { Gateway.ToolCalls((callId, tool, arguments)), Gateway.Finish("tool_calls"), FakeServer.Done }).ToArray());

    [Fact]
    public void The_list_holds_exactly_the_seven_items_in_the_order_of_sdk_38_with_the_two_not_yet_marked_and_destructive_apart()
    {
        Assert.Equal(new[]
        {
            "refused", "ask_first_rule", "author_always", "physical", "open_world_tainted", "mcp_server_question", "gateway_held",
        }, ApprovalFloor.NeverSkipped);
        Assert.Equal(new[]
        {
            ApprovalFloor.Refused, ApprovalFloor.AskFirstRule, ApprovalFloor.AuthorAlways, ApprovalFloor.Physical,
            ApprovalFloor.OpenWorldTainted, ApprovalFloor.McpServerQuestion, ApprovalFloor.GatewayHeld,
        }, ApprovalFloor.NeverSkipped);
        Assert.Equal(new[] { ApprovalFloor.Physical, ApprovalFloor.OpenWorldTainted }, ApprovalFloor.NotMarkedYet);
        Assert.Equal("destructive_or_unclassified", ApprovalFloor.DestructiveOrUnclassified);
        Assert.DoesNotContain(ApprovalFloor.DestructiveOrUnclassified, ApprovalFloor.NeverSkipped);
    }

    [Fact]
    public void The_approvals_page_lists_each_item()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fadenstack.slnx")))
        {
            directory = directory.Parent;
        }
        var page = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "approvals.md"));
        foreach (var id in ApprovalFloor.NeverSkipped.Concat(new[] { ApprovalFloor.DestructiveOrUnclassified }))
        {
            Assert.Contains("`" + id + "`", page);
        }
    }

    [Theory]
    [InlineData(ToolMode.Ask)]
    [InlineData(ToolMode.Auto)]
    [InlineData(ToolMode.Bypass)]
    public async Task Refused_a_call_the_gateway_denies_does_not_run_and_nobody_is_asked(ToolMode level)
    {
        var (gateway, _) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var session = await OpenAsync(agent, level);
        Script(gateway, Covering(session, level, "call-1", "add_comment").Concat(new[]
        {
            Gateway.Custom("faden.tool_denied", new { toolCallId = "call-1", toolCallName = "add_comment", message = "Comments are off.", decisionId = "d-1" }),
        }), "call-1", "add_comment", """{"page":1,"text":"a"}""").Says("Not added.");

        var result = await session.SendAsync("Comment.");

        Assert.Empty(tools.Ran);
        Assert.Empty(card.Asked);
        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        RanIn(gateway, level);
    }

    [Theory]
    [InlineData(ToolMode.Ask)]
    [InlineData(ToolMode.Auto)]
    [InlineData(ToolMode.Bypass)]
    public async Task Ask_first_rule_a_call_the_gateway_marks_asks_over_an_always_the_person_kept_and_under_the_bypass_grant(ToolMode level)
    {
        var (gateway, _) = Server();
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow) { Source = "host" });
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder(approvals: store).AddTools(tools).UseApprovals(card).Build();
        var session = await OpenAsync(agent, level);
        Script(gateway, Covering(session, level, "call-1", "add_comment").Concat(new[]
        {
            Gateway.Custom("faden.approval_required",
                new { toolCallId = "call-1", toolCallName = "add_comment", message = "Comments need a second look.", rule = true }),
        }), "call-1", "add_comment", """{"page":1,"text":"a"}""").Says("Ok.");

        await session.SendAsync("Comment.");

        var asked = Assert.Single(card.Asked);
        Assert.Equal(("add_comment", true, false, false), (asked.ToolName, asked.RequiredByRule, asked.CanAllowAlways, asked.CanAllowForSession));
        Assert.Empty(tools.Ran); // the card said no
        RanIn(gateway, level);
    }

    [Theory]
    [InlineData(ToolMode.Ask)]
    [InlineData(ToolMode.Auto)]
    [InlineData(ToolMode.Bypass)]
    public async Task Author_always_a_contract_tool_with_approval_always_asks_over_an_always_the_person_kept_and_under_the_bypass_grant(ToolMode level)
    {
        var (gateway, _) = Server(contract: """[{"name":"add_comment","risk":"write","approval":"always"}]""");
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow) { Source = "host" });
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder(approvals: store).AddTools(tools).UseApprovals(card).Build();
        var session = await OpenAsync(agent, level);
        Script(gateway, Covering(session, level, "call-1", "add_comment"), "call-1", "add_comment", """{"page":1,"text":"a"}""").Says("Ok.");

        await session.SendAsync("Comment.");

        var asked = Assert.Single(card.Asked);
        Assert.Equal(("add_comment", true, false, false), (asked.ToolName, asked.RequiredByRule, asked.CanAllowAlways, asked.CanAllowForSession));
        Assert.Empty(tools.Ran);
        RanIn(gateway, level);
    }

    [Theory]
    [InlineData(ToolMode.Ask)]
    [InlineData(ToolMode.Auto)]
    [InlineData(ToolMode.Bypass)]
    public async Task Gateway_held_a_gateway_tool_the_gateway_holds_asks_the_person_who_answers_the_gateway(ToolMode level)
    {
        var (gateway, _) = Server();
        var card = new Card(approve: false);
        using var agent = gateway.Builder().UseApprovals(card).Build();
        var session = await OpenAsync(agent, level);
        // A call the gateway holds is one it did not cover: it sends no grant_used for it.
        gateway.Then(Gateway.Custom("faden.approval_required", new
        {
            approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "Commenting needs your OK.",
            timeoutS = 300, toolClass = "write", rule = false,
        }), Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);

        await session.SendAsync("Comment on the issue.");

        var asked = Assert.Single(card.Asked);
        Assert.Equal(("mcp.github.comment", "gateway"), (asked.ToolName, asked.Source));
        var answer = gateway.Server.Requests.Single(r => r.Path.EndsWith("/approvals/ap-1"));
        Assert.False(answer.Json.GetProperty("approved").GetBoolean()); // the person's answer, not the SDK's own
        RanIn(gateway, level);
    }

    [Theory]
    [InlineData(ToolMode.Ask, "delete_page", """{"page":1}""")]
    [InlineData(ToolMode.Auto, "delete_page", """{"page":1}""")]
    [InlineData(ToolMode.Bypass, "delete_page", """{"page":1}""")]
    [InlineData(ToolMode.Ask, "stamp", "{}")]
    [InlineData(ToolMode.Auto, "stamp", "{}")]
    [InlineData(ToolMode.Bypass, "stamp", "{}")]
    public async Task Destructive_or_unclassified_asks_in_ask_and_auto_and_in_bypass_runs_unasked_only_under_the_grant_the_gateway_covers(
        ToolMode level, string tool, string arguments)
    {
        var (gateway, grants) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var session = await OpenAsync(agent, level);
        Script(gateway, Covering(session, level, "call-1", tool), "call-1", tool, arguments).Says("Done.");

        await session.SendAsync("Go.");

        if (level == ToolMode.Bypass)
        {
            Assert.Empty(card.Asked);
            Assert.Single(tools.Ran);
            var report = Assert.Single(grants.Reports);
            Assert.Equal((session.Bypass!.Id, "call-1"), (report.Grant, Assert.Single(report.Calls)));
        }
        else
        {
            var asked = Assert.Single(card.Asked);
            Assert.Equal((tool, false, false), (asked.ToolName, asked.CanAllowAlways, asked.CanAllowForSession));
            Assert.Empty(tools.Ran);
        }
        RanIn(gateway, level);
    }

    [Theory]
    [InlineData("delete_page", """{"page":1}""")]
    [InlineData("stamp", "{}")]
    public async Task Destructive_or_unclassified_without_the_grants_cover_bypass_asks_as_well(string tool, string arguments)
    {
        var (gateway, _) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var session = await OpenAsync(agent, ToolMode.Bypass);
        gateway.Calls(("call-1", tool, arguments)).Says("Not done.");

        await session.SendAsync("Go.");

        Assert.Equal(tool, Assert.Single(card.Asked).ToolName);
        Assert.Empty(tools.Ran);
        RanIn(gateway, ToolMode.Bypass);
    }

    private static McpServerTool Questioner() => McpRig.Tool("ask_user", "Asks the person.", async (McpServer server, CancellationToken ct) =>
    {
        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = { ["name"] = new ElicitRequestParams.StringSchema { Title = "Name", Description = "Who to greet." } },
            Required = ["name"],
        };
        var result = await server.ElicitAsync(new ElicitRequestParams { Message = "Tell me more", RequestedSchema = schema }, ct);
        var values = result.Content is null ? "" : string.Join(",", result.Content.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        return $"{result.Action}|{values}";
    });

    [Theory]
    [InlineData(ToolMode.Ask, true)]
    [InlineData(ToolMode.Auto, true)]
    [InlineData(ToolMode.Bypass, true)]
    [InlineData(ToolMode.Ask, false)]
    [InlineData(ToolMode.Auto, false)]
    [InlineData(ToolMode.Bypass, false)]
    public async Task Mcp_server_question_reaches_the_person_and_the_sdk_never_answers_it_itself(ToolMode level, bool personAnswers)
    {
        var rig = new McpRig { Tools = { Questioner() } };
        var (gateway, _) = Server(localMcp: DeveloperMode);
        var seen = new List<McpQuestion>();
        var module = new LocalMcpModule(new[] { rig.Server("notes-db") })
        {
            ReconnectDelay = TimeSpan.Zero,
            Ask = (question, _) =>
            {
                seen.Add(question);
                return Task.FromResult(personAnswers ? McpAnswer.Accept(new Dictionary<string, object?> { ["name"] = "Anna" }) : McpAnswer.Cancel());
            },
        };
        var card = new Card(approve: true); // the call itself may run; the question is what is asked about
        using var agent = gateway.Builder().AddLocalMcp(module).UseApprovals(card).Build();
        using var _m = module;
        await agent.StartAsync();
        var session = await OpenAsync(agent, level);
        gateway.Calls(("c0", "find_tool", """{"query":"ask the person"}""")).Calls(("c1", "ask_user", "{}")).Says("Done.");

        var result = await session.SendAsync("Ask me.");

        Assert.Equal(ChatOutcome.Completed, result.Outcome);
        var question = Assert.Single(seen); // it reached the app's question handler, not an answer made up by the SDK
        Assert.Equal(("notes-db", "Tell me more", session.Id), (question.Server, question.Message, question.SessionId));
        Assert.Contains(personAnswers ? "accept|name=Anna" : "cancel|", McpRig.LastToolResult(gateway.ChatRequests.Last()));
        RanIn(gateway, level);
    }
}

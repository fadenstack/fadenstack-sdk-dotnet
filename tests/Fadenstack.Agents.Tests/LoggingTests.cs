using System.ComponentModel;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Mcp;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// What the agent logs, traces and counts. The guarantee: names, ids, counts, durations and outcomes -- never content, never
/// a token, never who the user is. Every sentinel below starts with <c>SENTINEL</c>; none may turn up anywhere.
/// </summary>
[Collection(PathsCollection.Name)]
public class LoggingTests : IDisposable
{
    private const string Prompt = "SENTINEL-PROMPT-31";
    private const string Answer = "SENTINEL-ANSWER-32";
    private const string Reasoning = "SENTINEL-REASONING-33";
    private const string Attachment = "SENTINEL-ATTACHMENT-34";
    private const string Arguments = "SENTINEL-ARGUMENTS-35";
    private const string Result = "SENTINEL-RESULT-36";
    private const string Notice = "SENTINEL-NOTICE-37";
    private const string User = """{"tenant_id":"t-1","user_id":"user-sentinel-38","agent_id":"a-1","device_id":null}""";
    private const string DeveloperMode = """{"mode":"disable","developer_mode":true}""";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-logging-" + Guid.NewGuid());

    public LoggingTests() => FadenstackPaths.RootForTests = _temporary;

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    /// <summary>Tools whose arguments and results carry content.</summary>
    private sealed class SentinelTools
    {
        [AgentTool(ToolClass.Read), Description("Reads a page of the document.")]
        public string ReadPage(int page, string note) => $"{Result} {note}";

        [AgentTool(ToolClass.Write), Description("Adds a comment to a page.")]
        public string AddComment(int page, string text) => Result;
    }

    private static void AssertNoContent(IEnumerable<string> texts) => Assert.DoesNotContain(texts,
        t => t.Contains("SENTINEL", StringComparison.OrdinalIgnoreCase) || t.Contains("agent-token"));

    /// <summary>A turn that reasons, calls a read and a write tool with content in their arguments, gets a notice, and answers.</summary>
    private static Gateway Scripted() => new Gateway { Principal = User }
        .Then(
            FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { reasoning_content = Reasoning } } } }),
            Gateway.Custom("faden.governance", new { outcome = "observe", stage = "output", category = "pii", message = Notice, decision_id = "d-1" }),
            Gateway.ToolCalls(("call-1", "read_page", $$"""{"page":1,"note":"{{Arguments}}"}"""), ("call-2", "add_comment", $$"""{"page":1,"text":"{{Arguments}}"}""")),
            Gateway.Finish("tool_calls"), FakeServer.Done)
        .Says(Answer);

    [Fact]
    public async Task A_full_turn_logs_and_traces_names_ids_counts_and_outcomes_and_no_content()
    {
        var gateway = Scripted();
        var logs = new CapturingLoggerFactory();
        using var telemetry = new TelemetryCapture();
        using var agent = gateway.Builder().AddTools(new SentinelTools()).UseLogging(logs)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync($"Please read {Prompt}",
            new[] { new MessageAttachment { Title = $"Selection {Attachment}", Content = Attachment, Source = "word", Kind = "selection" } });

        // The content is really there: in the answer, and on its way to the gateway.
        Assert.Equal((ChatOutcome.Completed, Answer), (result.Outcome, result.Text));
        Assert.Contains(Prompt, gateway.ChatRequests[0].Body);
        Assert.Contains(Attachment, gateway.ChatRequests[0].Body);
        Assert.Contains(Arguments, gateway.ChatRequests[1].Body);

        string[] Messages(int id) => logs.Entries.Where(e => e.EventId == id).Select(e => e.Message).ToArray();
        Assert.Equal("Agent docs-assistant loaded: version 3, edition community, 1 features, user partition True", Assert.Single(Messages(2000)));
        Assert.Equal("Agent docs-assistant tools settled: 2 host, 0 from sources, 0 dropped by the contract", Assert.Single(Messages(2001)));
        Assert.Equal($"Turn {result.TurnId} of conversation {session.Id} started in tool mode ask with 1 attachments", Assert.Single(Messages(2010)));
        Assert.Matches($@"^Turn {result.TurnId} of conversation {session.Id} ended ok \(Completed\) after 2 rounds in \d+ ms, tool mode ask$",
            Assert.Single(Messages(2011)));
        Assert.Collection(Messages(2020).Select(m => System.Text.RegularExpressions.Regex.Replace(m, @"in \d+ ms", "in N ms")),
            m => Assert.Equal("Tool read_page (read, host) ended ok in N ms, approval none", m),
            m => Assert.Equal("Tool add_comment (write, host) ended ok in N ms, approval once", m));
        Assert.Matches(@"^Approval for add_comment \(write\): once, asked True, waited \d+ ms$", Assert.Single(Messages(2021)));
        Assert.Equal("Governance notice observe at stage output, category pii, code (null), decision d-1", Assert.Single(Messages(2030)));
        Assert.Equal(2, Messages(1000).Count(m => m.StartsWith("POST /v1/agents/docs-assistant/chat/completions answered 200")));
        Assert.Contains(logs.Entries, e => e.EventId == 1000 && e.Category == "Fadenstack.Client");
        Assert.Contains($"ConversationId={session.Id}", logs.Texts);
        Assert.Contains($"TurnId={result.TurnId}", logs.Texts);

        Assert.Contains(telemetry.Spans, s => s.StartsWith("invoke_agent docs-assistant") && s.Contains($"gen_ai.conversation.id={session.Id}")
            && s.Contains("gen_ai.operation.name=invoke_agent") && s.Contains("fadenstack.turn.rounds=2") && s.Contains("fadenstack.turn.status=ok"));
        Assert.Equal(2, telemetry.Spans.Count(s => s.StartsWith("chat") && s.Contains($"gen_ai.conversation.id={session.Id}") && s.Contains("gen_ai.operation.name=chat")));
        Assert.Contains(telemetry.Spans, s => s.StartsWith("execute_tool read_page") && s.Contains("gen_ai.tool.name=read_page")
            && s.Contains("gen_ai.tool.call.id=call-1") && s.Contains("fadenstack.tool.class=read") && s.Contains("fadenstack.approval=none"));
        Assert.Contains(telemetry.Spans, s => s.StartsWith("execute_tool add_comment") && s.Contains("fadenstack.approval=once"));
        Assert.Contains(telemetry.Spans, s => s.StartsWith("HTTP POST") && s.Contains("http.route=/v1/agents/docs-assistant/chat/completions"));
        Assert.Contains("fadenstack.agent.turns | outcome=ok", telemetry.Measurements);
        Assert.Contains("fadenstack.agent.tool_calls | class=write | outcome=ok", telemetry.Measurements);
        Assert.Contains("fadenstack.agent.tool.duration | class=read | outcome=ok", telemetry.Measurements);
        Assert.Contains("fadenstack.agent.approval.wait | class=write", telemetry.Measurements);

        AssertNoContent(logs.Texts);
        AssertNoContent(telemetry.Texts);
    }

    [Fact]
    public async Task A_stopped_turn_and_a_declined_tool_are_outcomes_in_the_log_too()
    {
        var gateway = new Gateway().Calls(("call-1", "add_comment", $$"""{"page":1,"text":"{{Arguments}}"}""")).Says(Answer);
        var logs = new CapturingLoggerFactory();
        using var agent = gateway.Builder().AddTools(new SentinelTools()).UseLogging(logs)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Deny($"no {Notice}"))).Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("Go.");

        Assert.Equal(ChatOutcome.ToolDenied, result.Outcome);
        Assert.Contains(logs.Entries, e => e.EventId == 2020 && e.Message.StartsWith("Tool add_comment (write, host) ended denied"));
        Assert.Contains(logs.Entries, e => e.EventId == 2021 && e.Message.StartsWith("Approval for add_comment (write): declined"));
        Assert.Contains(logs.Entries, e => e.EventId == 2011 && e.Message.Contains("ended ok (ToolDenied)"));
        AssertNoContent(logs.Texts);
    }

    [Fact]
    public async Task A_tool_the_model_made_up_is_logged_as_unknown_not_by_the_models_words()
    {
        var gateway = new Gateway().Calls(("call-1", "SENTINEL_made_up_tool", "{}")).Says(Answer);
        var logs = new CapturingLoggerFactory();
        using var telemetry = new TelemetryCapture();
        using var agent = gateway.Builder().AddTools(new SentinelTools()).UseLogging(logs).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Go.");

        Assert.Contains(logs.Entries, e => e.EventId == 2020 && e.Message.StartsWith("Tool (unknown) (unknown, host) ended error"));
        AssertNoContent(logs.Texts);
        AssertNoContent(telemetry.Texts);
    }

    [Fact]
    public async Task The_builders_factory_reaches_the_client_and_bypass_mode_and_dropped_tools_are_logged()
    {
        var logs = new CapturingLoggerFactory();
        var gateway = new Gateway(contract: """[{"name":"read_page","risk":"read"}]""");
        using var agent = gateway.Builder().AddTools(new SentinelTools()).UseLogging(logs).UsePermissionMode(PermissionMode.Bypass).Build();

        await agent.StartAsync();

        Assert.Equal(new[] { "Fadenstack.Agents", "Fadenstack.Client" }, logs.Entries.Select(e => e.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal("Host tool add_comment is not in the agent's contract and was dropped", Assert.Single(logs.Entries, e => e.EventId == 2002).Message);
        Assert.Contains("1 dropped by the contract", Assert.Single(logs.Entries, e => e.EventId == 2001).Message);
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Entries, e => e.EventId == 2003).Level);
    }

    [Fact]
    public async Task A_client_made_elsewhere_keeps_its_own_factory()
    {
        var logs = new CapturingLoggerFactory();
        var gateway = new Gateway();
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = Gateway.Slug, Credentials = new Auth.StaticTokenProvider("agent-token"),
            HttpClient = new HttpClient(gateway.Server), LoggerFactory = logs,
        });
        using var agent = FadenstackAgent.CreateBuilder().UseClient(client).UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore()).Build();

        await agent.StartAsync();

        Assert.Same(logs, agent.Settings.LoggerFactory);
        Assert.Contains(logs.Entries, e => e.EventId == 2000);
    }

    [Fact]
    public async Task Without_a_factory_a_turn_runs_and_logs_nowhere()
    {
        using var agent = new Gateway().Says(Answer).Builder().Build();
        var session = await agent.StartSessionAsync();

        var result = await session.SendAsync("Hi");

        Assert.Equal(Answer, result.Text);
        Assert.Same(NullLoggerFactory.Instance, agent.Settings.LoggerFactory);
        Assert.Same(NullLoggerFactory.Instance, agent.Client.Options.LoggerFactory);
    }

    [Fact]
    public async Task A_local_mcp_server_is_logged_by_label_transport_and_tool_count_never_its_program_or_arguments()
    {
        var logs = new CapturingLoggerFactory();
        var broken = LocalMcpServer.Stdio("broken", "/nonexistent/SENTINEL-mcp-server", ["--token", "SENTINEL-secret"]);
        using var allowed = new LocalMcpModule([broken]);
        using var agent = new Gateway(localMcp: DeveloperMode).Builder().AddLocalMcp(allowed).UseLogging(logs).Build();
        await agent.StartAsync();

        using var refused = new LocalMcpModule([LocalMcpServer.Http("notes", new Uri("https://SENTINEL.example/mcp?key=SENTINEL"))]);
        using var strict = new Gateway().Builder().AddLocalMcp(refused).UseLogging(logs).Build();
        await strict.StartAsync();

        Assert.Equal($"MCP server broken (stdio) could not be attached ({Assert.Single(allowed.Skipped).Error!.GetType().Name})",
            Assert.Single(logs.Entries, e => e.EventId == 3002).Message);
        Assert.Equal("MCP server notes (http) was refused by the agent's policy", Assert.Single(logs.Entries, e => e.EventId == 3001).Message);
        Assert.All(logs.Entries.Where(e => e.EventId is >= 3000 and < 3100), e => Assert.Equal("Fadenstack.Mcp", e.Category));
        AssertNoContent(logs.Texts);
    }

    [Fact]
    public void The_move_of_earlier_sessions_is_logged_by_folder_count_and_never_the_user()
    {
        Directory.CreateDirectory(Path.Combine(_temporary, "data", "sessions"));
        Directory.CreateDirectory(Path.Combine(_temporary, "data", "approvals"));
        var logs = new CapturingLoggerFactory();

        UserDataMigration.Adopt("user-sentinel-38", logs.CreateLogger("test"));

        Assert.Equal("The earlier sessions and approvals (2 folders) moved into the user's own folder", Assert.Single(logs.Entries, e => e.EventId == 2004).Message);
        AssertNoContent(logs.Texts);
    }
}

using Fadenstack.Agents.Delegation;
using Fadenstack.Agents.Registry;
using Xunit;

namespace Fadenstack.Agents.Delegation.Tests;

/// <summary>
/// The delegation transport end to end over a real pipe with a stand-in for the add-in's agent: A2A 1.0 JSON-RPC (the A2A
/// library's server and models) between <see cref="PipeDelegationClient"/> and <see cref="PipeDelegationServer"/>. Runs on
/// .NET 10 and on .NET Framework 4.8, the runtime of the Office add-ins.
/// </summary>
public sealed class TransportTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-transport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Runner : IDelegatedTaskRunner
    {
        public Func<TaskRequest, IDelegatedTaskChannel, CancellationToken, Task<TaskOutcome>> Run { get; set; } =
            (request, _, _) => Task.FromResult(new TaskOutcome(request.TaskId, TaskState.Completed, "ok"));

        public List<TaskRequest> Requests { get; } = new();

        public Task<TaskOutcome> RunAsync(TaskRequest request, IDelegatedTaskChannel channel, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Run(request, channel, cancellationToken);
        }
    }

    private sealed class Observer : ITaskObserver
    {
        public List<string> Statuses { get; } = new();
        public Func<ApprovalAsk, CancellationToken, Task<ApprovalChoice>> OnApproval { get; set; } = (_, _) => Task.FromResult(ApprovalChoice.Deny);
        public List<ApprovalAsk> Asked { get; } = new();

        public void Status(TaskState state, string? text) => Statuses.Add(state.ToWire() + (text is null ? "" : ":" + text));

        public Task<ApprovalChoice> ApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        {
            Asked.Add(ask);
            return OnApproval(ask, cancellationToken);
        }
    }

    private static readonly AgentCardSummary Card = new()
    {
        Slug = "word-assistant", Name = "Word", Description = "Edits the open document.",
        Skills = new[] { new AgentCardSkill("Comments", "Adds comments") },
    };

    private (PipeDelegationServer Server, Runner Runner) Serve(int maxBytes = FrameStream.DefaultMaxBytes)
    {
        var server = new PipeDelegationServer(Card, maxBytes);
        var runner = new Runner();
        server.Start(runner);
        _disposables.Add(server);
        return (server, runner);
    }

    private static async Task<T> Within<T>(Task<T> task, int seconds)
    {
        await task.WaitAsyncWithin(TimeSpan.FromSeconds(seconds));
        return await task;
    }

    private static TaskRequest Request(string text = "Comment on page 2.") =>
        new("t-1", text, new[] { "report.docx" }, "desktop-assistant", "Desktop agent");

    [Fact]
    public async Task A_task_completes_with_the_answer_as_artifact_and_arrives_with_its_sender_and_references()
    {
        var (server, runner) = Serve();
        runner.Run = async (request, channel, _) =>
        {
            await channel.StatusAsync(TaskState.Working, "Using read_page");
            return new TaskOutcome(request.TaskId, TaskState.Completed, "Page 2 has 3 terminals.");
        };
        var observer = new Observer();

        var outcome = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), observer, CancellationToken.None);

        Assert.Equal((TaskState.Completed, "Page 2 has 3 terminals."), (outcome.State, outcome.Text));
        Assert.Contains("working:Using read_page", observer.Statuses);
        Assert.Equal("completed", observer.Statuses.Last());
        var received = Assert.Single(runner.Requests);
        Assert.Equal(("Comment on page 2.", "desktop-assistant", "Desktop agent", "report.docx"),
            (received.Text, received.FromSlug, received.FromName, received.References.Single()));
    }

    [Fact]
    public async Task The_document_arrives_in_the_message_metadata_and_is_absent_when_none_is_given()
    {
        var (server, runner) = Serve();

        await new PipeDelegationClient().RunAsync(server.Endpoint, Request() with { Document = @"C:\Docs\report.docx" }, new Observer(), CancellationToken.None);
        await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), new Observer(), CancellationToken.None);

        Assert.Equal(new string?[] { @"C:\Docs\report.docx", null }, runner.Requests.Select(r => r.Document).ToArray());
    }

    [Theory]
    [InlineData("Once")]
    [InlineData("Chat")]
    [InlineData("Deny")]
    public async Task An_approval_travels_as_input_required_and_its_answer_continues_the_same_task(string answer)
    {
        var choice = (ApprovalChoice)Enum.Parse(typeof(ApprovalChoice), answer);
        var (server, runner) = Serve();
        runner.Run = async (request, channel, token) =>
        {
            var answer = await channel.RequestApprovalAsync(new ApprovalAsk("ap-1", "add_comment", "write", "Comment on page 2", """{"page":2}"""), token);
            return new TaskOutcome(request.TaskId, TaskState.Completed, "decided " + answer);
        };
        var observer = new Observer { OnApproval = (_, _) => Task.FromResult(choice) };

        var outcome = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), observer, CancellationToken.None);

        Assert.Equal((TaskState.Completed, "decided " + choice), (outcome.State, outcome.Text));
        var ask = Assert.Single(observer.Asked);
        Assert.Equal(("ap-1", "add_comment", "write", "Comment on page 2", """{"page":2}"""),
            (ask.ApprovalId, ask.Tool, ask.ToolClass, ask.Summary, ask.ArgumentsPreview));
        Assert.Contains(observer.Statuses, s => s.StartsWith("input-required"));
    }

    [Fact]
    public async Task Two_approvals_in_one_task_are_both_asked()
    {
        var (server, runner) = Serve();
        runner.Run = async (request, channel, token) =>
        {
            var first = await channel.RequestApprovalAsync(new ApprovalAsk("ap-1", "a", "write", "", ""), token);
            var second = await channel.RequestApprovalAsync(new ApprovalAsk("ap-2", "b", "write", "", ""), token);
            return new TaskOutcome(request.TaskId, TaskState.Completed, first + "," + second);
        };
        var observer = new Observer { OnApproval = (ask, _) => Task.FromResult(ask.ApprovalId == "ap-1" ? ApprovalChoice.Once : ApprovalChoice.Deny) };

        var outcome = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), observer, CancellationToken.None);

        Assert.Equal("Once,Deny", outcome.Text);
    }

    [Fact]
    public async Task Failed_and_rejected_are_A2A_states_with_their_reason()
    {
        var (server, runner) = Serve();
        runner.Run = (request, _, _) => Task.FromResult(new TaskOutcome(request.TaskId, TaskState.Failed, "The server did not allow it."));
        var failed = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), new Observer(), CancellationToken.None);
        runner.Run = (request, _, _) => Task.FromResult(new TaskOutcome(request.TaskId, TaskState.Rejected, "The agent is busy with another task."));
        var rejected = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(), new Observer(), CancellationToken.None);

        Assert.Equal((TaskState.Failed, "The server did not allow it."), (failed.State, failed.Text));
        Assert.Equal((TaskState.Rejected, "The agent is busy with another task."), (rejected.State, rejected.Text));
    }

    [Fact]
    public async Task The_caller_cancels_with_the_A2A_cancel_method_while_an_approval_waits()
    {
        var (server, runner) = Serve();
        var stopped = new TaskCompletionSource<bool>();
        runner.Run = async (request, channel, token) =>
        {
            var answer = await channel.RequestApprovalAsync(new ApprovalAsk("ap-1", "a", "write", "", ""), token);
            stopped.TrySetResult(token.IsCancellationRequested);
            return new TaskOutcome(request.TaskId, TaskState.Completed, "decided " + answer);
        };
        using var cancel = new CancellationTokenSource();
        var asked = new TaskCompletionSource<bool>();
        var observer = new Observer { OnApproval = async (_, token) => { asked.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return ApprovalChoice.Deny; } };

        var running = new PipeDelegationClient().RunAsync(server.Endpoint, Request(), observer, cancel.Token);
        await Within(asked.Task, 10);
        cancel.Cancel();
        var outcome = await Within(running, 15);

        Assert.Equal(TaskState.Canceled, outcome.State);
        Assert.True(await Within(stopped.Task, 10), "the run's token was cancelled");
    }

    [Fact]
    public async Task The_agent_card_is_served_through_the_extended_card_method()
    {
        var (server, _) = Serve();

        var card = await new PipeDelegationClient().GetAgentCardAsync(server.Endpoint, CancellationToken.None);

        Assert.Equal(("Word", "Edits the open document."), (card.Name, card.Description));
        Assert.True(card.Capabilities.Streaming);
        Assert.Equal(("Comments", "Adds comments"), (card.Skills.Single().Name, card.Skills.Single().Description));
        Assert.Equal("JSONRPC", card.SupportedInterfaces.Single().ProtocolBinding);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        var (server, runner) = Serve();

        var outcome = await new PipeDelegationClient().RunAsync(server.Endpoint with { Secret = "wrong" }, Request(), new Observer(), CancellationToken.None);

        Assert.Equal(TaskState.Failed, outcome.State);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused_by_either_side()
    {
        var (server, runner) = Serve(maxBytes: 1024);

        var byServer = await new PipeDelegationClient().RunAsync(server.Endpoint, Request(new string('x', 5000)), new Observer(), CancellationToken.None);
        var byClient = await new PipeDelegationClient(maxMessageBytes: 1024).RunAsync(server.Endpoint, Request(new string('x', 5000)), new Observer(), CancellationToken.None);

        Assert.Equal(TaskState.Failed, byServer.State);
        Assert.Equal(TaskState.Failed, byClient.State);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task The_server_going_away_mid_task_fails_the_task()
    {
        var (server, runner) = Serve();
        runner.Run = async (request, channel, token) =>
        {
            await channel.RequestApprovalAsync(new ApprovalAsk("ap-1", "a", "write", "", ""), token);
            return new TaskOutcome(request.TaskId, TaskState.Completed, "never");
        };
        var observer = new Observer
        {
            OnApproval = async (_, token) => { server.Dispose(); await Task.Delay(Timeout.Infinite, token); return ApprovalChoice.Once; },
        };

        var outcome = await Within(new PipeDelegationClient().RunAsync(server.Endpoint, Request(), observer, CancellationToken.None), 15);

        Assert.Equal(TaskState.Failed, outcome.State);
        Assert.Contains("closed the connection", outcome.Text);
    }

    [Fact]
    public async Task The_registry_finds_the_agent_and_the_local_delegate_runs_the_task()
    {
        var (server, _) = Serve();
        var registry = new DeviceAgentRegistry(_folder);
        using var registration = registry.Register(new AgentPresence { Agent = "word-assistant", Delegation = server.Endpoint });
        var caller = new LocalAgentDelegate(registry, "desktop-assistant", "Desktop agent");

        var result = await caller.RunAsync(new DelegationRequest("word-assistant", "Hi."));
        var missing = await caller.RunAsync(new DelegationRequest("excel-assistant", "Hi."));

        Assert.Equal((DelegationOutcome.Completed, "ok"), (result.Outcome, result.Text));
        Assert.Equal("Word", caller.FindCard("word-assistant")!.Name);
        Assert.Equal(DelegationOutcome.NotRunning, missing.Outcome);
    }

    [Fact]
    public void The_net_framework_dependency_versions_are_reported()
    {
        // Printed for the release notes: the assemblies a host has to bind to.
        foreach (var type in new[] { typeof(System.Text.Json.JsonSerializer), typeof(System.Threading.Channels.Channel), typeof(A2A.A2AServer) })
        {
            Console.WriteLine($"{type.Assembly.GetName().Name} {type.Assembly.GetName().Version} {type.Assembly.Location}");
        }
        Assert.NotNull(typeof(A2A.A2AServer).Assembly.GetName().Version);
    }
}

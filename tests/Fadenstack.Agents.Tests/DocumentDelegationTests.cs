using Fadenstack.Agents.Delegation;
using Fadenstack.Agents.Registry;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Delegation to a document: which instance takes the task, and starting the app when none is open.</summary>
public sealed class DocumentDelegationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-docdeleg-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();
    private readonly DeviceAgentRegistry _registry;
    private readonly Client _client = new();

    public DocumentDelegationTests()
    {
        _registry = new DeviceAgentRegistry(_folder);
    }

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

    /// <summary>Stands in for the pipe: records where each task went and completes it.</summary>
    private sealed class Client : IDelegationClient
    {
        public List<(string Pipe, TaskRequest Request)> Sent { get; } = new();

        public Task<TaskOutcome> RunAsync(AgentDelegationEndpoint endpoint, TaskRequest request, ITaskObserver observer, CancellationToken cancellationToken)
        {
            Sent.Add((endpoint.Pipe, request));
            return Task.FromResult(new TaskOutcome(request.TaskId, TaskState.Completed, "done"));
        }
    }

    private sealed class Launcher : IAgentLauncher
    {
        public bool Can { get; set; } = true;
        public AgentLaunch Launch { get; set; } = new(true, null);
        public Action? OnLaunch { get; set; }
        public List<(string Agent, string? Document)> Launched { get; } = new();

        public bool CanLaunch(string agent, string? document) => Can;

        public Task<AgentLaunch> LaunchAsync(string agent, string? document, CancellationToken cancellationToken)
        {
            Launched.Add((agent, document));
            OnLaunch?.Invoke();
            return Task.FromResult(Launch);
        }
    }

    private AgentRegistration Register(string pipe, params string[] workspaces)
    {
        var registration = _registry.Register(new AgentPresence
        {
            Agent = "word-assistant",
            Instance = pipe,
            Workspaces = workspaces,
            Delegation = new AgentDelegationEndpoint { Pipe = pipe, Secret = "s", Card = new AgentCardSummary { Slug = "word-assistant", Name = "Word" } },
        });
        _disposables.Add(registration);
        return registration;
    }

    private LocalAgentDelegate Caller(Launcher? launcher = null, TimeSpan? timeout = null) => new(_registry, "desktop-assistant", "Desktop agent",
        new Dictionary<string, string> { ["word-assistant"] = "Word" }, _client, new LocalAgentDelegateOptions
        {
            Launcher = launcher, ReadyTimeout = timeout ?? TimeSpan.FromSeconds(10), PollInterval = TimeSpan.FromMilliseconds(10),
        });

    private static DelegationRequest Ask(string? document) => new("word-assistant", "Comment.") { Document = document };

    [Fact]
    public async Task A_task_for_a_document_goes_to_the_instance_that_works_in_it()
    {
        var other = Register("pipe-other", @"C:\Docs\other.docx");
        Register("pipe-report", @"C:\Docs\report.docx");
        other.Update(AgentStatus.Idle); // the newest, but not the one

        var result = await Caller().RunAsync(Ask(@"c:\docs\.\report.docx"));

        Assert.Equal(DelegationOutcome.Completed, result.Outcome);
        var sent = Assert.Single(_client.Sent);
        Assert.Equal("pipe-report", sent.Pipe);
        Assert.Equal(@"c:\docs\.\report.docx", sent.Request.Document);
    }

    [Fact]
    public async Task Without_a_document_the_newest_instance_takes_the_task_as_before()
    {
        Register("pipe-first", "A");
        Register("pipe-second", "B");

        await Caller().RunAsync(Ask(null));

        Assert.Equal("pipe-second", Assert.Single(_client.Sent).Pipe);
        Assert.Null(_client.Sent[0].Request.Document);
    }

    [Fact]
    public void An_unsaved_documents_name_is_compared_as_a_name()
    {
        Assert.True(DocumentPaths.Same("Document1", "document1"));
        Assert.False(DocumentPaths.Same("Document1", "Document2"));
        Assert.False(DocumentPaths.Same("Document1", @"C:\Docs\Document1"));
        Assert.False(DocumentPaths.Same("", ""));
    }

    [Fact]
    public async Task A_document_nobody_has_open_and_no_launcher_names_the_document_in_the_answer()
    {
        Register("pipe-other", @"C:\Docs\other.docx");

        var result = await Caller().RunAsync(Ask(@"C:\Docs\report.docx"));

        Assert.Equal(DelegationOutcome.NotRunning, result.Outcome);
        Assert.Contains(@"C:\Docs\report.docx", result.Text);
        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task A_launcher_starts_the_app_and_the_task_runs_when_the_agent_appears()
    {
        var statuses = new List<DelegationStatus>();
        var launcher = new Launcher { OnLaunch = () => _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Register("pipe-new", @"C:\Docs\report.docx");
        }) };

        var result = await Caller(launcher).RunAsync(Ask(@"C:\Docs\report.docx"), new DelegationCallbacks { Status = statuses.Add });

        Assert.Equal(DelegationOutcome.Completed, result.Outcome);
        Assert.Equal((@"word-assistant", @"C:\Docs\report.docx"), Assert.Single(launcher.Launched));
        var launching = Assert.Single(statuses, s => s.State == "launching");
        Assert.Equal((result.TaskId, "word-assistant", @"C:\Docs\report.docx"), (launching.TaskId, launching.Agent, launching.Text));
        Assert.Equal("pipe-new", Assert.Single(_client.Sent).Pipe);
    }

    [Fact]
    public async Task A_launch_that_did_not_start_answers_with_the_launchers_problem()
    {
        var launcher = new Launcher { Launch = new AgentLaunch(false, "Word is not installed.") };

        var result = await Caller(launcher).RunAsync(Ask(@"C:\Docs\report.docx"));

        Assert.Equal((DelegationOutcome.NotRunning, "Word is not installed."), (result.Outcome, result.Text));
        Assert.Empty(_client.Sent);
    }

    [Fact]
    public async Task An_agent_that_does_not_get_ready_in_time_is_not_running()
    {
        var result = await Caller(new Launcher(), TimeSpan.FromMilliseconds(100)).RunAsync(Ask(@"C:\Docs\report.docx"));

        Assert.Equal((DelegationOutcome.NotRunning, "Word did not get ready in time."), (result.Outcome, result.Text));
    }

    [Fact]
    public async Task Cancelling_while_the_app_starts_cancels_the_delegation()
    {
        using var cancel = new CancellationTokenSource();
        var running = Caller(new Launcher()).RunAsync(Ask(@"C:\Docs\report.docx"), null, cancel.Token);
        await Task.Delay(100);

        cancel.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DelegationOutcome.Canceled, result.Outcome);
    }

    [Fact]
    public async Task A_launcher_that_cannot_start_the_agent_is_not_asked()
    {
        var launcher = new Launcher { Can = false };

        var result = await Caller(launcher).RunAsync(Ask(null));

        Assert.Equal(DelegationOutcome.NotRunning, result.Outcome);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task A_running_agent_is_not_launched_again()
    {
        Register("pipe-report", @"C:\Docs\report.docx");
        var launcher = new Launcher();

        await Caller(launcher).RunAsync(Ask(@"C:\Docs\report.docx"));

        Assert.Empty(launcher.Launched);
        Assert.Single(_client.Sent);
    }
}

using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Registry;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Tools;

namespace Fadenstack.Agents.Delegation;

/// <summary>What a <see cref="DelegationHost"/> says about its agent and how it limits delegated tasks.</summary>
public sealed class DelegationHostOptions
{
    /// <summary>The name callers show for the agent (<c>Word</c>); default: the agent's slug.</summary>
    public string? Name { get; set; }

    /// <summary>One line on what the agent is for, in the Agent Card.</summary>
    public string Description { get; set; } = "";

    /// <summary>What the agent can do, as short lines (name and description) a calling model chooses by.</summary>
    public IReadOnlyList<AgentCardSkill> Skills { get; set; } = Array.Empty<AgentCardSkill>();

    /// <summary>The instance it works in (<c>InstanceKey</c> text) for the registry; empty for none.</summary>
    public string Instance { get; set; } = "";

    /// <summary>The host's window key, for hosts with an agent per window.</summary>
    public string? Window { get; set; }

    /// <summary>The largest message on the pipe in bytes (default 256 KB).</summary>
    public int MaxMessageBytes { get; set; } = 256 * 1024;

    /// <summary>How long a task may run before it fails (default 30 minutes).</summary>
    public TimeSpan TaskTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the caller's person has to decide an approval before it is denied (default 5 minutes).</summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The tool mode of a delegated session; null: the agent's default.</summary>
    public ToolMode? ToolMode { get; set; }

    /// <summary>The longest answer handed back in characters (default 40 000); a longer one is cut and says so.</summary>
    public int MaxAnswerCharacters { get; set; } = 40_000;
}

/// <summary>What happened to a delegated task, for the host application's UI.</summary>
public enum DelegatedTaskChange
{
    /// <summary>The task started: its session exists (<see cref="DelegatedTask.Session"/>).</summary>
    Started,

    /// <summary>Progress: <see cref="DelegatedTask.StatusText"/> changed.</summary>
    Status,

    /// <summary>The task waits for the caller's person to decide an approval (<see cref="DelegatedTask.State"/> is input-required).</summary>
    ApprovalAsked,

    /// <summary>The task ended (<see cref="DelegatedTask.State"/> is completed, failed, canceled or rejected).</summary>
    Finished,
}

/// <summary>A task another agent delegated to this one, as the host application shows it.</summary>
public sealed class DelegatedTask
{
    internal DelegatedTask(string taskId, string fromSlug, string fromName)
    {
        TaskId = taskId;
        FromSlug = fromSlug;
        FromName = fromName;
        StartedUtc = DateTime.UtcNow;
    }

    public string TaskId { get; }

    /// <summary>The calling agent's slug and name.</summary>
    public string FromSlug { get; }

    public string FromName { get; }

    /// <summary>What to call it in the UI: <c>Task from Desktop agent</c>.</summary>
    public string Label => string.IsNullOrWhiteSpace(FromName) ? "Task from another agent" : "Task from " + FromName;

    public DateTime StartedUtc { get; }

    /// <summary>The task's state as A2A writes it: submitted, working, input-required, completed, failed, canceled, rejected.</summary>
    public string State { get; internal set; } = TaskState.Submitted.ToWire();

    /// <summary>The latest line of progress (a tool's name), or the reason a task did not complete.</summary>
    public string? StatusText { get; internal set; }

    /// <summary>The agent's session running the task; null before it starts and for a rejected task.</summary>
    public AgentSession? Session { get; internal set; }
}

/// <summary>Arguments of <see cref="DelegationHost.TaskChanged"/>.</summary>
public sealed class DelegatedTaskEventArgs : EventArgs
{
    internal DelegatedTaskEventArgs(DelegatedTaskChange change, DelegatedTask task)
    {
        Change = change;
        Task = task;
    }

    public DelegatedTaskChange Change { get; }

    public DelegatedTask Task { get; }
}

/// <summary>
/// Lets this process's agent take tasks from other agents on the device. An add-in starts it once its
/// <see cref="FadenstackAgent"/> is ready: it registers the agent with its card in the <see cref="DeviceAgentRegistry"/>,
/// listens on a local pipe (current OS user only, a secret per launch) and runs each delegated task as a new session of
/// the agent -- with its own server agent, contract, tool modes and approvals. The approvals the task's loop needs are
/// forwarded to the caller, whose person decides (a denial when nobody answers in time). One task runs at a time; another
/// is rejected as busy. The task's answer goes back to the caller; nothing else of the session does.
/// </summary>
/// <example>
/// <code>
/// var host = new DelegationHost(agent, new DeviceAgentRegistry(), new DelegationHostOptions { Name = "Word", Description = "Edits the open document." });
/// host.TaskChanged += (_, e) => pane.Show(e.Task.Label, e.Task.StatusText);
/// host.Start();
/// </code>
/// </example>
public sealed class DelegationHost : IDisposable
{
    private readonly FadenstackAgent _agent;
    private readonly DeviceAgentRegistry _registry;
    private readonly DelegationHostOptions _options;
    private readonly IDelegationServer _server;
    private readonly object _gate = new();
    private AgentRegistration? _registration;
    private DelegatedTask? _current;

    /// <param name="agent">A started agent (<see cref="FadenstackAgent.StartAsync"/>, or after its first session).</param>
    /// <param name="registry">The device's agent registry.</param>
    /// <param name="options">The card and the limits; defaults when null.</param>
    public DelegationHost(FadenstackAgent agent, DeviceAgentRegistry registry, DelegationHostOptions? options = null)
        : this(agent, registry, options, null)
    {
    }

    internal DelegationHost(FadenstackAgent agent, DeviceAgentRegistry registry, DelegationHostOptions? options, IDelegationServer? server)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new DelegationHostOptions();
        _server = server ?? new PipeDelegationServer(new AgentCardSummary
        {
            Slug = agent.Slug,
            Name = string.IsNullOrWhiteSpace(_options.Name) ? agent.Slug : _options.Name!,
            Description = _options.Description,
            Skills = _options.Skills,
        }, _options.MaxMessageBytes);
    }

    /// <summary>Where and how callers reach this agent (also in the registry).</summary>
    public AgentDelegationEndpoint Endpoint => _server.Endpoint;

    /// <summary>The task running now; null when none.</summary>
    public DelegatedTask? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// A task started, made progress, waits for its caller's person, or ended. Raised on a background thread: marshal to
    /// the UI thread. A handler that throws does not affect the task.
    /// </summary>
    public event EventHandler<DelegatedTaskEventArgs>? TaskChanged;

    /// <summary>Registers the agent and starts listening. Dispose to withdraw it.</summary>
    public void Start()
    {
        if (_registration is not null)
        {
            throw new InvalidOperationException("The host is already started.");
        }
        _server.Start(new Runner(this));
        _registration = _registry.Register(new AgentPresence
        {
            Agent = _agent.Slug,
            Instance = _options.Instance,
            Window = _options.Window,
            Delegation = _server.Endpoint,
        });
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _server.Dispose();
    }

    private void Raise(DelegatedTaskChange change, DelegatedTask task)
    {
        try
        {
            TaskChanged?.Invoke(this, new DelegatedTaskEventArgs(change, task));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the UI's trouble, not the task's
        }
    }

    private sealed class Runner : IDelegatedTaskRunner
    {
        private readonly DelegationHost _host;

        public Runner(DelegationHost host) => _host = host;

        public async Task<TaskOutcome> RunAsync(TaskRequest request, IDelegatedTaskChannel channel, CancellationToken cancellationToken)
        {
            var host = _host;
            var task = new DelegatedTask(request.TaskId, request.FromSlug, request.FromName);
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return new TaskOutcome(request.TaskId, TaskState.Rejected, "The task has no text.");
            }
            lock (host._gate)
            {
                if (host._current is not null)
                {
                    return new TaskOutcome(request.TaskId, TaskState.Rejected, "The agent is busy with another task.");
                }
                host._current = task;
            }
            host._registration?.Update(AgentStatus.Busy);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(host._options.TaskTimeout);
            AgentSession? session = null;
            try
            {
                session = await host._agent.StartSessionAsync(host._options.ToolMode, limit.Token).ConfigureAwait(false);
                session.Approvals = new ForwardedApprovals(host, task, channel);
                task.Session = session;
                task.State = TaskState.Working.ToWire();
                host.Raise(DelegatedTaskChange.Started, task);
                await channel.StatusAsync(TaskState.Working, null).ConfigureAwait(false);
                AgentResult? result = null;
                await foreach (var e in session.StreamAsync(MessageFor(request), limit.Token).ConfigureAwait(false))
                {
                    if (e is ToolCallStarted started)
                    {
                        task.StatusText = "Using " + started.Name;
                        host.Raise(DelegatedTaskChange.Status, task);
                        await channel.StatusAsync(TaskState.Working, task.StatusText).ConfigureAwait(false);
                    }
                    else if (e is TurnCompleted completed)
                    {
                        result = completed.Result;
                    }
                }
                return Finish(task, Outcome(request.TaskId, result));
            }
            catch (Exception e) when (e is OperationCanceledException || (limit.IsCancellationRequested && e is not OutOfMemoryException))
            {
                // a cancelled task can surface as other exceptions too (a response stream closed under the loop)
                var timedOut = limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                return Finish(task, timedOut
                    ? new TaskOutcome(request.TaskId, TaskState.Failed, "The task took too long.")
                    : new TaskOutcome(request.TaskId, TaskState.Canceled, "The task was canceled."));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Finish(task, new TaskOutcome(request.TaskId, TaskState.Failed, "The agent could not run the task: " + e.GetType().Name + "."));
            }
            finally
            {
                if (session is not null)
                {
                    try
                    {
                        await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // reporting the session's counts is best effort
                    }
                }
                lock (host._gate)
                {
                    host._current = null;
                }
                host._registration?.Update(AgentStatus.Idle);
            }
        }

        private TaskOutcome Finish(DelegatedTask task, TaskOutcome outcome)
        {
            task.State = outcome.State.ToWire();
            task.StatusText = outcome.State == TaskState.Completed ? task.StatusText : outcome.Text;
            _host.Raise(DelegatedTaskChange.Finished, task);
            return outcome;
        }

        private TaskOutcome Outcome(string taskId, AgentResult? result)
        {
            if (result is null)
            {
                return new TaskOutcome(taskId, TaskState.Failed, "The agent gave no answer.");
            }
            if (result.Outcome is ChatOutcome.PromptBlocked or ChatOutcome.ResponseBlocked or ChatOutcome.ApprovalRequired)
            {
                return new TaskOutcome(taskId, TaskState.Failed, result.Notice?.Message is { Length: > 0 } notice
                    ? "The server did not allow it: " + notice
                    : "The server did not allow it (" + result.Outcome + ").");
            }
            var text = result.Text ?? "";
            var max = _host._options.MaxAnswerCharacters;
            if (text.Length > max)
            {
                text = text.Substring(0, max) + "\n(The answer was cut: it was longer than " + max + " characters.)";
            }
            return new TaskOutcome(taskId, TaskState.Completed, text);
        }

        /// <summary>The task as the session's first message: it is labelled, so the session list shows where it came from.</summary>
        private static string MessageFor(TaskRequest request)
        {
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.FromName))
            {
                text.Append("[Task from ").Append(request.FromName.Replace('\n', ' ').Replace('\r', ' ')).Append("]\n");
            }
            text.Append(request.Text);
            if (request.References.Count > 0)
            {
                text.Append("\n\nReferences:");
                foreach (var reference in request.References)
                {
                    text.Append("\n- ").Append(reference);
                }
            }
            return text.ToString();
        }
    }

    /// <summary>The delegated session's approvals: each goes to the caller; no answer in time is a denial.</summary>
    private sealed class ForwardedApprovals : IApprovalHandler
    {
        private const int PreviewLimit = 1000;
        private const int SummaryLimit = 300;
        private readonly DelegationHost _host;
        private readonly DelegatedTask _task;
        private readonly IDelegatedTaskChannel _channel;

        public ForwardedApprovals(DelegationHost host, DelegatedTask task, IDelegatedTaskChannel channel)
        {
            _host = host;
            _task = task;
            _channel = channel;
        }

        public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            var summary = request.Target ?? request.Reason;
            var ask = new ApprovalAsk(Guid.NewGuid().ToString("N"), request.ToolName, request.ToolClass.ToString().ToLowerInvariant(),
                Cut(summary, SummaryLimit), Cut(Preview(request.Arguments), PreviewLimit));
            _task.State = TaskState.InputRequired.ToWire();
            _task.StatusText = "Waiting for approval: " + request.ToolName;
            _host.Raise(DelegatedTaskChange.ApprovalAsked, _task);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_host._options.ApprovalTimeout);
            ApprovalChoice choice;
            try
            {
                choice = await _channel.RequestApprovalAsync(ask, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                choice = ApprovalChoice.Deny;
            }
            _task.State = TaskState.Working.ToWire();
            return choice switch
            {
                ApprovalChoice.Once => ApprovalDecision.Approve(),
                ApprovalChoice.Chat => ApprovalDecision.ApproveForSession(),
                _ => ApprovalDecision.Deny("The calling agent's person did not approve it."),
            };
        }

        private static string Preview(IReadOnlyDictionary<string, object?> arguments)
        {
            try
            {
                return JsonSerializer.Serialize(arguments);
            }
            catch (Exception e) when (e is NotSupportedException or JsonException or InvalidOperationException)
            {
                return "";
            }
        }

        private static string Cut(string? text, int limit) =>
            text is null ? "" : text.Length <= limit ? text : text.Substring(0, limit) + "...";
    }
}

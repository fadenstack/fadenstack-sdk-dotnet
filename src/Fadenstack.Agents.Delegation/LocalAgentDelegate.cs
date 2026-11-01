using System.Diagnostics;
using Fadenstack.Agents.Registry;
using Fadenstack.Tools;

namespace Fadenstack.Agents.Delegation;

/// <summary>
/// Delegation to agents on this device: finds the agent in the <see cref="DeviceAgentRegistry"/> (an add-in's
/// <see cref="DelegationHost"/>), connects on its local pipe with the secret of its launch, sends the task and relays
/// progress and approvals. A task for a document goes to the instance that works in it; when none is open and an
/// <see cref="IAgentLauncher"/> can start one, it does, and the task waits for it. A task without a document goes to
/// the agent's only document; with several documents open (two Word windows) it is refused with their list, so the
/// caller names one instead of the task landing in a guessed one.
/// </summary>
public sealed class LocalAgentDelegate : IAgentDelegate
{
    private readonly DeviceAgentRegistry _registry;
    private readonly IDelegationClient _client;
    private readonly string _fromSlug;
    private readonly string _fromName;
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly LocalAgentDelegateOptions _options;

    /// <param name="registry">The device's agent registry.</param>
    /// <param name="fromSlug">The calling agent's slug.</param>
    /// <param name="fromName">The calling agent's name, which the other agent shows (<c>Task from Desktop agent</c>).</param>
    /// <param name="names">Display names by agent slug, for the answer about an agent that is not running.</param>
    public LocalAgentDelegate(DeviceAgentRegistry registry, string fromSlug, string fromName,
        IReadOnlyDictionary<string, string>? names = null)
        : this(registry, fromSlug, fromName, names, new PipeDelegationClient(), null)
    {
    }

    /// <param name="registry">The device's agent registry.</param>
    /// <param name="fromSlug">The calling agent's slug.</param>
    /// <param name="fromName">The calling agent's name.</param>
    /// <param name="names">Display names by agent slug.</param>
    /// <param name="options">The launcher and its waiting times.</param>
    public LocalAgentDelegate(DeviceAgentRegistry registry, string fromSlug, string fromName,
        IReadOnlyDictionary<string, string>? names, LocalAgentDelegateOptions? options)
        : this(registry, fromSlug, fromName, names, new PipeDelegationClient(), options)
    {
    }

    internal LocalAgentDelegate(DeviceAgentRegistry registry, string fromSlug, string fromName,
        IReadOnlyDictionary<string, string>? names, IDelegationClient client, LocalAgentDelegateOptions? options = null)
    {
        _options = options ?? new LocalAgentDelegateOptions();
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fromSlug = fromSlug ?? "";
        _fromName = fromName ?? "";
        _names = names ?? new Dictionary<string, string>();
        _client = client;
    }

    public AgentCardSummary? FindCard(string agent) => Find(agent, null)?.Delegation?.Card;

    public async Task<DelegationResult> RunAsync(DelegationRequest request, DelegationCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var watch = Stopwatch.StartNew();
        var taskId = Guid.NewGuid().ToString("N");
        var document = string.IsNullOrWhiteSpace(request.Document) ? null : request.Document;
        if (document is null && OpenDocuments(request.Agent) is { Count: > 1 } open)
        {
            // Two Word windows, "my document": which one is not ours to guess, a wrong guess edits the wrong file. The
            // caller's person chooses; a caller that cannot ask gets the list and names one.
            if (callbacks?.Choose is not { } choose)
            {
                return new DelegationResult(DelegationOutcome.Rejected,
                    DisplayName(request.Agent) + " has several documents open: " + string.Join("; ", open)
                    + ". Call delegate again with document set to the one the person means; if that is not clear, ask them.",
                    taskId, watch.Elapsed);
            }
            string? chosen;
            try
            {
                chosen = await choose(new DelegationChoiceRequest(taskId, request.Agent, DisplayName(request.Agent), open), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                chosen = null;
            }
            document = open.FirstOrDefault(o => DocumentPaths.Same(o, chosen));
            if (document is null)
            {
                return new DelegationResult(DelegationOutcome.Canceled, "The person chose no document, so nothing was handed over.",
                    taskId, watch.Elapsed);
            }
        }
        var endpoint = Find(request.Agent, document)?.Delegation;
        if (endpoint is null && _options.Launcher is { } launcher && launcher.CanLaunch(request.Agent, document))
        {
            try
            {
                endpoint = await LaunchAsync(launcher, request.Agent, document, taskId, callbacks, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DelegationResult(DelegationOutcome.Canceled, "The task was canceled.", taskId, watch.Elapsed);
            }
            catch (LaunchFailedException e)
            {
                return new DelegationResult(DelegationOutcome.NotRunning, e.Message, taskId, watch.Elapsed);
            }
        }
        if (endpoint is null)
        {
            return NotRunning(request.Agent, document, taskId, watch);
        }
        var name = endpoint.Card.Name.Length > 0 ? endpoint.Card.Name : request.Agent;
        var observer = new Observer(taskId, request.Agent, name, callbacks);
        try
        {
            var outcome = await _client.RunAsync(endpoint,
                new TaskRequest(taskId, request.Task, request.References ?? Array.Empty<string>(), _fromSlug, _fromName) { Document = document },
                observer, cancellationToken).ConfigureAwait(false);
            return new DelegationResult(outcome.State switch
            {
                TaskState.Completed => DelegationOutcome.Completed,
                TaskState.Canceled => DelegationOutcome.Canceled,
                TaskState.Rejected => DelegationOutcome.Rejected,
                _ => DelegationOutcome.Failed,
            }, outcome.Text ?? "", taskId, watch.Elapsed);
        }
        catch (AgentUnreachableException)
        {
            return NotRunning(request.Agent, document, taskId, watch);
        }
    }

    /// <summary>Starts the app and waits for the agent to register; throws <see cref="LaunchFailedException"/> when it cannot be had.</summary>
    private async Task<AgentDelegationEndpoint> LaunchAsync(IAgentLauncher launcher, string agent, string? document, string taskId,
        DelegationCallbacks? callbacks, CancellationToken cancellationToken)
    {
        try
        {
            callbacks?.Status?.Invoke(new DelegationStatus(taskId, agent, "launching", document));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the caller's UI does not stop the task
        }
        var launch = await launcher.LaunchAsync(agent, document, cancellationToken).ConfigureAwait(false);
        if (launch is null || !launch.Started)
        {
            throw new LaunchFailedException(launch?.Problem is { Length: > 0 } problem ? problem : DisplayName(agent) + " could not be started.");
        }
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (Find(agent, document)?.Delegation is { } endpoint)
            {
                return endpoint;
            }
            if (wait.Elapsed >= _options.ReadyTimeout)
            {
                throw new LaunchFailedException(DisplayName(agent) + " did not get ready in time.");
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private string DisplayName(string agent) => _names.TryGetValue(agent, out var name) ? name : agent;

    private DelegationResult NotRunning(string agent, string? document, string taskId, Stopwatch watch) =>
        new(DelegationOutcome.NotRunning,
            document is null
                ? DisplayName(agent) + " is not open with the Fadenstack add-in: ask the person to open it."
                : DisplayName(agent) + " is not open with the Fadenstack add-in in " + document + ": ask the person to open that document.",
            taskId, watch.Elapsed);

    private sealed class LaunchFailedException : Exception
    {
        public LaunchFailedException(string message) : base(message)
        {
        }
    }

    private AgentPresence? Find(string agent, string? document)
    {
        var running = Running(agent)
            .Where(p => document is null || p.Workspaces.Any(w => DocumentPaths.Same(w, document)))
            .ToList();
        // The list is oldest heartbeat first: the newest idle one, else the newest.
        return running.LastOrDefault(p => p.Status == AgentStatus.Idle) ?? running.LastOrDefault();
    }

    private IEnumerable<AgentPresence> Running(string agent) => _registry.List()
        .Where(p => p.Delegation is not null && (p.Agent == agent || p.Delegation.Card.Slug == agent));

    /// <summary>The different documents the agent's instances work in (instances without one do not count).</summary>
    private List<string> OpenDocuments(string agent)
    {
        var open = new List<string>();
        foreach (var workspace in Running(agent).SelectMany(p => p.Workspaces))
        {
            if (!string.IsNullOrWhiteSpace(workspace) && !open.Any(o => DocumentPaths.Same(o, workspace)))
            {
                open.Add(workspace);
            }
        }
        return open;
    }

    private sealed class Observer : ITaskObserver
    {
        private readonly string _taskId, _agent, _agentName;
        private readonly DelegationCallbacks? _callbacks;
        private readonly HashSet<string> _approvedTools = new();

        public Observer(string taskId, string agent, string agentName, DelegationCallbacks? callbacks)
        {
            (_taskId, _agent, _agentName, _callbacks) = (taskId, agent, agentName, callbacks);
        }

        public void Status(TaskState state, string? text)
        {
            try
            {
                _callbacks?.Status?.Invoke(new DelegationStatus(_taskId, _agent, state.ToWire(), text));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the caller's UI does not stop the task
            }
        }

        public async Task<ApprovalChoice> ApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        {
            if (_callbacks?.Approve is not { } approve)
            {
                return ApprovalChoice.Deny;
            }
            lock (_approvedTools)
            {
                if (_approvedTools.Contains(ask.Tool))
                {
                    return ApprovalChoice.Once; // the person allowed this tool for the rest of the task
                }
            }
            var decision = await approve(new DelegationApprovalRequest(_taskId, ask.ApprovalId, _agent, _agentName, ask.Tool,
                ParseClass(ask.ToolClass), ask.Summary, ask.ArgumentsPreview), cancellationToken).ConfigureAwait(false);
            if (decision is not { Approved: true })
            {
                return ApprovalChoice.Deny;
            }
            if (decision.ForSession)
            {
                lock (_approvedTools)
                {
                    _approvedTools.Add(ask.Tool);
                }
                return ApprovalChoice.Chat;
            }
            return ApprovalChoice.Once;
        }

        private static ToolClass ParseClass(string wire) => wire switch
        {
            "read" => ToolClass.Read,
            "write" => ToolClass.Write,
            "destructive" => ToolClass.Destructive,
            _ => ToolClass.Unknown,
        };
    }
}

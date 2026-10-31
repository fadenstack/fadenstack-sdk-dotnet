using System.Diagnostics;
using Fadenstack.Agents.Registry;
using Fadenstack.Tools;

namespace Fadenstack.Agents.Delegation;

/// <summary>A task for another agent: who, what, and the short references the caller chooses to share.</summary>
public sealed record DelegationRequest(string Agent, string Task)
{
    /// <summary>Short strings that point at things the other agent can look up itself: record ids, file paths.</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}

/// <summary>How a delegation ended.</summary>
public enum DelegationOutcome
{
    /// <summary>The agent answered; <see cref="DelegationResult.Text"/> is its answer.</summary>
    Completed,

    /// <summary>The task failed (the agent went away, was refused by the server, ran out of time); the text says why.</summary>
    Failed,

    /// <summary>The caller cancelled it.</summary>
    Canceled,

    /// <summary>The agent would not take it (busy, or the task had no text); the text says why.</summary>
    Rejected,

    /// <summary>The agent is not running with the delegation host; nothing was sent.</summary>
    NotRunning,
}

/// <summary>What came back from a delegation. <see cref="Text"/> is the answer, or the reason it did not complete.</summary>
public sealed record DelegationResult(DelegationOutcome Outcome, string Text, string TaskId, TimeSpan Duration);

/// <summary>Progress of a delegated task, for the caller's UI.</summary>
public sealed record DelegationStatus(string TaskId, string Agent, string State, string? Text);

/// <summary>An approval the other agent's loop needs, for the caller's person to decide.</summary>
public sealed record DelegationApprovalRequest(string TaskId, string ApprovalId, string Agent, string AgentName, string Tool,
    ToolClass ToolClass, string Summary, string ArgumentsPreview);

/// <summary>What the caller supplies for a delegation: where its person answers, and where progress shows.</summary>
public sealed class DelegationCallbacks
{
    /// <summary>
    /// Asks the caller's person (the desktop routes it to its own approval card); returns their decision. Without it every
    /// approval is denied. <see cref="ApprovalDecision.ForSession"/> approves the tool for the rest of that task.
    /// </summary>
    public Func<DelegationApprovalRequest, CancellationToken, Task<ApprovalDecision>>? Approve { get; set; }

    /// <summary>Progress lines (<c>working</c>, a tool's name); called on a background thread.</summary>
    public Action<DelegationStatus>? Status { get; set; }
}

/// <summary>"Run this task with that agent" (design 01 section 8): the only thing a caller needs to know about another agent.</summary>
public interface IAgentDelegate
{
    /// <summary>
    /// Hands the task to the agent and returns when it ends. A cancelled token cancels the task and returns
    /// <see cref="DelegationOutcome.Canceled"/>. Never throws for the other agent's trouble: an agent that is not
    /// running, busy, gone or refused gives a result that says so.
    /// </summary>
    Task<DelegationResult> RunAsync(DelegationRequest request, DelegationCallbacks? callbacks = null, CancellationToken cancellationToken = default);

    /// <summary>The card of the agent if it is running and takes delegation now; null otherwise.</summary>
    AgentCardSummary? FindCard(string agent);
}

/// <summary>
/// Delegation to agents on this device: finds the agent in the <see cref="DeviceAgentRegistry"/> (an add-in's
/// <see cref="DelegationHost"/>), connects on its local pipe with the secret of its launch, sends the task and relays
/// progress and approvals. With several instances of the agent open (two Word windows) it picks an idle one, the most
/// recently active first.
/// </summary>
public sealed class LocalAgentDelegate : IAgentDelegate
{
    private readonly DeviceAgentRegistry _registry;
    private readonly IDelegationClient _client;
    private readonly string _fromSlug;
    private readonly string _fromName;
    private readonly IReadOnlyDictionary<string, string> _names;

    /// <param name="registry">The device's agent registry.</param>
    /// <param name="fromSlug">The calling agent's slug.</param>
    /// <param name="fromName">The calling agent's name, which the other agent shows (<c>Task from Desktop agent</c>).</param>
    /// <param name="names">Display names by agent slug, for the answer about an agent that is not running.</param>
    public LocalAgentDelegate(DeviceAgentRegistry registry, string fromSlug, string fromName,
        IReadOnlyDictionary<string, string>? names = null)
        : this(registry, fromSlug, fromName, names, new PipeDelegationClient())
    {
    }

    internal LocalAgentDelegate(DeviceAgentRegistry registry, string fromSlug, string fromName,
        IReadOnlyDictionary<string, string>? names, IDelegationClient client)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fromSlug = fromSlug ?? "";
        _fromName = fromName ?? "";
        _names = names ?? new Dictionary<string, string>();
        _client = client;
    }

    public AgentCardSummary? FindCard(string agent) => Find(agent)?.Delegation?.Card;

    public async Task<DelegationResult> RunAsync(DelegationRequest request, DelegationCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var watch = Stopwatch.StartNew();
        var taskId = Guid.NewGuid().ToString("N");
        var endpoint = Find(request.Agent)?.Delegation;
        if (endpoint is null)
        {
            return NotRunning(request.Agent, taskId, watch);
        }
        var name = endpoint.Card.Name.Length > 0 ? endpoint.Card.Name : request.Agent;
        var observer = new Observer(taskId, request.Agent, name, callbacks);
        try
        {
            var outcome = await _client.RunAsync(endpoint,
                new TaskRequest(taskId, request.Task, request.References ?? Array.Empty<string>(), _fromSlug, _fromName),
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
            return NotRunning(request.Agent, taskId, watch);
        }
    }

    private DelegationResult NotRunning(string agent, string taskId, Stopwatch watch) =>
        new(DelegationOutcome.NotRunning,
            (_names.TryGetValue(agent, out var name) ? name : agent) + " is not open with the Fadenstack add-in: ask the person to open it.",
            taskId, watch.Elapsed);

    private AgentPresence? Find(string agent)
    {
        var running = _registry.List()
            .Where(p => p.Delegation is not null && (p.Agent == agent || p.Delegation.Card.Slug == agent))
            .ToList();
        // The list is oldest heartbeat first: the newest idle one, else the newest.
        return running.LastOrDefault(p => p.Status == AgentStatus.Idle) ?? running.LastOrDefault();
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

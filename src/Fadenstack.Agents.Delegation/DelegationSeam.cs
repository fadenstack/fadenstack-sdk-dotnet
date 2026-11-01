using Fadenstack.Agents.Registry;

namespace Fadenstack.Agents.Delegation;

// The seam between delegation and how a task travels. Everything above it (DelegationHost, LocalAgentDelegate, the
// delegate tool) speaks only these internal types; everything below it (today a local pipe with newline-delimited JSON,
// PipeDelegationServer / PipeDelegationClient / PipeWire) turns them into messages. The task model follows A2A 1.0's
// names so that the A2A library's types and its JSON-RPC binding can replace the two transport classes later without
// touching the host, the tool or their tests.

/// <summary>Where a task is, in A2A 1.0's names.</summary>
internal enum TaskState
{
    Submitted,
    Working,
    InputRequired,
    Completed,
    Failed,
    Canceled,
    Rejected,
}

internal static class TaskStates
{
    /// <summary>A task in one of these never changes again.</summary>
    public static bool IsFinal(this TaskState state) =>
        state is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected;

    /// <summary>The state as A2A writes it on the wire.</summary>
    public static string ToWire(this TaskState state) => state switch
    {
        TaskState.Submitted => "submitted",
        TaskState.Working => "working",
        TaskState.InputRequired => "input-required",
        TaskState.Completed => "completed",
        TaskState.Failed => "failed",
        TaskState.Canceled => "canceled",
        _ => "rejected",
    };

    public static bool TryParse(string? wire, out TaskState state)
    {
        foreach (TaskState candidate in Enum.GetValues(typeof(TaskState)))
        {
            if (candidate.ToWire() == wire)
            {
                state = candidate;
                return true;
            }
        }
        state = default;
        return false;
    }
}

/// <summary>A task a caller hands over: its text, the references it chose to share, and who it is from.</summary>
internal sealed record TaskRequest(string TaskId, string Text, IReadOnlyList<string> References, string FromSlug, string FromName)
{
    /// <summary>The document the task is about (see <see cref="DelegationRequest.Document"/>); null for none.</summary>
    public string? Document { get; init; }
}

/// <summary>An approval the embedded agent's loop needs, for the caller's person to decide.</summary>
internal sealed record ApprovalAsk(string ApprovalId, string Tool, string ToolClass, string Summary, string ArgumentsPreview);

/// <summary>The caller's answer to an <see cref="ApprovalAsk"/>.</summary>
internal enum ApprovalChoice
{
    Once,
    Chat,
    Deny,
}

/// <summary>
/// How a task ended. <see cref="Text"/> is the artifact (the final answer) for a completed task, the reason for any other.
/// </summary>
internal sealed record TaskOutcome(string TaskId, TaskState State, string? Text);

/// <summary>Embedded side, one accepted task: what the runner may tell the caller while it works.</summary>
internal interface IDelegatedTaskChannel
{
    /// <summary>A status update (working, with a short line of progress).</summary>
    Task StatusAsync(TaskState state, string? text);

    /// <summary>
    /// The task needs a decision (state input-required); returns when the caller's person has decided. A caller that goes
    /// away answers <see cref="ApprovalChoice.Deny"/> and cancels the token passed to the runner.
    /// </summary>
    Task<ApprovalChoice> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken);
}

/// <summary>Embedded side: what the host does with a task a server accepted.</summary>
internal interface IDelegatedTaskRunner
{
    /// <summary>
    /// Runs the task and returns how it ended (never throws for a task's own failure). The token is cancelled when the
    /// caller cancels or goes away. A runner that is busy returns <see cref="TaskState.Rejected"/> at once.
    /// </summary>
    Task<TaskOutcome> RunAsync(TaskRequest request, IDelegatedTaskChannel channel, CancellationToken cancellationToken);
}

/// <summary>Embedded side: listens for callers and gives each task to the runner.</summary>
internal interface IDelegationServer : IDisposable
{
    /// <summary>Where the server can be reached and the secret callers must present.</summary>
    AgentDelegationEndpoint Endpoint { get; }

    void Start(IDelegatedTaskRunner runner);
}

/// <summary>Caller side: what the caller learns while its task runs.</summary>
internal interface ITaskObserver
{
    void Status(TaskState state, string? text);

    /// <summary>Asks the caller's person; returns when they decided.</summary>
    Task<ApprovalChoice> ApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken);
}

/// <summary>Caller side: sends one task to an agent and waits for it to end.</summary>
internal interface IDelegationClient
{
    /// <summary>
    /// Runs the task. A cancelled token cancels the task and still returns its outcome (canceled); an agent that goes
    /// away mid-task, a refused secret or a message that is too large gives <see cref="TaskState.Failed"/> with a reason.
    /// </summary>
    Task<TaskOutcome> RunAsync(AgentDelegationEndpoint endpoint, TaskRequest request, ITaskObserver observer,
        CancellationToken cancellationToken);
}

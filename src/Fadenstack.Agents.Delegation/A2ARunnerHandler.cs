using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Fadenstack.Agents.Delegation;

// How a delegated task looks as A2A 1.0 (the shapes the caller and the agent agree on):
//
//   new task      SendStreamingMessage: message {role user, parts [text], metadata {"fadenstack": {"from": {slug, name}, "references": [...]}}}
//   progress      TaskStatusUpdateEvent state TASK_STATE_WORKING, status.message with a text part
//   approval      TaskStatusUpdateEvent state TASK_STATE_INPUT_REQUIRED, status.message {role agent, parts [text, data
//                 {approvalId, tool, toolClass, summary, argumentsPreview}]}; the stream ends there
//   the answer    SendStreamingMessage on the same task: message {taskId, contextId, parts [data {approvalId, decision: once|chat|deny}]};
//                 a new stream follows the task on
//   the result    TaskArtifactUpdateEvent with one text part (the final answer), then TASK_STATE_COMPLETED; failed, rejected and
//                 canceled are A2A states, with the reason as a text part of status.message
//   cancel        CancelTask

/// <summary>The names the delegation uses inside A2A messages.</summary>
internal static class A2AParts
{
    public const string MetadataKey = "fadenstack";

    public static A2A.Message AgentMessage(string taskId, string contextId, params A2A.Part[] parts) => new()
    {
        Role = A2A.Role.Agent, MessageId = Guid.NewGuid().ToString("N"), TaskId = taskId, ContextId = contextId, Parts = parts.ToList(),
    };

    public static A2A.Part Data(object value) => A2A.Part.FromData(JsonSerializer.SerializeToElement(value));

    public static string Decision(ApprovalChoice choice) => choice == ApprovalChoice.Once ? "once" : choice == ApprovalChoice.Chat ? "chat" : "deny";

    public static ApprovalChoice Choice(string? decision) => decision == "once" ? ApprovalChoice.Once : decision == "chat" ? ApprovalChoice.Chat : ApprovalChoice.Deny;

    /// <summary>The first text of a message's parts.</summary>
    public static string? Text(A2A.Message? message) => message?.Parts.FirstOrDefault(p => p.Text is not null)?.Text;

    public static string ToolClassOrEmpty(JsonElement data) => String(data, "toolClass");

    public static string String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}

/// <summary>
/// The A2A server's handler for the embedded agent: it runs each task with the host's <see cref="IDelegatedTaskRunner"/> (a new
/// session of the add-in's agent) and tells the story as A2A task events. A task that needs an approval ends its stream in
/// <c>input-required</c>; the caller's next message on the task answers it and the same run goes on.
/// </summary>
internal sealed class A2ARunnerHandler : A2A.IAgentHandler, A2A.IAgentCancellationHandler
{
    private readonly IDelegatedTaskRunner _runner;
    private readonly ConcurrentDictionary<string, Run> _runs = new();

    public A2ARunnerHandler(IDelegatedTaskRunner runner) => _runner = runner;

    /// <summary>Stops the run of a task whose caller went away.</summary>
    public void Abandon(string taskId)
    {
        if (_runs.TryRemove(taskId, out var run))
        {
            run.Cancel();
        }
    }

    /// <summary>Stops every run (the host closes).</summary>
    public void AbandonAll()
    {
        foreach (var id in _runs.Keys)
        {
            Abandon(id);
        }
    }

    public async Task ExecuteAsync(A2A.RequestContext context, A2A.AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new A2A.TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        try
        {
            Run? run;
            if (!context.IsContinuation)
            {
                await updater.SubmitAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var (from, references) = Metadata(context.Message);
                run = new Run(context.TaskId);
                _runs[context.TaskId] = run;
                run.Start(_runner, new TaskRequest(context.TaskId, context.UserText ?? "", references, from.Slug, from.Name),
                    () => _runs.TryRemove(context.TaskId, out _));
            }
            else if (!_runs.TryGetValue(context.TaskId, out run))
            {
                await updater.FailAsync(A2AParts.AgentMessage(context.TaskId, context.ContextId, A2A.Part.FromText("The task is no longer running.")),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            else if (!run.Answer(context.Message))
            {
                await updater.FailAsync(A2AParts.AgentMessage(context.TaskId, context.ContextId,
                    A2A.Part.FromText("The message answers no approval this task waits for.")), cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await PumpAsync(run, updater, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the stream was stopped (a cancel, or the caller left): CancelAsync or Abandon deals with the run
            eventQueue.Complete();
        }
    }

    /// <summary>Follows the run until it needs the caller (input-required) or ends.</summary>
    private async Task PumpAsync(Run run, A2A.TaskUpdater updater, CancellationToken cancellationToken)
    {
        while (true)
        {
            var step = await run.Events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (step)
            {
                case Run.Progress progress:
                    await updater.StartWorkAsync(A2AParts.AgentMessage(updater.TaskId, updater.ContextId, A2A.Part.FromText(progress.Text)),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case Run.Approval approval:
                    var ask = approval.Ask;
                    await updater.RequireInputAsync(A2AParts.AgentMessage(updater.TaskId, updater.ContextId,
                        A2A.Part.FromText("Approval needed: " + ask.Tool),
                        A2AParts.Data(new
                        {
                            approvalId = ask.ApprovalId, tool = ask.Tool, toolClass = ask.ToolClass, summary = ask.Summary,
                            argumentsPreview = ask.ArgumentsPreview,
                        })), cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                case Run.Finished finished:
                    await FinishAsync(updater, finished.Outcome, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
    }

    private static async Task FinishAsync(A2A.TaskUpdater updater, TaskOutcome outcome, CancellationToken cancellationToken)
    {
        var reason = A2AParts.AgentMessage(updater.TaskId, updater.ContextId, A2A.Part.FromText(outcome.Text ?? ""));
        switch (outcome.State)
        {
            case TaskState.Completed:
                await updater.AddArtifactAsync(new[] { A2A.Part.FromText(outcome.Text ?? "") }, name: "answer", cancellationToken: cancellationToken).ConfigureAwait(false);
                await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case TaskState.Rejected:
                await updater.RejectAsync(reason, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case TaskState.Canceled:
                await updater.CancelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            default:
                await updater.FailAsync(reason, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The A2A cancel method: the run stops (a pending approval is denied) and the task ends canceled.</summary>
    public async Task CancelAsync(A2A.RequestContext context, A2A.AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        if (_runs.TryRemove(context.TaskId, out var run))
        {
            run.Cancel();
            await run.Done.WaitAsyncWithin(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        await new A2A.TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static (FromInfo From, IReadOnlyList<string> References) Metadata(A2A.Message message)
    {
        var from = new FromInfo("", "");
        var references = new List<string>();
        if (message.Metadata is { } metadata && metadata.TryGetValue(A2AParts.MetadataKey, out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("from", out var party))
            {
                from = new FromInfo(A2AParts.String(party, "slug"), A2AParts.String(party, "name"));
            }
            if (info.TryGetProperty("references", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                references.AddRange(list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            }
        }
        return (from, references);
    }

    private readonly record struct FromInfo(string Slug, string Name);

    /// <summary>One delegated task's run: the runner on a background task, and the events it tells the A2A stream.</summary>
    private sealed class Run : IDelegatedTaskChannel
    {
        private readonly string _taskId;
        private readonly CancellationTokenSource _cancel = new();
        private readonly object _gate = new();
        private TaskCompletionSource<ApprovalChoice>? _waiting;
        private string? _waitingFor;
        private bool _cancelled;

        public Run(string taskId) => _taskId = taskId;

        public Channel<object> Events { get; } = Channel.CreateUnbounded<object>();

        public Task Done { get; private set; } = Task.CompletedTask;

        public sealed record Progress(string Text);

        public sealed record Approval(ApprovalAsk Ask);

        public sealed record Finished(TaskOutcome Outcome);

        public void Start(IDelegatedTaskRunner runner, TaskRequest request, Action finished)
        {
            Done = Task.Run(async () =>
            {
                TaskOutcome outcome;
                try
                {
                    outcome = await runner.RunAsync(request, this, _cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = new TaskOutcome(_taskId, TaskState.Canceled, "The task was canceled.");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    outcome = new TaskOutcome(_taskId, TaskState.Failed, "The agent failed: " + e.GetType().Name + ".");
                }
                Events.Writer.TryWrite(new Finished(outcome));
                finished();
            });
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _cancelled = true;
                _waiting?.TrySetResult(ApprovalChoice.Deny);
            }
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public Task StatusAsync(TaskState state, string? text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                Events.Writer.TryWrite(new Progress(text!));
            }
            return Task.CompletedTask;
        }

        public async Task<ApprovalChoice> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<ApprovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_cancelled)
                {
                    return ApprovalChoice.Deny;
                }
                (_waiting, _waitingFor) = (answer, ask.ApprovalId);
            }
            Events.Writer.TryWrite(new Approval(ask));
            using (cancellationToken.Register(() => answer.TrySetResult(ApprovalChoice.Deny)))
            {
                var choice = await answer.Task.ConfigureAwait(false);
                lock (_gate)
                {
                    (_waiting, _waitingFor) = (null, null);
                }
                return choice;
            }
        }

        /// <summary>The caller's message on the task: the answer to the approval it waits for.</summary>
        public bool Answer(A2A.Message message)
        {
            foreach (var part in message.Parts)
            {
                if (part.Data is { ValueKind: JsonValueKind.Object } data)
                {
                    TaskCompletionSource<ApprovalChoice>? waiting;
                    lock (_gate)
                    {
                        waiting = _waiting;
                        if (waiting is null || A2AParts.String(data, "approvalId") != _waitingFor)
                        {
                            return false;
                        }
                    }
                    return waiting.TrySetResult(A2AParts.Choice(A2AParts.String(data, "decision")));
                }
            }
            return false;
        }
    }
}

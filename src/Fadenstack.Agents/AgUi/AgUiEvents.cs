using System.Runtime.CompilerServices;
using System.Text.Json;
using Fadenstack.Governance;
using Fadenstack.Tools;

namespace Fadenstack.Agents.AgUi;

/// <summary>What <see cref="AgUiEvents.From"/> needs to know besides the events.</summary>
public sealed record AgUiOptions
{
    /// <summary>
    /// AG-UI's <c>threadId</c>: the conversation. Pass the session's <c>ChatSession.Id</c>. Left out, the first turn's id stands in.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>Milliseconds since the Unix epoch for each event's <c>timestamp</c>. Left out, the system clock (tests pass a fixed one).</summary>
    public Func<long>? Clock { get; init; }
}

/// <summary>
/// An adapter from the agent's own event stream to AG-UI events, so a third-party AG-UI client can render a Fadenstack
/// agent. The SDK's own view models keep using <see cref="AgentEvent"/>.
/// <code>
/// await foreach (var e in AgUiEvents.From(session.StreamAsync("Hello"), new AgUiOptions { ThreadId = session.Id }))
/// {
///     await response.WriteAsync("data: " + AgUiJson.Serialize(e) + "\n\n");
/// }
/// </code>
/// The mapping is in the "Interop" page of the docs. No secret and no header goes into an event: the arguments and
/// results of tool calls are what the native events carry; a call's pictures are not sent.
/// </summary>
public static class AgUiEvents
{
    /// <summary>
    /// The AG-UI events of a stream of agent events, as they come. An exception of the source (a stopped turn) ends the
    /// stream with <c>RUN_ERROR</c> (a cancellation is thrown on). A tool call that waits for the user ends the run with
    /// <c>RUN_FINISHED</c> and an <c>interrupt</c> outcome; the events after the user's answer are a new run (<c>RUN_STARTED</c>
    /// with the id <c>{turnId}.2</c>) on the same thread.
    /// </summary>
    public static async IAsyncEnumerable<AgUiEvent> From(IAsyncEnumerable<AgentEvent> events, AgUiOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }
        var translator = new Translator(options ?? new AgUiOptions());
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentEvent? current = null;
                Exception? failure = null;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    current = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                if (failure is not null)
                {
                    foreach (var error in translator.Fail(failure))
                    {
                        yield return error;
                    }
                    yield break;
                }
                foreach (var translated in translator.Translate(current!))
                {
                    yield return translated;
                }
            }
            foreach (var rest in translator.End())
            {
                yield return rest;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Governance words the SDK keeps for itself (the gateway round it sends back as history, the approval it turns into a card).
    private static readonly string[] PassedThrough = { "faden.citations", "faden.pii", "faden.attachment_left_out", "faden.provenance" };

    private sealed class Translator
    {
        private readonly AgUiOptions _options;
        private string _turn = "";
        private string _thread;
        private int _run;
        private bool _open;
        private int _messages;
        private string? _text;
        private string? _reasoning;
        private (string CallId, bool Approved)? _resolved;
        private bool _done;

        public Translator(AgUiOptions options)
        {
            _options = options;
            _thread = options.ThreadId ?? "";
        }

        private long? Now() => _options.Clock is { } clock ? clock() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private string RunId => _run <= 1 ? _turn : _turn + "." + _run;

        public IEnumerable<AgUiEvent> Translate(AgentEvent e)
        {
            var output = new List<AgUiEvent>();
            if (_done)
            {
                return output;
            }
            if (_turn.Length == 0)
            {
                _turn = e.TurnId;
                if (_thread.Length == 0)
                {
                    _thread = _turn;
                }
            }
            switch (e)
            {
                case TurnStarted started:
                    Start(output, new Dictionary<string, object?> { ["toolMode"] = started.ToolMode.ToWire() });
                    break;
                case TextDelta text:
                    Start(output);
                    CloseReasoning(output);
                    if (text.Text.Length > 0)
                    {
                        if (_text is null)
                        {
                            _text = NextMessage();
                            output.Add(new TextMessageStart(_text) { Timestamp = Now() });
                        }
                        output.Add(new TextMessageContent(_text, text.Text) { Timestamp = Now() });
                    }
                    break;
                case ReasoningDelta reasoning:
                    Start(output);
                    CloseText(output);
                    if (reasoning.Text.Length > 0)
                    {
                        if (_reasoning is null)
                        {
                            _reasoning = NextMessage();
                            output.Add(new ReasoningStart(_reasoning) { Timestamp = Now() });
                            output.Add(new ReasoningMessageStart(_reasoning) { Timestamp = Now() });
                        }
                        output.Add(new ReasoningMessageContent(_reasoning, reasoning.Text) { Timestamp = Now() });
                    }
                    break;
                case ToolCallStarted call:
                    Start(output);
                    CloseMessages(output);
                    output.Add(new ToolCallStart(call.CallId, call.Name)
                    {
                        Timestamp = Now(),
                        Metadata = new Dictionary<string, object?> { ["toolClass"] = call.Class.ToWire(), ["source"] = call.Source },
                    });
                    output.Add(new ToolCallArgs(call.CallId, AgUiJson.SerializeValue(call.Arguments)) { Timestamp = Now() });
                    output.Add(new ToolCallEnd(call.CallId) { Timestamp = Now() });
                    break;
                case ToolCallCompleted done:
                    Start(output);
                    CloseMessages(output);
                    output.Add(new ToolCallResult(_turn + "-result-" + done.CallId, done.CallId, done.Result ?? "")
                    {
                        Timestamp = Now(),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["outcome"] = done.Outcome, ["durationMs"] = (long)done.Duration.TotalMilliseconds,
                        },
                    });
                    break;
                case ApprovalRequested approval:
                    Start(output);
                    CloseMessages(output);
                    var request = approval.Request;
                    output.Add(new RunFinished(_thread, RunId)
                    {
                        Timestamp = Now(),
                        Interrupts = new[]
                        {
                            new AgUiInterrupt(request.CallId, "approval_required")
                            {
                                Message = request.Reason.Length > 0 ? request.Reason : null,
                                ToolCallId = request.CallId,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["toolName"] = request.ToolName, ["toolClass"] = request.ToolClass.ToWire(), ["source"] = request.Source,
                                },
                            },
                        },
                    });
                    _open = false;
                    break;
                case ApprovalDecided decided:
                    CloseMessages(output);
                    _resolved = (decided.Request.CallId, decided.Decision.Approved);
                    break;
                case NoticeRaised notice:
                    Start(output);
                    CloseMessages(output);
                    output.Add(new Custom("faden.notice", NoticeValue(notice.Notice)) { Timestamp = Now() });
                    break;
                case GatewayEvent gateway:
                    if (gateway.Event.Type == "CUSTOM" && gateway.Event.Name is { } name && Array.IndexOf(PassedThrough, name) >= 0)
                    {
                        Start(output);
                        CloseMessages(output);
                        output.Add(new Custom(name, gateway.Event.Value) { Timestamp = Now() });
                    }
                    break;
                case TurnCompleted completed:
                    Start(output);
                    CloseMessages(output);
                    var result = completed.Result;
                    output.Add(new RunFinished(_thread, RunId)
                    {
                        Timestamp = Now(),
                        Result = new Dictionary<string, object?> { ["outcome"] = OutcomeName(result.Outcome) },
                        Usage = UsageOf(result.Usage),
                    });
                    _open = false;
                    _done = true;
                    break;
            }
            return output;
        }

        public IEnumerable<AgUiEvent> Fail(Exception ex)
        {
            var output = new List<AgUiEvent>();
            if (_done)
            {
                return output;
            }
            Start(output);
            CloseMessages(output);
            output.Add(new RunError(ex.Message) { Timestamp = Now() });
            _open = false;
            _done = true;
            return output;
        }

        public IEnumerable<AgUiEvent> End()
        {
            var output = new List<AgUiEvent>();
            CloseMessages(output);
            return output;
        }

        private string NextMessage() => _turn + "-m" + ++_messages;

        // A run is open from its RUN_STARTED to its RUN_FINISHED. The first event of a run after an interrupt opens the next one.
        private void Start(List<AgUiEvent> output, Dictionary<string, object?>? metadata = null)
        {
            if (_open)
            {
                return;
            }
            _run++;
            _open = true;
            if (_resolved is { } resolved && _run > 1)
            {
                metadata = new Dictionary<string, object?>
                {
                    ["resumes"] = new Dictionary<string, object?> { ["interruptId"] = resolved.CallId, ["approved"] = resolved.Approved },
                };
            }
            _resolved = null;
            output.Add(new RunStarted(_thread, RunId) { Timestamp = Now(), Metadata = metadata });
        }

        private void CloseText(List<AgUiEvent> output)
        {
            if (_text is { } id)
            {
                output.Add(new TextMessageEnd(id) { Timestamp = Now() });
                _text = null;
            }
        }

        private void CloseReasoning(List<AgUiEvent> output)
        {
            if (_reasoning is { } id)
            {
                output.Add(new ReasoningMessageEnd(id) { Timestamp = Now() });
                output.Add(new ReasoningEnd(id) { Timestamp = Now() });
                _reasoning = null;
            }
        }

        private void CloseMessages(List<AgUiEvent> output)
        {
            CloseReasoning(output);
            CloseText(output);
        }

        private static Dictionary<string, object?> NoticeValue(GovernanceNotice notice)
        {
            var value = new Dictionary<string, object?> { ["outcome"] = notice.Outcome };
            if (notice.Stage is not null)
            {
                value["stage"] = notice.Stage;
            }
            if (notice.Category is not null)
            {
                value["category"] = notice.Category;
            }
            value["message"] = notice.Message;
            if (notice.DecisionId is not null)
            {
                value["decisionId"] = notice.DecisionId;
            }
            if (notice.RequestId is not null)
            {
                value["requestId"] = notice.RequestId;
            }
            if (notice.RetryAfter is { } retry)
            {
                value["retryAfterMs"] = (long)retry.TotalMilliseconds;
            }
            return value;
        }

        private static string OutcomeName(ChatOutcome outcome) => outcome switch
        {
            ChatOutcome.PromptBlocked => "prompt_blocked",
            ChatOutcome.ResponseBlocked => "response_blocked",
            ChatOutcome.ToolDenied => "tool_denied",
            ChatOutcome.ApprovalRequired => "approval_required",
            ChatOutcome.RateLimited => "rate_limited",
            ChatOutcome.AgentDisabled => "agent_disabled",
            _ => "completed",
        };

        private static IReadOnlyList<AgUiTokenUsage>? UsageOf(Microsoft.Extensions.AI.UsageDetails? usage) =>
            usage is null || (usage.InputTokenCount is null && usage.OutputTokenCount is null && usage.TotalTokenCount is null)
                ? null
                : new[] { new AgUiTokenUsage { InputTokens = usage.InputTokenCount, OutputTokens = usage.OutputTokenCount, TotalTokens = usage.TotalTokenCount } };
    }
}

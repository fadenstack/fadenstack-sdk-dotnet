using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Faden.Agents.Sessions;
using Faden.Chat;
using Faden.Governance;
using Faden.Http;
using Faden.Models;
using Faden.Tools;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// One conversation with the agent, kept on this device (06). A turn sends the history the session rule
/// allows, streams the answer, runs the agent's tools under the session's tool mode -- asking the user where
/// the mode says so -- and ends with a typed outcome. Refusals are outcomes; the session goes on.
/// </summary>
public sealed class AgentSession
{
    private readonly FadenAgent _agent;
    private readonly List<TranscriptEntry> _entries;
    private readonly HashSet<string> _approvedForSession;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly ContextBuilder _context;

    internal AgentSession(FadenAgent agent, string id, List<TranscriptEntry> entries, ToolMode toolMode)
    {
        _agent = agent;
        Id = id;
        _entries = entries;
        ToolMode = toolMode;
        _context = new ContextBuilder { TokenBudget = agent.Settings.HistoryTokens };
        _approvedForSession = new HashSet<string>(entries
            .Where(e => e.Type == EntryTypes.ApprovalDecision && Bool(e.Data, "for_session") && Bool(e.Data, "approved"))
            .Select(e => ChatWire.Str(e.Data, "name") ?? ""));
    }

    public string Id { get; }

    /// <summary>The mode the user picked for this session (sdk/07). New sessions start in Ask.</summary>
    public ToolMode ToolMode { get; set; }

    /// <summary>The mode turns run in: the user's pick, narrowed by the agent's ceiling (EE).</summary>
    public ToolMode EffectiveToolMode => ToolModes.Narrowest(ToolMode, _agent.Ceiling);

    /// <summary>The transcript, as kept on this device.</summary>
    public IReadOnlyList<TranscriptEntry> Transcript => _entries;

    /// <summary>What the next request would carry as history.</summary>
    public IList<ChatMessage> History => _context.Build(_entries);

    /// <summary>Tools the user approved for the rest of this session.</summary>
    public IReadOnlyCollection<string> ApprovedForSession => _approvedForSession;

    /// <summary>Sends a message and waits for the turn to end.</summary>
    public async Task<AgentResult> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        AgentResult? result = null;
        await foreach (var e in StreamAsync(message, cancellationToken).ConfigureAwait(false))
        {
            if (e is TurnCompleted completed)
            {
                result = completed.Result;
            }
        }
        return result!;
    }

    /// <summary>Sends a message and streams what happens. Leaving the stream early cancels the turn.</summary>
    public async IAsyncEnumerable<AgentEvent> StreamAsync(string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await _turn.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A turn is already running in this session.");
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var run = RunAsync(message, channel.Writer, stop.Token);
        try
        {
            while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var e))
                {
                    yield return e;
                }
            }
            await run.ConfigureAwait(false); // rethrows what ended the turn
        }
        finally
        {
            if (!run.IsCompleted)
            {
                stop.Cancel();
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch
                {
                    // the reader left; the turn is recorded as cancelled
                }
            }
            _turn.Release();
        }
    }

    /// <summary>Reports the session's counts (never content) and sends what is left to report.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _agent.Reporter.Add(new SessionStatsEvent
        {
            ConversationId = Id,
            Turns = _entries.Count(e => e.Type == EntryTypes.TurnEnd),
            Compactions = _entries.Count(e => e.Type == EntryTypes.Compaction),
        });
        await _agent.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task WriteStartAsync(CancellationToken cancellationToken) =>
        AppendAsync(EntryTypes.SessionStart, null, new
        {
            agent = _agent.Slug,
            agent_version = _agent.Profile.Version,
            tool_mode = ToolMode.ToWire(),
        });

    internal static ToolMode? LastMode(IReadOnlyList<TranscriptEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Type is EntryTypes.TurnStart or EntryTypes.SessionStart
                && ToolModes.ParseMode(ChatWire.Str(entries[i].Data, "tool_mode")) is { } mode)
            {
                return mode;
            }
        }
        return null;
    }

    private async Task RunAsync(string message, ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield(); // the caller gets the stream before the first request goes out
            var turn = new Turn(this, Guid.NewGuid().ToString("N"), writer);
            await turn.RunAsync(message, cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception e)
        {
            writer.TryComplete(e);
            throw;
        }
        finally
        {
            await _agent.Reporter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AppendAsync(string type, string? turnId, object data, string? status = null)
    {
        var entry = new TranscriptEntry
        {
            ParentId = _entries.Count == 0 ? null : _entries[_entries.Count - 1].Id,
            Type = type,
            TurnId = turnId,
            Status = status,
            Data = JsonSerializer.SerializeToElement(data, FadenJson.Options),
        };
        // Never cancelled: a turn that was stopped is still recorded as stopped.
        await _agent.SessionStore.AppendAsync(_agent.Slug, Id, entry, CancellationToken.None).ConfigureAwait(false);
        _entries.Add(entry);
    }

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>One turn: the loop of model calls and tool runs, and its record.</summary>
    private sealed class Turn
    {
        private readonly AgentSession _session;
        private readonly string _id;
        private readonly ChannelWriter<AgentEvent> _writer;
        private readonly List<GovernanceNotice> _notices = new();
        private readonly HashSet<string> _deniedByGateway = new();
        private readonly StringBuilder _text = new();
        private UsageDetails? _usage;
        private GovernanceNotice? _blocked;
        private bool _toolDenied;
        private ToolMode _mode;

        public Turn(AgentSession session, string id, ChannelWriter<AgentEvent> writer)
        {
            _session = session;
            _id = id;
            _writer = writer;
        }

        private FadenAgent Agent => _session._agent;

        public async Task RunAsync(string message, CancellationToken cancellationToken)
        {
            _mode = _session.EffectiveToolMode;
            await _session.AppendAsync(EntryTypes.UserMessage, _id, new { text = message }).ConfigureAwait(false);
            await _session.AppendAsync(EntryTypes.TurnStart, _id, new
            {
                tool_mode = _session.ToolMode.ToWire(),
                effective_tool_mode = _mode.ToWire(),
            }).ConfigureAwait(false);
            Emit(new TurnStarted(_mode));
            try
            {
                for (var round = 0; ; round++)
                {
                    if (round == Agent.Settings.MaxRounds)
                    {
                        await NoticeAsync(new GovernanceNotice
                        {
                            Outcome = "round_limit", Stage = "tool",
                            Message = $"Stopped after {round} rounds of tool calls.",
                        }).ConfigureAwait(false);
                        break;
                    }
                    var calls = await RequestAsync(cancellationToken).ConfigureAwait(false);
                    if (_blocked is not null || calls.Count == 0)
                    {
                        break;
                    }
                    foreach (var call in calls)
                    {
                        await RunToolAsync(call, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await EndAsync(TurnStatus.Cancelled, null).ConfigureAwait(false);
                throw;
            }
            catch (FadenRateLimitedException e)
            {
                var notice = new GovernanceNotice
                {
                    Outcome = e.IsBudget ? "budget_exceeded" : "rate_limited", Message = e.Message, RetryAfter = e.RetryAfter,
                };
                await NoticeAsync(notice).ConfigureAwait(false);
                await EndAsync(TurnStatus.Error, e.IsBudget ? ChatOutcome.BudgetExceeded : ChatOutcome.RateLimited)
                    .ConfigureAwait(false);
                return;
            }
            catch
            {
                await EndAsync(TurnStatus.Error, null).ConfigureAwait(false);
                throw;
            }
            var status = _blocked?.Outcome switch
            {
                "prompt_blocked" => TurnStatus.PromptBlocked,
                null => TurnStatus.Ok,
                _ => TurnStatus.ResponseBlocked,
            };
            var outcome = status switch
            {
                TurnStatus.PromptBlocked => ChatOutcome.PromptBlocked,
                TurnStatus.ResponseBlocked => ChatOutcome.ResponseBlocked,
                _ => _toolDenied ? ChatOutcome.ToolDenied : ChatOutcome.Completed,
            };
            await EndAsync(status, outcome).ConfigureAwait(false);
        }

        /// <summary>One model call; returns the calls of the agent's own tools to run.</summary>
        private async Task<List<FunctionCallContent>> RequestAsync(CancellationToken cancellationToken)
        {
            var options = new ChatOptions { Tools = Agent.Offer(_mode) }
                .WithConversation(_session.Id, _id)
                .WithToolMode(_mode);
            if (options.Tools!.Count == 0)
            {
                options.Tools = null;
            }
            var history = _session._context.Build(_session._entries);
            var text = new StringBuilder();
            var calls = new List<FunctionCallContent>();
            await foreach (var update in Agent.Settings.Chat.GetStreamingResponseAsync(history, options, cancellationToken)
                .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent t when !string.IsNullOrEmpty(t.Text):
                            text.Append(t.Text);
                            Emit(new TextDelta(t.Text));
                            break;
                        case TextReasoningContent r when !string.IsNullOrEmpty(r.Text):
                            Emit(new ReasoningDelta(r.Text));
                            break;
                        case FunctionCallContent f when !f.InformationalOnly:
                            calls.Add(f);
                            break;
                        case UsageContent u:
                            AddUsage(u.Details);
                            break;
                        case GovernanceNoticeContent g:
                            _blocked = g.Notice;
                            break;
                        case FadenEventContent e:
                            await OnGatewayEventAsync(e, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
                if (update.FinishReason == ChatFinishReason.ContentFilter && _blocked is null)
                {
                    _blocked = new GovernanceNotice { Outcome = "response_blocked", Stage = "output", Message = "The answer was stopped." };
                }
            }
            if (_blocked is not null)
            {
                calls.Clear(); // a refused answer's tool calls never run
            }
            _text.Append(text);
            if (text.Length > 0 || calls.Count > 0)
            {
                await _session.AppendAsync(EntryTypes.AssistantMessage, _id, new
                {
                    text = text.ToString(),
                    tool_calls = calls.Select(c => new { id = c.CallId, name = c.Name, arguments = Json(c.Arguments) }).ToList(),
                    origin = "model",
                }).ConfigureAwait(false);
            }
            if (_blocked is not null)
            {
                await NoticeAsync(_blocked).ConfigureAwait(false);
            }
            return calls;
        }

        private async Task OnGatewayEventAsync(FadenEventContent e, CancellationToken cancellationToken)
        {
            var value = e.Value;
            switch (e.Kind)
            {
                case "faden.gateway_round":
                    await KeepRoundAsync(value).ConfigureAwait(false);
                    break;
                case "faden.approval_required":
                    await AnswerGatewayApprovalAsync(value, cancellationToken).ConfigureAwait(false);
                    break;
                case "faden.tool_denied":
                    _toolDenied = true;
                    if (ChatWire.Str(value, "toolCallId") is { Length: > 0 } callId)
                    {
                        _deniedByGateway.Add(callId);
                    }
                    await NoticeAsync(new GovernanceNotice
                    {
                        Outcome = "tool_denied", Stage = "tool",
                        Message = ChatWire.Str(value, "message") ?? $"'{ChatWire.Str(value, "toolCallName")}' is not allowed here.",
                        DecisionId = ChatWire.Str(value, "decisionId"),
                    }).ConfigureAwait(false);
                    break;
                case "faden.governance":
                    break; // arrives as GovernanceNoticeContent in the same update
                case "TOOL_CALL_START":
                    Emit(new ToolCallStarted(ChatWire.Str(e.Event, "toolCallId") ?? "", ChatWire.Str(e.Event, "toolCallName") ?? "",
                        ToolClass.Read, "gateway", new Dictionary<string, object?>()));
                    break;
                case "TOOL_CALL_END":
                    var failed = e.Event.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True;
                    var ms = e.Event.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                    Emit(new ToolCallCompleted(ChatWire.Str(e.Event, "toolCallId") ?? "", ChatWire.Str(e.Event, "toolCallName") ?? "",
                        "gateway", failed ? "error" : "ok", null, TimeSpan.FromMilliseconds(ms)));
                    break;
            }
            Emit(new GatewayEvent(e));
        }

        /// <summary>The gateway's own tool round, kept so the model still knows what it searched (02 §7).</summary>
        private async Task KeepRoundAsync(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            var names = new Dictionary<string, string>();
            foreach (var m in messages.EnumerateArray())
            {
                switch (ChatWire.Str(m, "role"))
                {
                    case "assistant":
                        var calls = new List<object>();
                        if (m.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in tc.EnumerateArray())
                            {
                                var f = c.TryGetProperty("function", out var fn) ? fn : default;
                                var id = ChatWire.Str(c, "id") ?? "";
                                names[id] = ChatWire.Str(f, "name") ?? "";
                                calls.Add(new { id, name = names[id], arguments = ChatWire.Str(f, "arguments") ?? "{}" });
                            }
                        }
                        await _session.AppendAsync(EntryTypes.AssistantMessage, _id, new
                        {
                            text = ChatWire.Str(m, "content") ?? "",
                            tool_calls = calls,
                            origin = "gateway",
                        }).ConfigureAwait(false);
                        break;
                    case "tool":
                        var callId = ChatWire.Str(m, "tool_call_id") ?? "";
                        await _session.AppendAsync(EntryTypes.ToolComplete, _id, new
                        {
                            call_id = callId,
                            name = names.TryGetValue(callId, out var n) ? n : null,
                            source = "gateway",
                            result = ChatWire.Str(m, "content") ?? "",
                            origin = "gateway",
                        }).ConfigureAwait(false);
                        break;
                }
            }
        }

        /// <summary>A gateway tool held for the user: ask, and answer the gateway while it waits.</summary>
        private async Task AnswerGatewayApprovalAsync(JsonElement value, CancellationToken cancellationToken)
        {
            var approvalId = ChatWire.Str(value, "approvalId");
            if (string.IsNullOrEmpty(approvalId))
            {
                return;
            }
            var request = new ApprovalRequest
            {
                CallId = ChatWire.Str(value, "toolCallId") ?? "",
                ToolName = ChatWire.Str(value, "toolCallName") ?? "",
                ToolClass = ToolClass.Unknown,
                Source = "gateway",
                Reason = ChatWire.Str(value, "message") ?? "",
            };
            var timeout = value.TryGetProperty("timeoutS", out var t) && t.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(t.GetDouble()) : TimeSpan.FromMinutes(5);
            var (decision, _) = await AskAsync(request, timeout, cancellationToken).ConfigureAwait(false);
            if (decision is null)
            {
                return; // timed out: the gateway expires it on its own
            }
            await Agent.Client.Agent!.AnswerApprovalAsync(approvalId!, decision.Approved, cancellationToken).ConfigureAwait(false);
            if (!decision.Approved)
            {
                _toolDenied = true;
            }
        }

        private async Task RunToolAsync(FunctionCallContent call, CancellationToken cancellationToken)
        {
            var tool = Agent.Find(call.Name);
            var source = tool?.Source ?? "host";
            var toolClass = tool?.Class ?? ToolClass.Unknown;
            var arguments = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments);
            await _session.AppendAsync(EntryTypes.ToolStart, _id, new
            {
                call_id = call.CallId, name = call.Name, source, tool_class = toolClass.ToWire(),
            }).ConfigureAwait(false);
            Emit(new ToolCallStarted(call.CallId, call.Name, toolClass, source, arguments));
            var watch = Stopwatch.StartNew();
            string result;
            string outcome;
            if (tool is null)
            {
                (result, outcome) = ($"Tool '{call.Name}' does not exist here.", "error");
            }
            else if (_deniedByGateway.Contains(call.CallId))
            {
                (result, outcome) = ($"Tool '{call.Name}' is not allowed here.", "denied");
            }
            else if (call.Exception is not null)
            {
                (result, outcome) = ($"Error: the arguments are not valid JSON ({call.Exception.Message}).", "error");
            }
            else
            {
                (result, outcome) = await DecideAndRunAsync(tool, call, arguments, cancellationToken).ConfigureAwait(false);
            }
            watch.Stop();
            await _session.AppendAsync(EntryTypes.ToolComplete, _id, new
            {
                call_id = call.CallId, name = call.Name, source, result, outcome,
                duration_ms = (long)watch.Elapsed.TotalMilliseconds, origin = "client",
            }).ConfigureAwait(false);
            Emit(new ToolCallCompleted(call.CallId, call.Name, source, outcome, result, watch.Elapsed));
            if (tool is not null)
            {
                Agent.Reporter.Add(new ToolExecutedEvent
                {
                    ConversationId = _session.Id, Tool = call.Name, Source = source, Server = tool.Server,
                    DurationMs = (long)watch.Elapsed.TotalMilliseconds, Outcome = outcome,
                });
            }
        }

        private async Task<(string Result, string Outcome)> DecideAndRunAsync(AgentTool tool, FunctionCallContent call,
            Dictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            switch (ToolModes.Decide(_mode, tool.Class))
            {
                case ToolModeDecision.Hidden:
                    _toolDenied = true;
                    var why = $"the tool mode is {_mode.ToWire()}";
                    await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Category = "mode", Message = $"'{tool.Name}' is not allowed: {why}." }).ConfigureAwait(false);
                    return ($"Tool '{tool.Name}' is not allowed here: {why}.", "denied");
                case ToolModeDecision.Ask:
                    var (decision, waited) = await AskAsync(new ApprovalRequest
                    {
                        CallId = call.CallId,
                        ToolName = tool.Name,
                        ToolClass = tool.Class,
                        Source = tool.Source,
                        Reason = tool.Class == ToolClass.Unknown
                            ? "Nobody has said what this tool does."
                            : $"'{tool.Name}' is {tool.Class.ToWire()}; the tool mode is {_mode.ToWire()}.",
                        Arguments = arguments,
                    }, null, cancellationToken).ConfigureAwait(false);
                    if (waited is { } ms)
                    {
                        Agent.Reporter.Add(new ApprovalDecidedEvent
                        {
                            ConversationId = _session.Id, Tool = tool.Name, WaitMs = ms,
                            Decision = decision is null ? "timeout" : decision.Approved ? "approved" : "denied",
                        });
                    }
                    if (decision is not { Approved: true })
                    {
                        _toolDenied = true;
                        var reason = decision?.Reason is { Length: > 0 } r ? $" ({r})" : "";
                        return ($"The user declined to run '{tool.Name}'{reason}.", "denied");
                    }
                    break;
            }
            try
            {
                var invocation = new ToolInvocation(tool, call, new AIFunctionArguments(arguments), _session.Id, _id);
                var value = await Invoker()(invocation, cancellationToken).ConfigureAwait(false);
                return (ChatWire.ResultText(new FunctionResultContent(call.CallId, value)), "ok");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return ($"Error: {e.Message}", "error");
            }
        }

        private ToolInvoker Invoker()
        {
            ToolInvoker invoker = (i, ct) => i.Tool.Function.InvokeAsync(i.Arguments, ct).AsTask();
            var stages = Agent.Settings.ToolStages;
            for (var s = stages.Count - 1; s >= 0; s--)
            {
                var stage = stages[s];
                var next = invoker;
                invoker = (i, ct) => stage.InvokeAsync(i, next, ct);
            }
            return invoker;
        }

        /// <summary>
        /// Asks the user, unless they approved this tool for the session. Returns null when the time ran out,
        /// and how long the user took (null when nobody was asked).
        /// </summary>
        private async Task<(ApprovalDecision? Decision, long? WaitedMs)> AskAsync(ApprovalRequest request, TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            if (_session._approvedForSession.Contains(request.ToolName))
            {
                return (ApprovalDecision.Approve(), null);
            }
            Emit(new ApprovalRequested(request));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t)
            {
                limit.CancelAfter(t);
            }
            var watch = Stopwatch.StartNew();
            ApprovalDecision? decision;
            try
            {
                decision = await Agent.Settings.Approvals.RequestAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                decision = null;
            }
            var waited = (long)watch.Elapsed.TotalMilliseconds;
            await _session.AppendAsync(EntryTypes.ApprovalDecision, _id, new
            {
                call_id = request.CallId,
                name = request.ToolName,
                source = request.Source,
                approved = decision?.Approved ?? false,
                for_session = decision?.ForSession ?? false,
                timed_out = decision is null,
                wait_ms = waited,
            }).ConfigureAwait(false);
            if (decision is { Approved: true, ForSession: true })
            {
                _session._approvedForSession.Add(request.ToolName);
            }
            Emit(new ApprovalDecided(request, decision ?? ApprovalDecision.Deny("No answer in time.")));
            return (decision, waited);
        }

        private async Task NoticeAsync(GovernanceNotice notice)
        {
            _notices.Add(notice);
            await _session.AppendAsync(EntryTypes.GovernanceNotice, _id, notice).ConfigureAwait(false);
            Emit(new NoticeRaised(notice));
        }

        private async Task EndAsync(string status, ChatOutcome? outcome)
        {
            await _session.AppendAsync(EntryTypes.TurnEnd, _id, new
            {
                status,
                outcome = outcome?.ToString(),
                usage = _usage is null ? null : new
                {
                    input_tokens = _usage.InputTokenCount,
                    output_tokens = _usage.OutputTokenCount,
                    total_tokens = _usage.TotalTokenCount,
                },
            }, status).ConfigureAwait(false);
            if (outcome is { } o)
            {
                Emit(new TurnCompleted(new AgentResult
                {
                    TurnId = _id,
                    Outcome = o,
                    Text = _text.ToString(),
                    Notices = _notices.ToList(),
                    Usage = _usage,
                }));
            }
        }

        private void AddUsage(UsageDetails details)
        {
            if (_usage is null)
            {
                _usage = new UsageDetails();
            }
            _usage.Add(details);
        }

        private void Emit(AgentEvent e) => _writer.TryWrite(e with { TurnId = _id });

        private static string Json(IDictionary<string, object?>? arguments) =>
            arguments is null ? "{}" : JsonSerializer.Serialize(arguments, ChatWire.Compact);
    }
}

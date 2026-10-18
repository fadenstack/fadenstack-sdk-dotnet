using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Fadenstack.Agents.Sessions;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// One conversation with the agent, kept on this device (06). A turn sends the history the session rule
/// allows, streams the answer, runs the agent's tools under the session's tool mode -- asking the user where
/// the mode says so -- and ends with a typed outcome. Refusals are outcomes; the session goes on.
/// </summary>
public sealed class AgentSession
{
    private readonly FadenstackAgent _agent;
    private readonly List<TranscriptEntry> _entries;
    private readonly HashSet<string> _approvedForSession = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly ContextBuilder _context;

    internal AgentSession(FadenstackAgent agent, string id, List<TranscriptEntry> entries, ToolMode toolMode)
    {
        _agent = agent;
        Id = id;
        _entries = entries;
        ToolMode = toolMode;
        _context = new ContextBuilder { TokenBudget = agent.Settings.HistoryTokens };
    }

    public string Id { get; }

    /// <summary>The mode the user picked for this session. New sessions start in Ask.</summary>
    public ToolMode ToolMode { get; set; }

    /// <summary>The mode turns run in: the user's pick, narrowed by the ceiling the server sets, if any.</summary>
    public ToolMode EffectiveToolMode => ToolModes.Narrowest(ToolMode, _agent.Ceiling);

    /// <summary>The transcript, as kept on this device.</summary>
    public IReadOnlyList<TranscriptEntry> Transcript => _entries;

    /// <summary>What the next request would carry as history.</summary>
    public IList<ChatMessage> History => _context.Build(_entries);

    /// <summary>
    /// Tools the user allowed for this chat. Kept in memory only: a session opened again asks again.
    /// </summary>
    public IReadOnlyCollection<string> ApprovedForSession => _approvedForSession;

    /// <summary>
    /// Forgets what the user allowed for this chat: every tool, or <paramref name="tool"/> only. (What they allowed
    /// always: <see cref="FadenstackAgent.ResetApprovalsAsync"/>.)
    /// </summary>
    public void ResetApprovals(string? tool = null)
    {
        lock (_approvedForSession)
        {
            if (tool is null)
            {
                _approvedForSession.Clear();
            }
            else
            {
                _approvedForSession.Remove(tool);
            }
        }
    }

    /// <summary>Sends a message and waits for the turn to end.</summary>
    public Task<AgentResult> SendAsync(string message, CancellationToken cancellationToken = default) =>
        SendAsync(message, Array.Empty<MessageAttachment>(), cancellationToken);

    /// <summary>Sends a message with attached context and waits for the turn to end.</summary>
    public Task<AgentResult> SendAsync(string message, IReadOnlyList<MessageAttachment> attachments,
        CancellationToken cancellationToken = default) =>
        SendAsync(message, new SendOptions { Attachments = attachments ?? Array.Empty<MessageAttachment>() }, cancellationToken);

    /// <summary>Sends a message with what goes with it (attachments, picked skills) and waits for the turn to end.</summary>
    public async Task<AgentResult> SendAsync(string message, SendOptions options, CancellationToken cancellationToken = default)
    {
        AgentResult? result = null;
        await foreach (var e in StreamAsync(message, options, cancellationToken).ConfigureAwait(false))
        {
            if (e is TurnCompleted completed)
            {
                result = completed.Result;
            }
        }
        return result!;
    }

    /// <summary>Sends a message and streams what happens. Leaving the stream early cancels the turn.</summary>
    public IAsyncEnumerable<AgentEvent> StreamAsync(string message, CancellationToken cancellationToken = default) =>
        StreamAsync(message, new SendOptions(), cancellationToken);

    /// <summary>Sends a message with attached context and streams what happens.</summary>
    public IAsyncEnumerable<AgentEvent> StreamAsync(string message, IReadOnlyList<MessageAttachment> attachments,
        CancellationToken cancellationToken = default) =>
        StreamAsync(message, new SendOptions { Attachments = attachments ?? Array.Empty<MessageAttachment>() }, cancellationToken);

    /// <summary>Sends a message with what goes with it (attachments, picked skills) and streams what happens.</summary>
    public async IAsyncEnumerable<AgentEvent> StreamAsync(string message, SendOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await _turn.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A turn is already running in this session.");
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var run = RunAsync(message, options ?? new SendOptions(), channel.Writer, stop.Token);
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

    private async Task RunAsync(string message, SendOptions options, ChannelWriter<AgentEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield(); // the caller gets the stream before the first request goes out
            var turn = new Turn(this, Guid.NewGuid().ToString("N"), writer);
            await turn.RunAsync(message, options.Attachments ?? Array.Empty<MessageAttachment>(), options.Skills ?? Array.Empty<PickedSkill>(),
                cancellationToken).ConfigureAwait(false);
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
            Data = JsonSerializer.SerializeToElement(data, FadenstackJson.Options),
        };
        // Never cancelled: a turn that was stopped is still recorded as stopped.
        await _agent.SessionStore.AppendAsync(_agent.Slug, Id, entry, CancellationToken.None).ConfigureAwait(false);
        _entries.Add(entry);
    }

    private bool ApprovedForChat(string tool)
    {
        lock (_approvedForSession)
        {
            return _approvedForSession.Contains(tool);
        }
    }

    private void ApproveForChat(string tool)
    {
        lock (_approvedForSession)
        {
            _approvedForSession.Add(tool);
        }
    }

    /// <summary>An admin's "Ask first" rule, unless the gateway says exactly <c>false</c>: an older gateway says nothing.</summary>
    private static bool Rule(JsonElement value) =>
        !(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("rule", out var r) && r.ValueKind == JsonValueKind.False);

    /// <summary>The stricter of two classes; unclassified when either is.</summary>
    private static ToolClass Stricter(ToolClass own, ToolClass? other)
    {
        if (other is not { } o || o == own)
        {
            return own;
        }
        if (own == ToolClass.Unknown || o == ToolClass.Unknown)
        {
            return ToolClass.Unknown;
        }
        return o > own ? o : own; // Read < Write < Destructive
    }

    /// <summary>Destructive and unclassified tools ask every time: never for the chat, never always.</summary>
    private static bool AsksEveryTime(ToolClass toolClass) => toolClass is ToolClass.Destructive or ToolClass.Unknown;

    /// <summary>
    /// A call of the agent's own tool the gateway marked (<c>faden.approval_required</c> without an approvalId): an
    /// admin's "Ask first" rule, or the class the gateway holds for the tool, never taken looser than the tool's own.
    /// </summary>
    private sealed record GatewayMark(bool Rule, ToolClass? ToolClass, string Message);

    /// <summary>How a call that needed approval was settled; the user's reason when they declined.</summary>
    private sealed record Settled(bool Approved, bool TimedOut, string? Reason);

    /// <summary>One turn: the loop of model calls and tool runs, and its record.</summary>
    private sealed class Turn
    {
        private readonly AgentSession _session;
        private readonly string _id;
        private readonly ChannelWriter<AgentEvent> _writer;
        private readonly List<GovernanceNotice> _notices = new();
        private readonly HashSet<string> _deniedByGateway = new();
        private readonly Dictionary<string, GatewayMark> _marks = new();
        private readonly StringBuilder _text = new();
        private readonly List<ProvenanceReceiptContent> _receipts = new();
        private UsageDetails? _usage;
        private GovernanceNotice? _blocked;
        private bool _toolDenied;
        private ToolMode _mode;
        private IReadOnlyList<string> _skills = Array.Empty<string>();

        // Pictures the turn's tools returned, by call; only the newest goes back to the model.
        private readonly Dictionary<string, IReadOnlyList<DataContent>> _pictures = new();
        private string? _newestPicture;

        public Turn(AgentSession session, string id, ChannelWriter<AgentEvent> writer)
        {
            _session = session;
            _id = id;
            _writer = writer;
        }

        private FadenstackAgent Agent => _session._agent;

        public async Task RunAsync(string message, IReadOnlyList<MessageAttachment> attachments, IReadOnlyList<PickedSkill> skills,
            CancellationToken cancellationToken)
        {
            _mode = _session.EffectiveToolMode;
            var picked = skills.Where(s => !string.IsNullOrEmpty(s?.Slug)).GroupBy(s => s.Slug, StringComparer.Ordinal).Select(g => g.First()).ToList();
            _skills = picked.Select(s => s.Slug).ToList();
            var user = new Dictionary<string, object?> { ["text"] = message };
            if (attachments.Count > 0)
            {
                user["attachments"] = attachments.Select(a => new
                {
                    title = a.Title, content = a.Content, source = a.Source, kind = a.Kind, reference = a.Reference, media_type = a.MediaType,
                }).ToList();
            }
            if (picked.Count > 0)
            {
                // The name too: a chat opened again shows the skill as it was called then.
                user["skills"] = picked.Select(s => new { slug = s.Slug, name = string.IsNullOrEmpty(s.Name) ? s.Slug : s.Name }).ToList();
            }
            await _session.AppendAsync(EntryTypes.UserMessage, _id, user).ConfigureAwait(false);
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
            catch (FadenstackRateLimitedException e)
            {
                var notice = new GovernanceNotice
                {
                    Outcome = "rate_limited", Message = e.Message, RetryAfter = e.RetryAfter,
                };
                await NoticeAsync(notice).ConfigureAwait(false);
                await EndAsync(TurnStatus.Error, ChatOutcome.RateLimited).ConfigureAwait(false);
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
            if (Agent.Settings.Reasoning is { } effort)
            {
                options.Reasoning = new ReasoningOptions { Effort = effort };
            }
            if (options.Tools!.Count == 0)
            {
                options.Tools = null;
            }
            // On every call of the turn, not the first only: the gateway gives a request the skills it names.
            options.WithSkills(_skills);
            var history = _session._context.Build(_session._entries);
            AttachNewestPicture(history);
            var text = new StringBuilder();
            var calls = new List<FunctionCallContent>();
            var receipts = new List<ProvenanceReceiptContent>();
            await foreach (var update in Agent.Settings.Chat.GetStreamingResponseAsync(history, options, cancellationToken)
                .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case ProvenanceReceiptContent p:
                            receipts.Add(p);
                            break;
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
                        case FadenstackEventContent e:
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
            _receipts.AddRange(receipts);
            if (text.Length > 0 || calls.Count > 0)
            {
                await _session.AppendAsync(EntryTypes.AssistantMessage, _id, new
                {
                    text = text.ToString(),
                    tool_calls = calls.Select(c => new { id = c.CallId, name = c.Name, arguments = Json(c.Arguments) }).ToList(),
                    origin = "model",
                    // What made the answer, by the receipts' ids: never more of them than that.
                    receipts = receipts.Count == 0 ? null : receipts.Select(ReceiptData).ToList(),
                }).ConfigureAwait(false);
            }
            if (_blocked is not null)
            {
                await NoticeAsync(_blocked).ConfigureAwait(false);
            }
            return calls;
        }

        private async Task OnGatewayEventAsync(FadenstackEventContent e, CancellationToken cancellationToken)
        {
            var value = e.Value;
            switch (e.Kind)
            {
                case "faden.gateway_round":
                    await KeepRoundAsync(value).ConfigureAwait(false);
                    break;
                case "faden.approval_required":
                    // With an approvalId the gateway runs the tool and waits for the answer; without, the call is one of
                    // the agent's own tools, which the session asks about as the gateway says.
                    if (string.IsNullOrEmpty(ChatWire.Str(value, "approvalId")))
                    {
                        Mark(value);
                    }
                    else
                    {
                        await AnswerGatewayApprovalAsync(value, cancellationToken).ConfigureAwait(false);
                    }
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

        /// <summary>The gateway's own tool round, kept so the model still knows what it searched.</summary>
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
            var approvalId = ChatWire.Str(value, "approvalId")!;
            var request = new ApprovalRequest
            {
                CallId = ChatWire.Str(value, "toolCallId") ?? "",
                ToolName = ChatWire.Str(value, "toolCallName") ?? "",
                ToolClass = ToolModes.ParseClass(ChatWire.Str(value, "toolClass")),
                Source = "gateway",
                Reason = ChatWire.Str(value, "message") ?? "",
            };
            var timeout = value.TryGetProperty("timeoutS", out var t) && t.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(t.GetDouble()) : TimeSpan.FromMinutes(5);
            var settled = await ApproveAsync(request, Rule(value), timeout, cancellationToken).ConfigureAwait(false);
            if (settled.TimedOut)
            {
                return; // the gateway expires it on its own
            }
            await Agent.Client.Agent!.AnswerApprovalAsync(approvalId, settled.Approved, cancellationToken).ConfigureAwait(false);
            if (!settled.Approved)
            {
                _toolDenied = true;
            }
        }

        /// <summary>The gateway's word on a call of the agent's own tool, kept until the call runs.</summary>
        private void Mark(JsonElement value)
        {
            if (ChatWire.Str(value, "toolCallId") is not { Length: > 0 } callId)
            {
                return;
            }
            var toolClass = ChatWire.Str(value, "toolClass");
            _marks[callId] = new GatewayMark(Rule(value), toolClass is null ? null : ToolModes.ParseClass(toolClass),
                ChatWire.Str(value, "message") ?? "");
        }

        private async Task RunToolAsync(FunctionCallContent call, CancellationToken cancellationToken)
        {
            var tool = Agent.Find(call.Name);
            var source = tool?.Source ?? "host";
            _marks.TryGetValue(call.CallId, out var mark);
            var toolClass = tool is null ? ToolClass.Unknown : Stricter(tool.Class, mark?.ToolClass);
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
            var waited = TimeSpan.Zero;
            IReadOnlyList<DataContent> pictures = Array.Empty<DataContent>();
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
                (result, outcome, waited, pictures) = await DecideAndRunAsync(tool, toolClass, mark, call, arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (pictures.Count > 0)
            {
                if (Agent.SeesPictures)
                {
                    _pictures[call.CallId] = pictures;
                    _newestPicture = call.CallId;
                }
                else
                {
                    result += (result.Length > 0 ? "\n" : "") + "(The model cannot see pictures: the picture was not sent.)";
                }
            }
            watch.Stop();
            // The tool's own time: the user's time on the approval card is reported apart (ApprovalDecidedEvent).
            var ran = watch.Elapsed > waited ? watch.Elapsed - waited : TimeSpan.Zero;
            await _session.AppendAsync(EntryTypes.ToolComplete, _id, new
            {
                call_id = call.CallId, name = call.Name, source, result, outcome,
                duration_ms = (long)ran.TotalMilliseconds, origin = "client", pictures = pictures.Count,
            }).ConfigureAwait(false);
            Emit(new ToolCallCompleted(call.CallId, call.Name, source, outcome, result, ran) { Pictures = pictures });
            if (tool is not null)
            {
                Agent.Reporter.Add(new ToolExecutedEvent
                {
                    ConversationId = _session.Id, Tool = call.Name, Source = source, Server = tool.Server,
                    DurationMs = (long)ran.TotalMilliseconds, Outcome = outcome,
                });
            }
        }

        /// <summary>
        /// Asks if the mode or an admin's rule says so, then runs the tool; also returns how long the asking took.
        /// <paramref name="toolClass"/> is the stricter of the tool's own and the gateway's.
        /// </summary>
        private async Task<(string Result, string Outcome, TimeSpan Waited, IReadOnlyList<DataContent> Pictures)> DecideAndRunAsync(AgentTool tool,
            ToolClass toolClass, GatewayMark? mark, FunctionCallContent call, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            var asking = Stopwatch.StartNew();
            var decided = ToolModes.Decide(_mode, toolClass);
            if (decided == ToolModeDecision.Hidden)
            {
                _toolDenied = true;
                var why = $"the tool mode is {_mode.ToWire()}";
                await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Category = "mode", Message = $"'{tool.Name}' is not allowed: {why}." }).ConfigureAwait(false);
                return ($"Tool '{tool.Name}' is not allowed here: {why}.", "denied", TimeSpan.Zero, Array.Empty<DataContent>());
            }
            var rule = mark is { Rule: true };
            if (rule || decided == ToolModeDecision.Ask)
            {
                var settled = await ApproveAsync(new ApprovalRequest
                {
                    CallId = call.CallId,
                    ToolName = tool.Name,
                    ToolClass = toolClass,
                    Source = tool.Source,
                    Reason = rule ? mark!.Message
                        : toolClass == ToolClass.Unknown ? "Nobody has said what this tool does."
                        : $"'{tool.Name}' is {toolClass.ToWire()}; the tool mode is {_mode.ToWire()}.",
                    Arguments = arguments,
                    OpenWorld = tool.OpenWorld == true,
                }, rule, null, cancellationToken,
                    tool.Describe is null ? null : () => DescribeTargetAsync(tool, arguments, cancellationToken)).ConfigureAwait(false);
                if (!settled.Approved)
                {
                    _toolDenied = true;
                    var reason = settled.Reason is { Length: > 0 } r ? $" ({r})" : "";
                    return ($"The user declined to run '{tool.Name}'{reason}.", "denied", asking.Elapsed, Array.Empty<DataContent>());
                }
            }
            var askedFor = asking.Elapsed;
            using var services = new ToolServices(Agent.Settings.ToolServices);
            try
            {
                var invocation = new ToolInvocation(tool, call, new AIFunctionArguments(arguments) { Services = services }, _session.Id, _id,
                    Agent, (type, data) => _session.AppendAsync(type, _id, data), NoticeAsync);
                services.Bind(invocation);
                var value = await Invoker()(invocation, cancellationToken).ConfigureAwait(false);
                var (text, pictures) = ToolResults.Split(call.CallId, value);
                return (text, "ok", askedFor, pictures);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return ($"Error: {e.Message}", "error", askedFor, Array.Empty<DataContent>());
            }
        }

        /// <summary>
        /// The newest picture a tool returned in this turn goes back with that tool's result; older ones are left out,
        /// their text stays. Pictures are never in the transcript, so later turns carry none.
        /// </summary>
        private void AttachNewestPicture(IList<ChatMessage> history)
        {
            if (_newestPicture is null || !_pictures.TryGetValue(_newestPicture, out var pictures))
            {
                return;
            }
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var message = history[i];
                if (message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == _newestPicture))
                {
                    foreach (var picture in pictures)
                    {
                        message.Contents.Add(picture);
                    }
                    return;
                }
            }
        }

        /// <summary>How long a tool may take to say what a call acts on before the card is shown without it.</summary>
        private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(3);

        /// <summary>What the card shows of a target at most.</summary>
        private const int MaxTargetChars = 300;

        /// <summary>
        /// What the call acts on, as the tool says (one line, clipped), or null: a tool that fails or takes too long only
        /// leaves the card with the arguments. A stopped turn stops here too.
        /// </summary>
        private static async Task<string?> DescribeTargetAsync(AgentTool tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DescribeTimeout);
            string? text;
            try
            {
                var describing = tool.Describe!(new Dictionary<string, object?>(arguments), limit.Token);
                var late = Task.Delay(Timeout.Infinite, limit.Token);
                if (await Task.WhenAny(describing, late).ConfigureAwait(false) != describing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
                text = await describing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                limit.Cancel(); // ends the wait above
            }
            var line = text is null ? "" : Regex.Replace(text, @"\s+", " ").Trim();
            if (line.Length == 0)
            {
                return null;
            }
            return line.Length > MaxTargetChars ? line.Substring(0, MaxTargetChars - 1) + "…" : line;
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
        /// Settles a call that needs approval, first that applies: an admin's rule asks (once only, even in bypass);
        /// bypass mode approves; a destructive or unclassified tool asks (once only); what the user allowed always,
        /// then what they allowed for this chat, approves; else the user is asked, and may allow once, for this chat or
        /// always. Every outcome goes in the transcript and the server's record, with what let the call through.
        /// <paramref name="target"/> is read only when the user is asked: what the call acts on, for the card.
        /// </summary>
        private async Task<Settled> ApproveAsync(ApprovalRequest request, bool rule, TimeSpan? timeout, CancellationToken cancellationToken,
            Func<Task<string?>>? target = null)
        {
            var (name, toolClass) = (request.ToolName, request.ToolClass);
            var open = !rule && !AsksEveryTime(toolClass);
            var standing = rule ? null
                : Agent.PermissionMode == PermissionMode.Bypass ? ApprovalScopes.Bypass
                : !open ? null
                : await ApprovedAlwaysAsync(name, toolClass).ConfigureAwait(false) ? ApprovalScopes.Always
                : _session.ApprovedForChat(name) ? ApprovalScopes.Chat
                : null;
            if (standing is not null)
            {
                await RecordAsync(request, standing, asked: false, rule, timedOut: false, waitedMs: null).ConfigureAwait(false);
                return new Settled(true, false, null);
            }
            var described = target is null ? null : await target().ConfigureAwait(false);
            request = request with { CanAllowForSession = open, CanAllowAlways = open, RequiredByRule = rule, Target = described };
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
                decision = null; // the time ran out
            }
            var waited = (long)watch.Elapsed.TotalMilliseconds;
            // What the request offered, and nothing more: a handler cannot widen it.
            var scope = decision is not { Approved: true } ? null
                : decision.Always && open ? ApprovalScopes.Always
                : decision.ForSession && open ? ApprovalScopes.Chat
                : ApprovalScopes.Once;
            if (scope == ApprovalScopes.Always && !await KeepAlwaysAsync(name, toolClass).ConfigureAwait(false))
            {
                scope = ApprovalScopes.Once;
            }
            if (scope == ApprovalScopes.Chat)
            {
                _session.ApproveForChat(name);
            }
            await RecordAsync(request, scope, asked: true, rule, timedOut: decision is null, waited).ConfigureAwait(false);
            Emit(new ApprovalDecided(request, decision is null ? ApprovalDecision.Deny("No answer in time.")
                : scope == ApprovalScopes.Always ? ApprovalDecision.ApproveAlways()
                : scope == ApprovalScopes.Chat ? ApprovalDecision.ApproveForSession()
                : scope == ApprovalScopes.Once ? ApprovalDecision.Approve()
                : decision));
            return new Settled(scope is not null, decision is null, decision?.Reason);
        }

        /// <summary>The approval's transcript entry, and the server's record of it (names only).</summary>
        private async Task RecordAsync(ApprovalRequest request, string? scope, bool asked, bool rule, bool timedOut, long? waitedMs)
        {
            await _session.AppendAsync(EntryTypes.ApprovalDecision, _id, new
            {
                call_id = request.CallId,
                name = request.ToolName,
                source = request.Source,
                approved = scope is not null,
                for_session = asked && scope == ApprovalScopes.Chat, // for older readers
                scope,
                asked,
                rule,
                timed_out = timedOut,
                wait_ms = waitedMs ?? 0,
            }).ConfigureAwait(false);
            Agent.Reporter.Add(new ApprovalDecidedEvent
            {
                ConversationId = _session.Id, Tool = request.ToolName, WaitMs = waitedMs, Scope = scope,
                Decision = timedOut ? "timeout" : scope is not null ? "approved" : "denied",
            });
        }

        private async Task<bool> ApprovedAlwaysAsync(string tool, ToolClass toolClass)
        {
            try
            {
                var kept = await Agent.ApprovalStore.ListAsync(Agent.Slug).ConfigureAwait(false);
                return kept.Any(a => a.Tool == tool && a.ToolClass == toolClass);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false; // a store that cannot be read approves nothing
            }
        }

        private async Task<bool> KeepAlwaysAsync(string tool, ToolClass toolClass)
        {
            try
            {
                await Agent.ApprovalStore.AddAsync(Agent.Slug, new StoredApproval(tool, toolClass, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false; // not kept: this call runs, the next one asks
            }
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
                    Receipts = _receipts.ToList(),
                }));
            }
        }

        /// <summary>A receipt as the transcript keeps it: its id and whether it was signed, never the receipt.</summary>
        private static object ReceiptData(ProvenanceReceiptContent receipt) => new
        {
            receipt_id = receipt.ReceiptId,
            status = receipt.Signed is not null ? "signed" : receipt.ReceiptId is not null ? "reference" : "unsigned",
            reason = receipt.Signed is null && receipt.ReceiptId is null ? receipt.Reason : null,
        };

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

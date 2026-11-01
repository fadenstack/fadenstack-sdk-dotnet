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
    private ReasoningLevel? _reasoning;

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

    /// <summary>
    /// How long the model thinks on the next message. Until it is set, the agent's level (<see cref="FadenstackAgent.Reasoning"/>:
    /// the person's pick, the profile's default, the app's) -- so a new conversation starts from the person's pick. A turn
    /// that already started keeps the level it started with.
    /// </summary>
    public ReasoningLevel Reasoning
    {
        get => _reasoning ?? _agent.Reasoning;
        set => _reasoning = value;
    }

    /// <summary>What a turn started now would send: the session's level, or -- while it follows the agent -- the agent's own effort.</summary>
    internal ReasoningEffort? RequestEffort => _reasoning is { } level ? level.ToEffort() : _agent.RequestEffort;

    /// <summary>
    /// Who is asked about this session's calls that need approval, instead of the agent's own handler
    /// (<see cref="AgentBuilder.UseApprovals(IApprovalHandler)"/>); null: the agent's. A session that someone else's person
    /// supervises sets it, such as a task delegated by another agent (<c>Fadenstack.Agents.Delegation.DelegationHost</c>).
    /// </summary>
    public IApprovalHandler? Approvals { get; set; }

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

    /// <summary>
    /// Which call of a round a word of the gateway (<c>faden.tool_denied</c>, <c>faden.approval_required</c>) is about: the
    /// call's index in the stream when the gateway gives one, else its id, else (no id) the tool's name.
    /// </summary>
    private sealed record Aim(string CallId, string ToolName, int? Index)
    {
        public static Aim From(JsonElement value) => new(ChatWire.Str(value, "toolCallId") ?? "", ChatWire.Str(value, "toolCallName") ?? "",
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty("toolCallIndex", out var i)
                && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : null);
    }

    /// <summary>A call as the model made it, with its place in the stream: the gateway may name either.</summary>
    private sealed record RoundCall(FunctionCallContent Call, string ModelId, int Index);

    /// <summary>A call of a round ready to run: under an id of its own, with what the gateway said of it.</summary>
    private sealed record PlannedCall(FunctionCallContent Call, bool Denied, bool Hidden, GatewayMark? Mark);

    /// <summary>How a call that needed approval was settled; the user's reason when they declined.</summary>
    private sealed record Settled(bool Approved, bool TimedOut, string? Reason, string? Kind);

    /// <summary>One turn: the loop of model calls and tool runs, and its record.</summary>
    private sealed class Turn
    {
        private readonly AgentSession _session;
        private readonly string _id;
        private readonly ChannelWriter<AgentEvent> _writer;
        private readonly List<GovernanceNotice> _notices = new();
        // What the gateway said of this round's calls (cleared as a round starts), and the tools it hides for the turn.
        private readonly List<Aim> _denials = new();
        private readonly List<(Aim Aim, GatewayMark Mark)> _marks = new();
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly StringBuilder _text = new();
        private readonly List<ProvenanceReceiptContent> _receipts = new();
        private UsageDetails? _usage;
        private GovernanceNotice? _blocked;
        private bool _toolDenied;
        private ToolMode _mode;
        private ReasoningEffort? _effort;
        private IReadOnlyList<string> _skills = Array.Empty<string>();

        // The turn's span and clock, and the model requests it made: what its end reports.
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private Activity? _activity;
        private int _rounds;

        /// <summary>What let a call that needed approval through, when an admin's rule made it ask (the scope says the rest).</summary>
        private const string RuleKind = "rule";

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
            _effort = _session.RequestEffort;
            using var scope = Log.TurnScope(Agent.Logger, _session.Id, _id);
            using var activity = AgentTelemetry.Source.StartActivity("invoke_agent " + Agent.Slug);
            _activity = activity;
            activity?.SetTag("gen_ai.operation.name", "invoke_agent");
            activity?.SetTag("gen_ai.agent.name", Agent.Slug);
            activity?.SetTag("gen_ai.conversation.id", _session.Id);
            activity?.SetTag("fadenstack.turn.id", _id);
            activity?.SetTag("fadenstack.tool_mode", _mode.ToWire());
            activity?.SetTag("fadenstack.reasoning_effort", ReasoningLevels.Record(_effort));
            Log.TurnStarted(Agent.Logger, _session.Id, _id, _mode.ToWire(), attachments.Count);
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
                reasoning_effort = ReasoningLevels.Record(_effort),
            }).ConfigureAwait(false);
            Emit(new TurnStarted(_mode));
            try
            {
                await Agent.RefreshToolsAsync(cancellationToken).ConfigureAwait(false); // tools that changed since the last turn, never inside one
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
                    var calls = await RequestAsync(round, cancellationToken).ConfigureAwait(false);
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
                    Outcome = "rate_limited", Message = e.Message, RetryAfter = e.RetryAfter, Code = e.Code,
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
        private async Task<List<PlannedCall>> RequestAsync(int round, CancellationToken cancellationToken)
        {
            _denials.Clear();
            _marks.Clear();
            _rounds++;
            using var activity = AgentTelemetry.Source.StartActivity("chat" + (Agent.Profile.Model is { } model ? " " + model : ""));
            activity?.SetTag("gen_ai.operation.name", "chat");
            activity?.SetTag("gen_ai.request.model", Agent.Profile.Model);
            activity?.SetTag("gen_ai.conversation.id", _session.Id);
            activity?.SetTag("fadenstack.round", round);
            var requested = Stopwatch.StartNew();
            var options = new ChatOptions { Tools = Agent.Offer(_mode, _session) }
                .WithConversation(_session.Id, _id)
                .WithToolMode(_mode);
            if (_effort is { } effort)
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
            ChatFinishReason? finish = null;
            await foreach (var update in Agent.Settings.Chat.GetStreamingResponseAsync(history, options, cancellationToken)
                .ConfigureAwait(false))
            {
                finish = update.FinishReason ?? finish;
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
            var planned = Plan(calls, round);
            _text.Append(text);
            _receipts.AddRange(receipts);
            if (text.Length > 0 || calls.Count > 0)
            {
                await _session.AppendAsync(EntryTypes.AssistantMessage, _id, new
                {
                    text = text.ToString(),
                    tool_calls = planned.Select(p => new { id = p.Call.CallId, name = p.Call.Name, arguments = Json(p.Call.Arguments) }).ToList(),
                    origin = "model",
                    // What made the answer, by the receipts' ids: never more of them than that.
                    receipts = receipts.Count == 0 ? null : receipts.Select(ReceiptData).ToList(),
                }).ConfigureAwait(false);
            }
            if (_blocked is not null)
            {
                await NoticeAsync(_blocked).ConfigureAwait(false);
            }
            else if (calls.Count == 0 && string.IsNullOrWhiteSpace(text.ToString()) && !cancellationToken.IsCancellationRequested)
            {
                // A model that thought and then wrote nothing would leave the user with nothing at all on screen.
                await NoticeAsync(EmptyAnswer(finish)).ConfigureAwait(false);
            }
            activity?.SetTag("fadenstack.tool_calls", planned.Count);
            Log.ModelRequest(Agent.Logger, _id, round, planned.Count, requested.ElapsedMilliseconds);
            return planned;
        }

        /// <summary>
        /// The round's calls, each under an id of its own and with the gateway's word on it. A call with no id, or an id
        /// another call of the round has, gets <c>fs-{turn}-{round}-{index}</c>. A word that cannot tell calls apart (no id,
        /// or one id on several calls) covers every call of its tool: a refusal is never lost, and a mark asks.
        /// </summary>
        private List<PlannedCall> Plan(List<FunctionCallContent> calls, int round)
        {
            var model = calls.Select((c, i) => new RoundCall(c, c.CallId,
                c.AdditionalProperties?.TryGetValue(FadenstackChatOptions.CallIndex, out var index) == true && index is int n ? n : i)).ToList();
            var denied = new bool[model.Count];
            var marks = new GatewayMark?[model.Count];
            foreach (var aim in _denials)
            {
                foreach (var i in Aimed(aim, model, out _))
                {
                    denied[i] = true;
                }
            }
            foreach (var (aim, mark) in _marks)
            {
                foreach (var i in Aimed(aim, model, out var byName))
                {
                    var word = byName ? mark with { Rule = true } : mark;
                    marks[i] = marks[i] is { } earlier ? Merge(earlier, word) : word;
                }
            }
            return model.Select((c, i) =>
            {
                var shared = c.ModelId.Length == 0 || model.Count(o => o.ModelId == c.ModelId) > 1;
                var call = shared
                    ? new FunctionCallContent($"fs-{_id}-{round}-{c.Index}", c.Call.Name, c.Call.Arguments) { Exception = c.Call.Exception }
                    : c.Call;
                var hidden = _hidden.Contains(c.Call.Name);
                return new PlannedCall(call, denied[i] || hidden, hidden, marks[i]);
            }).ToList();
        }

        /// <summary>The places in the round a word of the gateway covers; <paramref name="byName"/> when it could only go by the tool's name.</summary>
        private static List<int> Aimed(Aim aim, IReadOnlyList<RoundCall> round, out bool byName)
        {
            byName = false;
            if (aim.Index is { } index)
            {
                return Where(round, c => c.Index == index);
            }
            if (aim.CallId.Length > 0)
            {
                var same = Where(round, c => c.ModelId == aim.CallId);
                if (same.Count <= 1)
                {
                    return same;
                }
            }
            byName = true;
            return Where(round, c => aim.ToolName.Length == 0 || c.Call.Name == aim.ToolName);
        }

        private static List<int> Where(IReadOnlyList<RoundCall> round, Func<RoundCall, bool> match) =>
            Enumerable.Range(0, round.Count).Where(i => match(round[i])).ToList();

        /// <summary>Two marks on one call: a rule wins, the stricter class wins.</summary>
        private static GatewayMark Merge(GatewayMark a, GatewayMark b) => new(a.Rule || b.Rule,
            a.ToolClass is { } own ? Stricter(own, b.ToolClass) : b.ToolClass, b.Rule && !a.Rule ? b.Message : a.Message);

        /// <summary>
        /// The notice for a model call that ended with no text and no tool call (<c>empty_answer</c>); <c>length</c> when
        /// the model ran out of tokens first, often while thinking. The UI says it in the user's language and offers to try again.
        /// </summary>
        private static GovernanceNotice EmptyAnswer(ChatFinishReason? finish) => finish == ChatFinishReason.Length
            ? new GovernanceNotice
            {
                Outcome = "empty_answer", Stage = "output", Category = "length", Message = "The model used up its length before it wrote an answer.",
            }
            : new GovernanceNotice { Outcome = "empty_answer", Stage = "output", Message = "The model ended without an answer." };

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
                    _denials.Add(Aim.From(value));
                    await NoticeAsync(new GovernanceNotice
                    {
                        Outcome = "tool_denied", Stage = "tool",
                        Message = ChatWire.Str(value, "message") ?? $"'{ChatWire.Str(value, "toolCallName")}' is not allowed here.",
                        DecisionId = ChatWire.Str(value, "decisionId"),
                    }).ConfigureAwait(false);
                    break;
                case "faden.governance":
                    // A refusal arrives as GovernanceNoticeContent in the same update; any other outcome only informs.
                    if (value.ValueKind == JsonValueKind.Object && !ChatWire.IsRefusal(ChatWire.Str(value, "outcome")))
                    {
                        await NoticeAsync(ChatWire.Notice(value)).ConfigureAwait(false);
                    }
                    break;
                case "faden.tool_mode":
                    FollowToolMode(value);
                    break;
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
                SessionId = _session.Id,
                TurnId = _id,
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

        /// <summary>The gateway's word on a call of the agent's own tool, kept until the round's calls are planned.</summary>
        private void Mark(JsonElement value)
        {
            var toolClass = ChatWire.Str(value, "toolClass");
            _marks.Add((Aim.From(value), new GatewayMark(Rule(value), toolClass is null ? null : ToolModes.ParseClass(toolClass),
                ChatWire.Str(value, "message") ?? "")));
        }

        /// <summary>
        /// The gateway's tool mode for this turn: the turn's mode is never widened by it, an unknown mode is ignored, and the
        /// tools it hides are refused here too.
        /// </summary>
        private void FollowToolMode(JsonElement value)
        {
            if (ToolModes.ParseMode(ChatWire.Str(value, "effective_tool_mode")) is { } effective)
            {
                _mode = ToolModes.Narrowest(_mode, effective);
            }
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in hidden.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String))
                {
                    _hidden.Add(name.GetString()!);
                }
            }
        }

        private async Task RunToolAsync(PlannedCall planned, CancellationToken cancellationToken)
        {
            var (call, mark) = (planned.Call, planned.Mark);
            var tool = Agent.Find(call.Name);
            var source = tool?.Source ?? "host";
            var toolClass = tool is null ? ToolClass.Unknown : Stricter(tool.Class, mark?.ToolClass);
            var arguments = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments);
            // A name the model made up is its words, not ours: only the agent's own tools are named in logs and traces.
            var loggedName = tool?.Name ?? "(unknown)";
            using var activity = AgentTelemetry.Source.StartActivity("execute_tool " + loggedName);
            activity?.SetTag("gen_ai.operation.name", "execute_tool");
            activity?.SetTag("gen_ai.tool.name", loggedName);
            activity?.SetTag("gen_ai.tool.call.id", call.CallId);
            activity?.SetTag("gen_ai.conversation.id", _session.Id);
            await _session.AppendAsync(EntryTypes.ToolStart, _id, new
            {
                call_id = call.CallId, name = call.Name, source, tool_class = toolClass.ToWire(),
            }).ConfigureAwait(false);
            Emit(new ToolCallStarted(call.CallId, call.Name, toolClass, source, arguments));
            var watch = Stopwatch.StartNew();
            string result;
            string outcome;
            string? approval = null;
            var waited = TimeSpan.Zero;
            IReadOnlyList<DataContent> pictures = Array.Empty<DataContent>();
            if (tool is null)
            {
                (result, outcome) = (Agent.IsGone(call.Name) ? $"{call.Name} is no longer available." : $"Tool '{call.Name}' does not exist here.", "error");
            }
            else if (planned.Denied)
            {
                if (planned.Hidden)
                {
                    _toolDenied = true;
                    await NoticeAsync(new GovernanceNotice
                    {
                        Outcome = "tool_denied", Stage = "tool", Category = "mode", Message = $"'{call.Name}' is not allowed: the gateway hides it in this tool mode.",
                    }).ConfigureAwait(false);
                }
                (result, outcome) = ($"Tool '{call.Name}' is not allowed here.", "denied");
            }
            else if (call.Exception is not null)
            {
                (result, outcome) = ($"Error: the arguments are not valid JSON ({call.Exception.Message}).", "error");
            }
            else if (tool.Scope?.BeforeCall(tool, _session) is { } answered)
            {
                (result, outcome) = (answered, "ok"); // a tool outside the conversation's scope, now switched on
            }
            else
            {
                (result, outcome, waited, pictures, approval) = await DecideAndRunAsync(tool, toolClass, mark, call, arguments, cancellationToken)
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
            RecordToolCall(activity, loggedName, toolClass.ToWire(), source, outcome, approval ?? "none", ran, waited);
            if (tool is not null)
            {
                Agent.Reporter.Add(new ToolExecutedEvent
                {
                    ConversationId = _session.Id, Tool = call.Name, Source = source, Server = tool.Server,
                    DurationMs = (long)ran.TotalMilliseconds, Outcome = outcome,
                });
            }
        }

        /// <summary>What a finished tool call leaves: its span's tags, the metrics, and a log line (names, class, outcome, times).</summary>
        private void RecordToolCall(Activity? activity, string tool, string toolClass, string source, string outcome, string approval,
            TimeSpan ran, TimeSpan waited)
        {
            activity?.SetTag("fadenstack.tool.class", toolClass);
            activity?.SetTag("fadenstack.tool.source", source);
            activity?.SetTag("fadenstack.tool.outcome", outcome);
            activity?.SetTag("fadenstack.approval", approval);
            activity?.SetTag("fadenstack.approval.wait_ms", (long)waited.TotalMilliseconds);
            if (outcome == "error")
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }
            var (classTag, outcomeTag) = (new KeyValuePair<string, object?>("class", toolClass), new KeyValuePair<string, object?>("outcome", outcome));
            AgentTelemetry.ToolCalls.Add(1, classTag, outcomeTag);
            AgentTelemetry.ToolDuration.Record(ran.TotalMilliseconds, classTag, outcomeTag);
            Log.ToolCall(Agent.Logger, tool, toolClass, source, outcome, approval, (long)ran.TotalMilliseconds);
        }

        /// <summary>
        /// Asks if the mode or an admin's rule says so, then runs the tool; also returns how long the asking took and what let
        /// the call through (null: nothing needed approval, or it was declined). <paramref name="toolClass"/> is the stricter of
        /// the tool's own and the gateway's.
        /// </summary>
        private async Task<(string Result, string Outcome, TimeSpan Waited, IReadOnlyList<DataContent> Pictures, string? Approval)> DecideAndRunAsync(
            AgentTool tool, ToolClass toolClass, GatewayMark? mark, FunctionCallContent call, Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var asking = Stopwatch.StartNew();
            var decided = ToolModes.Decide(_mode, toolClass);
            if (decided == ToolModeDecision.Hidden)
            {
                _toolDenied = true;
                var why = $"the tool mode is {_mode.ToWire()}";
                await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Category = "mode", Message = $"'{tool.Name}' is not allowed: {why}." }).ConfigureAwait(false);
                return ($"Tool '{tool.Name}' is not allowed here: {why}.", "denied", TimeSpan.Zero, Array.Empty<DataContent>(), null);
            }
            var rule = mark is { Rule: true };
            string? approval = null;
            if (rule || decided == ToolModeDecision.Ask)
            {
                var settled = await ApproveAsync(new ApprovalRequest
                {
                    SessionId = _session.Id,
                    TurnId = _id,
                    CallId = call.CallId,
                    ToolName = tool.Name,
                    ToolClass = toolClass,
                    Source = tool.Source,
                    Server = tool.Server,
                    ReadOnlyHint = tool.ReadOnlyHint,
                    DestructiveHint = tool.DestructiveHint,
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
                    return ($"The user declined to run '{tool.Name}'{reason}.", "denied", asking.Elapsed, Array.Empty<DataContent>(), null);
                }
                approval = settled.Kind;
            }
            var askedFor = asking.Elapsed;
            using var services = new ToolServices(Agent.Settings.ToolServices);
            try
            {
                var invocation = new ToolInvocation(tool, call, new AIFunctionArguments(arguments) { Services = services }, _session.Id, _id,
                    Agent, (type, data) => _session.AppendAsync(type, _id, data), NoticeAsync, _session);
                services.Bind(invocation);
                var value = await Invoker()(invocation, cancellationToken).ConfigureAwait(false);
                var (text, pictures) = ToolResults.Split(call.CallId, value);
                return (text, "ok", askedFor, pictures, approval);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return ($"Error: {e.Message}", "error", askedFor, Array.Empty<DataContent>(), approval);
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
        /// then what they allowed for 1 hour (only when the server advertises it), then what they allowed for this chat,
        /// approves; else the user is asked, and may allow once, for this chat, for 1 hour or always. Every outcome goes in the transcript and the server's record, with what let the call through.
        /// <paramref name="target"/> is read only when the user is asked: what the call acts on, for the card.
        /// </summary>
        private async Task<Settled> ApproveAsync(ApprovalRequest request, bool rule, TimeSpan? timeout, CancellationToken cancellationToken,
            Func<Task<string?>>? target = null)
        {
            var (name, toolClass) = (request.ToolName, request.ToolClass);
            var open = !rule && !AsksEveryTime(toolClass);
            DateTimeOffset? expires = null;
            string? standing;
            if (rule)
            {
                standing = null;
            }
            else if (Agent.PermissionMode == PermissionMode.Bypass)
            {
                standing = ApprovalScopes.Bypass;
            }
            else if (!open)
            {
                standing = null;
            }
            else if (await ApprovedAlwaysAsync(name, toolClass).ConfigureAwait(false))
            {
                standing = ApprovalScopes.Always;
            }
            else if (!request.OpenWorld && Agent.TimeGrantOf(name) is { } granted) // never for a call that reaches outside
            {
                (standing, expires) = (ApprovalScopes.Time, granted);
            }
            else
            {
                standing = _session.ApprovedForChat(name) ? ApprovalScopes.Chat : null;
            }
            if (standing is not null)
            {
                await RecordAsync(request, standing, asked: false, rule, timedOut: false, waitedMs: null, expires).ConfigureAwait(false);
                return new Settled(true, false, null, standing);
            }
            var described = target is null ? null : await target().ConfigureAwait(false);
            request = request with
            {
                CanAllowForSession = open, CanAllowAlways = open, CanAllowForTime = open && !request.OpenWorld && Agent.OffersTimeGrants,
                RequiredByRule = rule, Target = described,
            };
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
                decision = await (_session.Approvals ?? Agent.Settings.Approvals).RequestAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                decision = null; // the time ran out
            }
            var waited = (long)watch.Elapsed.TotalMilliseconds;
            // What the request offered, and nothing more: a handler cannot widen it.
            var scope = decision is not { Approved: true } ? null
                : decision.Always && open ? ApprovalScopes.Always
                : decision.ForTime && request.CanAllowForTime ? ApprovalScopes.Time
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
            else if (scope == ApprovalScopes.Time)
            {
                expires = Agent.GrantTime(name);
            }
            await RecordAsync(request, scope, asked: true, rule, timedOut: decision is null, waited, expires).ConfigureAwait(false);
            Emit(new ApprovalDecided(request, decision is null ? ApprovalDecision.Deny("No answer in time.")
                : scope == ApprovalScopes.Always ? ApprovalDecision.ApproveAlways()
                : scope == ApprovalScopes.Time ? ApprovalDecision.ApproveForTime()
                : scope == ApprovalScopes.Chat ? ApprovalDecision.ApproveForSession()
                : scope == ApprovalScopes.Once ? ApprovalDecision.Approve()
                : decision));
            return new Settled(scope is not null, decision is null, decision?.Reason, KindOf(scope, rule));
        }

        /// <summary>What let a call through: an admin's rule, else the scope the user gave (once, chat, always) or bypass; null if declined.</summary>
        private static string? KindOf(string? scope, bool rule) => scope is null ? null : rule ? RuleKind : scope;

        /// <summary>The approval's transcript entry, and the server's record of it (names only).</summary>
        private async Task RecordAsync(ApprovalRequest request, string? scope, bool asked, bool rule, bool timedOut, long? waitedMs,
            DateTimeOffset? expiresAt = null)
        {
            await _session.AppendAsync(EntryTypes.ApprovalDecision, _id, new
            {
                expires_at = scope == ApprovalScopes.Time ? expiresAt?.ToUniversalTime() : null, // only a grant for a while has an end
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
                ExpiresAt = scope == ApprovalScopes.Time ? expiresAt?.ToUniversalTime() : null,
                Decision = timedOut ? "timeout" : scope is not null ? "approved" : "denied",
            });
            var toolClass = request.ToolClass.ToWire();
            Log.Approval(Agent.Logger, request.ToolName, toolClass, KindOf(scope, rule) ?? (timedOut ? "timeout" : "declined"), asked, waitedMs ?? 0);
            if (waitedMs is { } waited)
            {
                AgentTelemetry.ApprovalWait.Record(waited, new KeyValuePair<string, object?>("class", toolClass));
            }
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
            // The message is the admin's text for the user and may quote what was checked: the log keeps the codes.
            Log.Notice(Agent.Logger, notice.Outcome, notice.Stage, notice.Category, notice.Code, notice.DecisionId);
            await _session.AppendAsync(EntryTypes.GovernanceNotice, _id, notice).ConfigureAwait(false);
            Emit(new NoticeRaised(notice));
        }

        private async Task EndAsync(string status, ChatOutcome? outcome)
        {
            _activity?.SetTag("fadenstack.turn.status", status);
            _activity?.SetTag("fadenstack.turn.outcome", outcome?.ToString());
            _activity?.SetTag("fadenstack.turn.rounds", _rounds);
            _activity?.SetTag("gen_ai.usage.input_tokens", _usage?.InputTokenCount);
            _activity?.SetTag("gen_ai.usage.output_tokens", _usage?.OutputTokenCount);
            if (status == TurnStatus.Error)
            {
                _activity?.SetStatus(ActivityStatusCode.Error);
            }
            AgentTelemetry.Turns.Add(1, new KeyValuePair<string, object?>("outcome", status));
            Log.TurnEnded(Agent.Logger, _session.Id, _id, _mode.ToWire(), _rounds, status, outcome?.ToString(), _watch.ElapsedMilliseconds);
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

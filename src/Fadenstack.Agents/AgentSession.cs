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
        get => _reasoning is { } level && level != ReasoningLevel.Auto ? ReasoningLevels.FromEffort(_agent.EffortFor(level)) : _reasoning ?? _agent.Reasoning;
        set => _reasoning = value;
    }

    /// <summary>What a turn started now would send: the session's level, or -- while it follows the agent -- the agent's own effort.</summary>
    internal ReasoningEffort? RequestEffort => _reasoning is { } level ? _agent.EffortFor(level) : _agent.RequestEffort;

    /// <summary>
    /// Who is asked about this session's calls that need approval, instead of the agent's own handler
    /// (<see cref="AgentBuilder.UseApprovals(IApprovalHandler)"/>); null: the agent's. A session that someone else's person
    /// supervises sets it, such as a task delegated by another agent (<c>Fadenstack.Agents.Delegation.DelegationHost</c>).
    /// </summary>
    public IApprovalHandler? Approvals { get; set; }

    /// <summary>
    /// The mode turns run in: the user's pick, narrowed by the ceiling the server sets, if any. Bypass only while the
    /// server holds the conversation's Bypass grant (<see cref="StartBypassAsync"/>): no grant, no Bypass -- it is Auto
    /// (sdk/09 L-59).
    /// </summary>
    public ToolMode EffectiveToolMode
    {
        get
        {
            var mode = ToolModes.Narrowest(ToolMode, _agent.Ceiling);
            return mode == ToolMode.Bypass && (Bypass is null || Delegated) ? ToolMode.Auto : mode;
        }
    }

    /// <summary>The conversation's live Bypass grant (the marker shows its time left); null when it is not in Bypass.</summary>
    public AgentGrant? Bypass => _agent.Grants.BypassFor(Id);

    /// <summary>
    /// A task another agent handed over (<c>Fadenstack.Agents.Delegation.DelegationHost</c>): it never runs in Bypass -- not
    /// by the app's <see cref="PermissionMode.Bypass"/>, not by its host's mode, and nobody switches it on for it -- so it
    /// never makes a Bypass grant nor sends the level (sdk/09 L-58: a delegated or receiving agent does not inherit it).
    /// </summary>
    internal bool Delegated { get; set; }

    // The mode before Bypass, to go back to when it ends.
    private ToolMode? _beforeBypass;

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

    /// <summary>
    /// Asks the person to let the agent work alongside them in this chat (desktop 37 §5.3): read and write in
    /// <paramref name="places"/>, for a while, without asking -- never for anything destructive, unclassified, or an
    /// admin's "Ask first" rule. The card is drawn by the app's grant handler (<see cref="IGrantHandler"/>); the person
    /// may remove places and picks the time. Returns the grant the server confirmed, or null: declined, not offered
    /// (no grants on the server, the agent does not offer them, no handler), or no place left. A refusal by the server
    /// is thrown (<see cref="FadenstackApiException"/>): no answer, no grant (L-59).
    /// </summary>
    public async Task<AgentGrant?> OfferWorkAsync(IReadOnlyList<GrantPlace> places, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var grants = _agent.Grants;
        if (_agent.Settings.Grants is not { } handler || !grants.Offers("grant") || places.Count == 0 || grants.Choices.Count == 0)
        {
            return null;
        }
        var answer = await handler.RequestGrantAsync(new GrantOffer
        {
            Places = places, Classes = grants.Settings!.GrantClasses, Durations = grants.Choices,
            Preselected = grants.Choices.Contains(AgentGrants.Preselected) ? AgentGrants.Preselected : grants.Choices.Last(),
            Reason = reason, ConversationId = Id,
        }, cancellationToken).ConfigureAwait(false);
        var kept = answer.Places.Where(p => places.Contains(p)).ToList();
        if (!answer.Allowed || kept.Count == 0)
        {
            return null;
        }
        return await grants.GrantWorkAsync(kept, Within(answer.Duration, grants.Choices), Id, answer.ShownText, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the person to extend a running grant -- <paramref name="more"/> time, and for a stretch of work the new
    /// <paramref name="places"/> -- on the same card, showing only the difference (desktop 37 §5.3). The server keeps the
    /// grant within the agent's longest grant from when it was made; past that it refuses, and a new grant needs the full
    /// card. A Bypass grant is never extended: it runs for the time its confirmation said, and more time is a new Bypass
    /// with the confirmation again (L-58). Returns the extended grant, or null when the person said no or nothing is offered.
    /// </summary>
    public async Task<AgentGrant?> OfferExtensionAsync(AgentGrant grant, TimeSpan more, IReadOnlyList<GrantPlace>? places = null,
        string? reason = null, CancellationToken cancellationToken = default)
    {
        var grants = _agent.Grants;
        if (_agent.Settings.Grants is not { } handler || !grants.Available || grant.Kind == GrantKinds.Bypass)
        {
            return null;
        }
        var added = places ?? Array.Empty<GrantPlace>();
        var answer = await handler.RequestGrantAsync(new GrantOffer
        {
            Places = added, Classes = grant.Scope.Classes ?? new List<string>(), Durations = new[] { more },
            Preselected = more, Reason = reason, Extending = grant,
        }, cancellationToken).ConfigureAwait(false);
        if (!answer.Allowed)
        {
            return null;
        }
        var kept = answer.Places.Where(p => added.Contains(p)).ToList();
        var time = answer.Duration > TimeSpan.Zero ? (answer.Duration < more ? answer.Duration : more) : TimeSpan.Zero;
        if (time == TimeSpan.Zero && kept.Count == 0)
        {
            return null;
        }
        return await grants.ExtendAsync(grant.Id, time, kept, answer.ShownText, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the person to switch approvals off in this conversation for a while (sdk/09 §3.12, L-58): the app's grant
    /// handler draws the confirmation (<see cref="GrantOffer.Bypass"/>), which says what will no longer be asked, what
    /// still asks and what the server records, and the person picks the time -- the shortest is preselected (sdk/40
    /// A3.3) -- and may say why. Only once the server confirmed the grant does the conversation run in Bypass
    /// (<see cref="ToolMode.Bypass"/>, L-59); a refusal is thrown (<see cref="FadenstackApiException"/>). An app set to
    /// <see cref="PermissionMode.Bypass"/> chose it in its own code: no card, the longest time, recorded the same way.
    /// <paramref name="reason"/> is recorded when the person gave none on the card. Returns the grant, or null: not
    /// offered (the organisation or the agent does not allow it), no handler, or declined. Never on the model's word: no
    /// tool and no other agent can call this (L-58).
    /// </summary>
    public async Task<AgentGrant?> StartBypassAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        var grants = _agent.Grants;
        var choices = grants.BypassChoices;
        if (Delegated || !_agent.BypassOffered || choices.Count == 0)
        {
            return null;
        }
        var duration = choices[choices.Count - 1];
        string? shown = null;
        if (_agent.PermissionMode != PermissionMode.Bypass)
        {
            if (_agent.Settings.Grants is not { } handler)
            {
                return null;
            }
            var answer = await handler.RequestGrantAsync(new GrantOffer
            {
                // 30 min first, as the work card (owner, 2026-11-09); where it is not offered, the longest that is.
                Bypass = true, Durations = choices, Reason = reason,
                Preselected = choices.Contains(AgentGrants.Preselected) ? AgentGrants.Preselected : choices[choices.Count - 1],
                Evidence = grants.Settings?.Bypass?.Evidence, ConversationId = Id,
            }, cancellationToken).ConfigureAwait(false);
            if (!answer.Allowed)
            {
                return null;
            }
            (duration, shown) = (Within(answer.Duration, choices), answer.ShownText);
            if (answer.Reason?.Trim() is { Length: > 0 } own)
            {
                reason = GrantAnswer.Cut(own);
            }
        }
        else if (_agent.Settings.StepUp is null)
        {
            // Set in the app's code, but nobody can sign in again for it: no Bypass, the session runs in Auto (sdk/41).
            Trace.TraceWarning("Bypass was not switched on: the app opens no second sign-in (AgentBuilder.UseStepUpHandler).");
            RunInAutoInstead();
            return null;
        }
        // The second sign-in comes after the card's Allow, never before and never on the model's word (sdk/41).
        var grant = await grants.GrantBypassAsync(duration, Id, reason, shown, cancellationToken).ConfigureAwait(false);
        if (ToolMode != ToolMode.Bypass)
        {
            (_beforeBypass, ToolMode) = (ToolMode, ToolMode.Bypass);
        }
        return grant;
    }

    /// <summary>
    /// Ends Bypass in this conversation: its grant ends from the next call on (a call already running finishes), and the
    /// conversation goes back to the mode it had before. The marker's Stop.
    /// </summary>
    public async Task StopBypassAsync(CancellationToken cancellationToken = default)
    {
        await EndBypassGrantsAsync(cancellationToken).ConfigureAwait(false);
        if (ToolMode == ToolMode.Bypass)
        {
            ToolMode = _beforeBypass ?? ToolMode.Auto;
        }
        _beforeBypass = null;
    }

    private async Task EndBypassGrantsAsync(CancellationToken cancellationToken)
    {
        foreach (var grant in _agent.Grants.Live.Where(g => g.Kind == GrantKinds.Bypass && g.ConversationId == Id).ToList())
        {
            await _agent.Grants.RevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Before a turn: a conversation that left Bypass ends its grant (leaving Bypass ends it, L-63); an app set to
    /// <see cref="PermissionMode.Bypass"/> starts it where the server offers it, never from Read only or Off -- Bypass
    /// skips questions, it does not show what the mode hides.
    /// </summary>
    private async Task SettleBypassAsync(CancellationToken cancellationToken)
    {
        if (ToolMode != ToolMode.Bypass && Bypass is not null)
        {
            await EndBypassGrantsAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!Delegated && _agent.PermissionMode == PermissionMode.Bypass && ToolMode >= ToolMode.Ask && Bypass is null && _agent.BypassOffered)
        {
            try
            {
                await StartBypassAsync("PermissionMode.Bypass", cancellationToken).ConfigureAwait(false);
            }
            catch (BypassSignInException e)
            {
                // No second sign-in, no Bypass (sdk/41): the turn runs in Auto.
                Trace.TraceWarning($"Bypass was not switched on, the turn runs in Auto: {e.Message}");
                RunInAutoInstead();
            }
        }
    }

    /// <summary>
    /// An app set to <see cref="PermissionMode.Bypass"/> that gets no Bypass (no second sign-in): its session runs in Auto
    /// instead (owner, 2026-11-09), never above the ceiling and never more than the app's own mode allows.
    /// </summary>
    private void RunInAutoInstead()
    {
        if (ToolMode is ToolMode.Ask)
        {
            ToolMode = ToolModes.Narrowest(ToolMode.Auto, _agent.Settings.DefaultMode);
        }
    }

    /// <summary>
    /// After a turn in Bypass: the calls the device ran under its grant go to the gateway, which compares them with its
    /// own records (L-61). A report that does not arrive is logged; the gateway's record stands on its own.
    /// </summary>
    private async Task ReportBypassAsync(Turn turn)
    {
        if (turn.BypassGrant is not { } grantId)
        {
            return;
        }
        try
        {
            await _agent.Grants.ReportAsync(grantId, turn.BypassRan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Trace.TraceWarning($"The calls of a turn in Bypass could not be reported to the server: {e.Message}");
        }
    }

    /// <summary>The longest of <paramref name="choices"/> not above <paramref name="picked"/>: a card cannot widen the offer.</summary>
    private static TimeSpan Within(TimeSpan picked, IReadOnlyList<TimeSpan> choices) =>
        choices.Where(c => c <= picked).DefaultIfEmpty(choices[0]).Max();

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

    /// <summary>
    /// One host tool the person started (a button, a view of a module), with its result; no model request. The server checks it
    /// first (<see cref="HostToolPhase.Prepare"/>) and asks the person through the approval handler where it says so --
    /// always for this call alone, never for the chat or for good -- then checks it again right before it runs
    /// (<see cref="HostToolPhase.Dispatch"/>), and anything that changed in between stops it. A refusal, a decline or a
    /// failure is the result's <see cref="HostCommandResult.Outcome"/>; a stopped command throws. The transcript gets a
    /// <c>user.command</c> entry, which the model never sees (<see cref="Sessions.EntryTypes.UserCommand"/>). Needs a server
    /// with the route (<c>POST tools/check</c>); an older one makes the command fail, never run unchecked.
    /// </summary>
    public async Task<HostCommandResult> InvokeToolAsync(string name, IReadOnlyDictionary<string, object?>? arguments,
        HostCommandOptions? options = null, CancellationToken cancellationToken = default)
    {
        HostCommandResult? result = null;
        await foreach (var e in StreamToolAsync(name, arguments, options, cancellationToken).ConfigureAwait(false))
        {
            if (e is ToolCallCompleted completed)
            {
                result = new HostCommandResult(completed.TurnId, completed.CallId, completed.Outcome, completed.Result);
            }
        }
        return result ?? throw new InvalidOperationException("The host command did not finish.");
    }

    /// <summary>The same, streaming the events a turn has; leaving the stream early, or cancelling, also cancels a pending approval.</summary>
    public async IAsyncEnumerable<AgentEvent> StreamToolAsync(string name, IReadOnlyDictionary<string, object?>? arguments,
        HostCommandOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new HostCommandOptions();
        HostCommands.CheckName(name);
        // A copy taken now, as strict JSON: what the person reviews is what runs, whatever the caller does with its own after.
        var copy = ParseArguments(CommandJson.Of(arguments ?? new Dictionary<string, object?>()));
        if (options.Origin == HostToolOrigin.ModelTool)
        {
            throw new ArgumentException("Invalid command origin.", nameof(options));
        }
        var label = HostCommandOptions.CheckLabel(options.Label);
        if (!await _turn.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A turn is already running in this session.");
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var run = RunCommandAsync(name, copy, options.Origin, label, channel.Writer, stop.Token);
        try
        {
            while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var e))
                {
                    yield return e;
                }
            }
            await run.ConfigureAwait(false); // rethrows what ended the command
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
                    // the reader left; the command is recorded as cancelled
                }
            }
            _turn.Release();
        }
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private async Task RunCommandAsync(string name, Dictionary<string, object?> arguments, HostToolOrigin origin, string? label,
        ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield(); // the caller gets the stream before the first check goes out
            var turn = new Turn(this, Guid.NewGuid().ToString("N"), writer);
            await turn.RunCommandAsync(name, arguments, origin, label, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The mode a session's transcript last ran in, never Bypass -- a switch-on never comes back from the transcript (sdk/09
    /// L-58): the mode it had before Bypass, else none. A conversation whose Bypass grant still lives opens in Bypass all the
    /// same (<see cref="FadenstackAgent.OpenSessionAsync"/>; owner, 2026-11-09: parallel chats never break).
    /// </summary>
    internal static ToolMode? LastMode(IReadOnlyList<TranscriptEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Type is EntryTypes.TurnStart or EntryTypes.SessionStart
                && ToolModes.ParseMode(ChatWire.Str(entries[i].Data, "tool_mode")) is { } mode && mode != ToolMode.Bypass)
            {
                return mode;
            }
        }
        return null;
    }

    /// <summary>A session opened again in Bypass (its grant lives): the mode to go back to when it ends.</summary>
    internal void Reopened(ToolMode? before)
    {
        if (ToolMode == ToolMode.Bypass)
        {
            _beforeBypass = before;
        }
    }

    /// <summary>
    /// What the model is told in the first request after a Bypass of its conversation ended (owner, 2026-11-09): it may say
    /// so; it can never switch Bypass on -- only the person can, in the app (sdk/09 L-58).
    /// </summary>
    internal const string BypassEndedNote =
        "Bypass has ended in this conversation: tools ask for the person's approval again, as the conversation's mode says. " +
        "You cannot switch Bypass on, and no tool does; only the person can, in the app.";

    /// <summary>Whether the last turn of the transcript ran in Bypass (its <c>effective_tool_mode</c>).</summary>
    private bool LastTurnRanInBypass()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Type == EntryTypes.TurnStart)
            {
                return ToolModes.ParseMode(ChatWire.Str(_entries[i].Data, "effective_tool_mode")) == ToolMode.Bypass;
            }
        }
        return false;
    }

    private async Task RunAsync(string message, SendOptions options, ChannelWriter<AgentEvent> writer,
        CancellationToken cancellationToken)
    {
        Turn? turn = null;
        try
        {
            await Task.Yield(); // the caller gets the stream before the first request goes out
            await SettleBypassAsync(cancellationToken).ConfigureAwait(false);
            turn = new Turn(this, Guid.NewGuid().ToString("N"), writer);
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
            if (turn is not null)
            {
                await ReportBypassAsync(turn).ConfigureAwait(false);
            }
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

    /// <summary>
    /// How a call that needed approval was settled; the user's reason when they declined, and what let an approved
    /// call through (<see cref="ApprovalScopes"/>).
    /// </summary>
    private sealed record Settled(bool Approved, bool TimedOut, string? Reason, string? Scope = null, string? GrantId = null);

    /// <summary>The server's answer to a host call's first check, what the person reviewed, and the limits they were reviewed under.</summary>
    private sealed record PreparedHostPolicy(HostToolDecision Decision, string InputJson, string Controls);

    /// <summary>A command the server or the person refused: the tool's outcome is <c>denied</c> with this text.</summary>
    private sealed class CommandDenied : Exception
    {
        public CommandDenied(string message) : base(message)
        {
        }
    }

    /// <summary>One turn: the loop of model calls and tool runs, and its record.</summary>
    private sealed class Turn
    {
        // A host command the person started (null: a turn of model calls), the arguments reviewed for it, and the time they took.
        private HostToolOrigin? _command;
        private string? _commandJson;
        private TimeSpan _commandWaited;

        private readonly AgentSession _session;
        private readonly string _id;
        private readonly ChannelWriter<AgentEvent> _writer;
        private readonly List<GovernanceNotice> _notices = new();
        // What the gateway said of this round's calls (cleared as a round starts), and the tools it hides for the turn.
        private readonly List<Aim> _denials = new();
        private readonly List<(Aim Aim, GatewayMark Mark)> _marks = new();
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

        // Calls of the agent's own tools the gateway said a grant covers (faden.grant_used): call id -> grant id and tool.
        // Cleared as a round starts too: a model that reuses an id in a later round never inherits an earlier call's grant.
        // A mark counts once, for that call and tool: it is taken out when read, so a repeated id never spends it twice.
        private readonly Dictionary<string, (string GrantId, string Tool)> _covered = new();
        private readonly StringBuilder _text = new();

        // The conversation's Bypass ended since its last turn: the model is told (BypassEndedNote).
        private bool _bypassEnded;
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

        // In Bypass: the conversation's grant as the turn began, and the calls of the app's tools it ran (L-61).
        private readonly List<string> _bypassRan = new();

        /// <summary>The Bypass grant the turn ran under; null when it did not run in Bypass.</summary>
        public string? BypassGrant { get; private set; }

        /// <summary>The calls of the app's own tools the turn ran in Bypass without the person's answer.</summary>
        public IReadOnlyList<string> BypassRan => _bypassRan;

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

        /// <summary>One host tool the person started: its own turn, without a model request (<see cref="InvokeToolAsync"/>).</summary>
        public async Task RunCommandAsync(string name, Dictionary<string, object?> arguments, HostToolOrigin origin, string? label,
            CancellationToken cancellationToken)
        {
            _mode = _session.EffectiveToolMode;
            _command = origin;
            _commandJson = CommandJson.Of(arguments);
            using var scope = Log.TurnScope(Agent.Logger, _session.Id, _id);
            await _session.AppendAsync(EntryTypes.UserCommand, _id, new { name, origin = HostCommands.Wire(origin), label }).ConfigureAwait(false);
            await _session.AppendAsync(EntryTypes.TurnStart, _id, new
            {
                tool_mode = _session.ToolMode.ToWire(),
                effective_tool_mode = _mode.ToWire(),
                origin = HostCommands.Wire(origin),
            }).ConfigureAwait(false);
            Emit(new TurnStarted(_mode));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tool = Agent.Find(name);
                if (tool is not null && tool.Source != "host")
                {
                    throw new InvalidOperationException("Explicit commands are available for host tools only.");
                }
                var call = new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments);
                await RunToolAsync(new PlannedCall(call, false, false, null), cancellationToken).ConfigureAwait(false);
                await EndAsync(TurnStatus.Ok, _toolDenied ? ChatOutcome.ToolDenied : ChatOutcome.Completed).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await EndAsync(TurnStatus.Cancelled, null).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await EndAsync(TurnStatus.Error, null).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>A check of a host call for the server: this turn's, with the session's level as it is now.</summary>
        private HostToolCheck CheckOf(AgentTool tool, string argumentsJson, HostToolOrigin origin, string? callId, HostToolPhase phase,
            HostToolDecision? prepared)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return new HostToolCheck
            {
                Name = tool.Name, Arguments = document.RootElement.Clone(), ToolMode = ToolModes.Narrowest(_mode, _session.EffectiveToolMode),
                Origin = origin, CallId = callId, Phase = phase, SchemaSha256 = CommandJson.SchemaHash(tool.Function.JsonSchema),
                Prepared = prepared, ConversationId = _session.Id, TurnId = _id,
            };
        }

        private static JsonElement ArgumentsElement(IEnumerable<KeyValuePair<string, object?>> arguments) =>
            JsonSerializer.SerializeToElement(arguments.ToDictionary(a => a.Key, a => a.Value), FadenstackJson.Options);

        private static string? CheckArguments(JsonElement schema, JsonElement arguments) => HostCommands.CheckArguments(schema, arguments);

        /// <summary>
        /// A model's host call, right before the tool runs: the arguments are the ones the person reviewed (a stage that
        /// changed them -- local PII unmasking -- would send raw values to a new cloud endpoint, so it stops the call), the
        /// approval limits are the same, and the server says the same again.
        /// </summary>
        private async Task CheckHostDispatchAsync(ToolInvocation invocation, PreparedHostPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CommandJson.Of(ArgumentsElement(invocation.Arguments)) != policy.InputJson)
            {
                throw new InvalidOperationException(
                    "Host arguments changed after policy review. Transformed host arguments require a reviewed disclosure contract.");
            }
            await Agent.RefreshPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (Agent.ApprovalControls != policy.Controls)
            {
                throw new InvalidOperationException("Approval limits changed. Review a new host call.");
            }
            var checkedAgain = await Agent.Client.Agent!.CheckHostToolAsync(
                CheckOf(invocation.Tool, policy.InputJson, HostToolOrigin.ModelTool, invocation.Call.CallId, HostToolPhase.Dispatch, policy.Decision),
                cancellationToken).ConfigureAwait(false);
            if (HostCommands.Drifted(policy.Decision, checkedAgain, Agent.Profile.Principal))
            {
                throw new InvalidOperationException("The host call's identity or central policy changed. Review it again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (CommandJson.Of(ArgumentsElement(invocation.Arguments)) != policy.InputJson)
            {
                throw new InvalidOperationException("Host arguments changed after approval.");
            }
        }

        /// <summary>
        /// The person's command, with the final stage arguments: checked by the server, approved where it says so, checked
        /// again immediately before the tool runs. Arguments a stage transformed are refused for the same reason as above.
        /// </summary>
        private async Task<object?> InvokeCommandAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serialized = CommandJson.Of(ArgumentsElement(invocation.Arguments));
            if (serialized != _commandJson)
            {
                throw new InvalidOperationException(
                    "Transformed direct-command arguments are not supported. Use the normal governed conversation.");
            }
            var tool = invocation.Tool;
            using var document = JsonDocument.Parse(serialized);
            var arguments = document.RootElement.Clone();
            if (HostCommands.CheckArguments(tool.Function.JsonSchema, arguments) is { } invalid)
            {
                throw new InvalidOperationException(invalid);
            }
            var origin = _command!.Value;
            var resource = Agent.Client.Agent!;
            var prepared = await resource.CheckHostToolAsync(
                CheckOf(tool, serialized, origin, null, HostToolPhase.Prepare, null), cancellationToken).ConfigureAwait(false);
            if (Agent.Profile.Principal is not { } principal || prepared.Principal != principal)
            {
                throw new InvalidOperationException("The command's authenticated identity does not match the active agent profile.");
            }
            var toolClass = Stricter(invocation.Class, prepared.ToolClass);
            var mode = ToolModes.Narrowest(_mode, prepared.EffectiveToolMode);
            var local = ToolModes.Decide(mode, toolClass);
            if (prepared.Decision == HostToolVerdict.Deny || local == ToolModeDecision.Hidden)
            {
                _toolDenied = true;
                await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Message = prepared.Message }).ConfigureAwait(false);
                throw new CommandDenied(prepared.Message);
            }
            if (prepared.Decision == HostToolVerdict.Ask || local == ToolModeDecision.Ask)
            {
                var asking = Stopwatch.StartNew();
                var copy = ParseArguments(serialized);
                var settled = await ApproveAsync(new ApprovalRequest
                {
                    SessionId = _session.Id,
                    TurnId = _id,
                    CallId = invocation.Call.CallId,
                    ToolName = tool.Name,
                    ToolClass = toolClass,
                    Source = "host",
                    ReadOnlyHint = tool.ReadOnlyHint,
                    DestructiveHint = tool.DestructiveHint,
                    Reason = prepared.Message,
                    Arguments = copy,
                    OpenWorld = tool.OpenWorld == true,
                }, prepared.RequiredByRule, gatewayWait: false, null, cancellationToken,
                    tool.Describe is null ? null : () => DescribeTargetAsync(tool, copy, cancellationToken), individual: true).ConfigureAwait(false);
                _commandWaited += asking.Elapsed;
                if (!settled.Approved)
                {
                    _toolDenied = true;
                    throw new CommandDenied($"The user declined to run '{tool.Name}'.");
                }
            }
            if (CommandJson.Of(ArgumentsElement(invocation.Arguments)) != serialized)
            {
                throw new InvalidOperationException("The command changed after review.");
            }
            var dispatched = await resource.CheckHostToolAsync(
                CheckOf(tool, serialized, origin, null, HostToolPhase.Dispatch, prepared), cancellationToken).ConfigureAwait(false);
            if (HostCommands.Drifted(prepared, dispatched, principal))
            {
                throw new InvalidOperationException("The command's identity or policy changed. Review it again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (CommandJson.Of(ArgumentsElement(invocation.Arguments)) != serialized)
            {
                throw new InvalidOperationException("The command changed after review.");
            }
            return await tool.Function.InvokeAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false);
        }

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
            BypassGrant = _mode == ToolMode.Bypass ? _session.Bypass?.Id : null;
            // The last turn ran in Bypass and this one does not: the model hears it ended (from the transcript, so an
            // app started again tells it too).
            _bypassEnded = _mode != ToolMode.Bypass && _session.LastTurnRanInBypass();
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
            // What the gateway said about calls holds for this round's calls only: a model may give a later round's
            // call an id it used before, and an earlier "a grant covers it" must never let that one run unasked.
            _marks.Clear();
            _covered.Clear();
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
            if (_bypassEnded)
            {
                // Before the person's newest message, every round of this turn: what the model may and may not do now.
                var newest = history.Select((m, i) => (m, i)).LastOrDefault(x => x.m.Role == ChatRole.User).i;
                history.Insert(Math.Max(0, newest), new ChatMessage(ChatRole.System, BypassEndedNote));
            }
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
                case "faden.grant_used":
                    // The gateway checked the call against the device's grants and counted it: the first line. The
                    // copy is the second (ApproveAsync).
                    if (ChatWire.Str(value, "toolCallId") is { Length: > 0 } covered && ChatWire.Str(value, "grantId") is { Length: > 0 } grant
                        && ChatWire.Str(value, "toolCallName") is { Length: > 0 } coveredTool)
                    {
                        _covered[covered] = (grant, coveredTool);
                    }
                    break;
                case "faden.grants":
                    Agent.Grants.Replace(value);
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

        /// <summary>
        /// A gateway tool held for the user: ask, and answer the gateway while it waits, saying what the approval covers.
        /// Bypass mode never answers here: the gateway runs the tool, and only the server may skip its question.
        /// </summary>
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
            var settled = await ApproveAsync(request, Rule(value), gatewayWait: true, timeout, cancellationToken).ConfigureAwait(false);
            if (settled.TimedOut)
            {
                return; // the gateway expires it on its own
            }
            // The gateway's own wait takes once, chat or always: an answer that also made a grant approves this call once.
            var scope = settled.Scope == ApprovalScopes.Grant ? ApprovalScopes.Once : settled.Scope;
            await Agent.Client.Agent!.AnswerApprovalAsync(approvalId, settled.Approved, scope, cancellationToken).ConfigureAwait(false);
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
        /// Asks if the mode, an admin's rule or the agent's contract says so, then runs the tool; also returns how long the
        /// asking took and what let the call through (null: nothing needed approval, or it was declined).
        /// <paramref name="toolClass"/> is the stricter of the tool's own and the gateway's. The contract's
        /// <see cref="AgentTool.Approval"/> goes on top of the mode, as the gateway puts it: <c>always</c> asks every time,
        /// like a rule; <c>ask</c> asks where the mode would run; <c>never</c> runs a read or write tool the mode would ask
        /// about, unless a rule asks. None of them shows a tool the mode hides.
        /// </summary>
        private async Task<(string Result, string Outcome, TimeSpan Waited, IReadOnlyList<DataContent> Pictures, string? Approval)> DecideAndRunAsync(
            AgentTool tool, ToolClass toolClass, GatewayMark? mark, FunctionCallContent call, Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var asking = Stopwatch.StartNew();
            PreparedHostPolicy? policy = null;
            if (_command is null && tool.Source == "host" && Agent.HostToolPolicyChecks)
            {
                // The server's word on this call, fresh, before the person is asked (host_tools.dispatch_check). Anything but a
                // clear answer stops the call: nothing falls back to the policy cached in the profile.
                try
                {
                    await Agent.RefreshPolicyAsync(cancellationToken).ConfigureAwait(false);
                    var inputJson = CommandJson.Of(arguments);
                    var prepared = await Agent.Client.Agent!.CheckHostToolAsync(
                        CheckOf(tool, inputJson, HostToolOrigin.ModelTool, call.CallId, HostToolPhase.Prepare, null), cancellationToken).ConfigureAwait(false);
                    if (Agent.Profile.Principal is not { } principal || prepared.Principal != principal)
                    {
                        throw new FadenstackException("The host call's authenticated identity changed.");
                    }
                    if (prepared.Decision == HostToolVerdict.Deny)
                    {
                        _toolDenied = true;
                        await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Message = prepared.Message }).ConfigureAwait(false);
                        return (prepared.Message, "denied", TimeSpan.Zero, Array.Empty<DataContent>(), null);
                    }
                    policy = new PreparedHostPolicy(prepared, inputJson, Agent.ApprovalControls);
                    toolClass = Stricter(toolClass, prepared.ToolClass);
                    _mode = ToolModes.Narrowest(ToolModes.Narrowest(_mode, _session.EffectiveToolMode), prepared.EffectiveToolMode);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    return ($"Error: {e.Message}", "error", TimeSpan.Zero, Array.Empty<DataContent>(), null);
                }
            }
            var decided = ToolModes.Decide(_mode, toolClass);
            if (decided == ToolModeDecision.Hidden)
            {
                _toolDenied = true;
                var why = $"the tool mode is {_mode.ToWire()}";
                await NoticeAsync(new GovernanceNotice { Outcome = "tool_denied", Stage = "tool", Category = "mode", Message = $"'{tool.Name}' is not allowed: {why}." }).ConfigureAwait(false);
                return ($"Tool '{tool.Name}' is not allowed here: {why}.", "denied", TimeSpan.Zero, Array.Empty<DataContent>(), null);
            }
            if (_command is not null && CheckArguments(tool.Function.JsonSchema, ArgumentsElement(arguments)) is { } invalid)
            {
                return ($"Error: {invalid}", "error", TimeSpan.Zero, Array.Empty<DataContent>(), null);
            }
            var always = tool.Approval == "always";
            var centralRule = policy?.Decision.RequiredByRule == true;
            var rule = mark is { Rule: true } || always || centralRule;
            if (tool.Approval == "ask")
            {
                decided = ToolModeDecision.Ask;
            }
            else if (tool.Approval == "never" && toolClass is ToolClass.Read or ToolClass.Write)
            {
                decided = ToolModeDecision.Run;
            }
            string? approval = null;
            // In Bypass the device reports what ran without the person's answer, as the gateway records it (L-61): what the
            // mode ran, and what the turn's own Bypass grant settled. A call the person answered is recorded as an answer
            // (L-60); one a time or work grant settled belongs to that grant.
            var unanswered = true;
            var centralAsks = policy?.Decision.Decision == HostToolVerdict.Ask;
            // A command asks inside its own check (InvokeCommandAsync), once the server has said whether it must.
            if (_command is null && (rule || decided == ToolModeDecision.Ask || centralAsks))
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
                    Reason = mark is { Rule: true } ? mark.Message
                        : centralRule || (centralAsks && decided != ToolModeDecision.Ask) ? policy!.Decision.Message
                        : always ? $"This agent asks every time before it uses '{tool.Name}'."
                        : tool.Approval == "ask" ? $"This agent asks before it uses '{tool.Name}'."
                        : toolClass == ToolClass.Unknown ? "Nobody has said what this tool does."
                        : $"'{tool.Name}' is {toolClass.ToWire()}; the tool mode is {_mode.ToWire()}.",
                    Arguments = arguments,
                    OpenWorld = tool.OpenWorld == true,
                }, rule, gatewayWait: false, null, cancellationToken,
                    tool.Describe is null ? null : () => DescribeTargetAsync(tool, arguments, cancellationToken)).ConfigureAwait(false);
                if (!settled.Approved)
                {
                    _toolDenied = true;
                    var reason = settled.Reason is { Length: > 0 } r ? $" ({r})" : "";
                    return ($"The user declined to run '{tool.Name}'{reason}.", "denied", asking.Elapsed, Array.Empty<DataContent>(), null);
                }
                approval = KindOf(settled.Scope, rule);
                unanswered = settled.Scope == ApprovalScopes.Grant && settled.GrantId == BypassGrant;
            }
            var askedFor = asking.Elapsed;
            using var services = new ToolServices(Agent.Settings.ToolServices);
            try
            {
                var invocation = new ToolInvocation(tool, call, new AIFunctionArguments(arguments) { Services = services }, _session.Id, _id,
                    Agent, (type, data) => _session.AppendAsync(type, _id, data), NoticeAsync, _session);
                services.Bind(invocation);
                if (BypassGrant is not null && unanswered)
                {
                    _bypassRan.Add(call.CallId); // it runs: the device reports it, whatever it returns
                }
                var value = await Invoker(policy)(invocation, cancellationToken).ConfigureAwait(false);
                var (text, pictures) = ToolResults.Split(call.CallId, value);
                return (text, "ok", askedFor + _commandWaited, pictures, approval);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CommandDenied e)
            {
                return (e.Message, "denied", askedFor + _commandWaited, Array.Empty<DataContent>(), approval);
            }
            catch (Exception e)
            {
                return ($"Error: {e.Message}", "error", askedFor + _commandWaited, Array.Empty<DataContent>(), approval);
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

        private ToolInvoker Invoker(PreparedHostPolicy? policy = null)
        {
            ToolInvoker invoker = async (i, ct) =>
            {
                if (_command is not null)
                {
                    return await InvokeCommandAsync(i, ct).ConfigureAwait(false);
                }
                if (policy is not null)
                {
                    await CheckHostDispatchAsync(i, policy, ct).ConfigureAwait(false);
                }
                return await i.Tool.Function.InvokeAsync(i.Arguments, ct).ConfigureAwait(false);
            };
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
        /// Settles a call that needs approval, first that applies: an admin's rule or a tool that asks every time asks (once
        /// only, even in Bypass); a grant the gateway said covers the call (<c>faden.grant_used</c>) and the device's copy
        /// holds approves (38 §6, the second line) -- in Bypass a destructive or unclassified call too, under the
        /// conversation's Bypass grant while the turn runs in Bypass; a destructive or unclassified tool asks (once only);
        /// what the user allowed always, then what they allowed for this chat, approves; else the user is asked, and
        /// may allow once, for this chat, always, or for a time (a grant the server holds) -- as far as the agent allows those
        /// (its <c>standing</c>). Every outcome goes in the transcript and the server's record, with what let the call
        /// through. <paramref name="target"/> is read only when the user is asked: what the call acts on, for the card.
        /// </summary>
        private async Task<Settled> ApproveAsync(ApprovalRequest request, bool rule, bool gatewayWait, TimeSpan? timeout,
            CancellationToken cancellationToken, Func<Task<string?>>? target = null, bool individual = false)
        {
            var (name, toolClass) = (request.ToolName, request.ToolClass);
            // An individual call (a person's own command) is asked about on its own: no grant, no standing approval, none offered.
            var open = !rule && !individual && !AsksEveryTime(toolClass);
            var (granted, leadsOutside) = rule || individual ? (null, null) : await CoveredByGrantAsync(request, cancellationToken).ConfigureAwait(false);
            var standing = rule || individual ? null
                : granted is not null ? ApprovalScopes.Grant
                : !open ? null
                : Offers(ApprovalScopes.Always) && await ApprovedAlwaysAsync(name, toolClass).ConfigureAwait(false) ? ApprovalScopes.Always
                : Offers(ApprovalScopes.Chat) && _session.ApprovedForChat(name) ? ApprovalScopes.Chat
                : null;
            if (standing is not null)
            {
                await RecordAsync(request, standing, asked: false, rule, timedOut: false, waitedMs: null, granted).ConfigureAwait(false);
                return new Settled(true, false, null, standing, granted);
            }
            var described = target is null ? null : await target().ConfigureAwait(false);
            var grants = Agent.Grants;
            var durations = open && toolClass is ToolClass.Read or ToolClass.Write && grants.Offers("time")
                ? grants.Choices : Array.Empty<TimeSpan>();
            request = request with
            {
                CanAllowForSession = open && Offers(ApprovalScopes.Chat), CanAllowAlways = open && Offers(ApprovalScopes.Always),
                GrantDurations = durations, RequiredByRule = rule, Target = described, LeadsOutside = leadsOutside,
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
                : decision.ForTime is { } d && durations.Contains(d) ? ApprovalScopes.Grant
                : decision.Always && request.CanAllowAlways ? ApprovalScopes.Always
                : decision.ForSession && request.CanAllowForSession ? ApprovalScopes.Chat
                : ApprovalScopes.Once;
            string? grantId = null;
            int? confirmed = null; // the minutes the server confirmed, which may be fewer than the person picked
            if (scope == ApprovalScopes.Grant)
            {
                var made = await GrantForTimeAsync(name, decision!.ForTime!.Value, cancellationToken).ConfigureAwait(false);
                grantId = made?.Id;
                confirmed = made is null ? null : Math.Max(1, (int)Math.Ceiling(made.RemainingS / 60.0));
                scope = made is null ? ApprovalScopes.Once : scope;
            }
            if (scope == ApprovalScopes.Always && !await KeepAlwaysAsync(name, toolClass).ConfigureAwait(false))
            {
                scope = ApprovalScopes.Once;
            }
            if (scope == ApprovalScopes.Chat)
            {
                _session.ApproveForChat(name);
            }
            var minutes = scope == ApprovalScopes.Grant ? (int?)decision!.ForTime!.Value.TotalMinutes : null;
            await RecordAsync(request, scope, asked: true, rule, timedOut: decision is null, waited, grantId, minutes, confirmed).ConfigureAwait(false);
            Emit(new ApprovalDecided(request, decision is null ? ApprovalDecision.Deny("No answer in time.")
                : scope == ApprovalScopes.Grant ? ApprovalDecision.ApproveFor(TimeSpan.FromMinutes(confirmed!.Value))
                : scope == ApprovalScopes.Always ? ApprovalDecision.ApproveAlways()
                : scope == ApprovalScopes.Chat ? ApprovalDecision.ApproveForSession()
                : scope == ApprovalScopes.Once ? ApprovalDecision.Approve()
                : decision));
            return new Settled(scope is not null, decision is null, decision?.Reason, scope, grantId);
        }

        /// <summary>Whether the agent allows the standing approval <paramref name="scope"/> (<c>chat</c>, <c>always</c>); any, from an older server.</summary>
        private bool Offers(string scope) => Agent.ProfileOrNull?.Approvals?.Allows(scope) ?? true;

        /// <summary>
        /// The grant that covers a call of the agent's own tool, or null (38 §6): the gateway said so (the first line), and
        /// the device's copy holds that grant, active and unexpired, for this tool or class, and for a stretch of work the
        /// place the call names, where it really leads (the second). A grant the copy does not know is read from the server
        /// once; a copy that still lacks it covers nothing, and the user is asked. <c>LeadsOutside</c>: the real place, when a
        /// link took the call out of the grant.
        /// </summary>
        private async Task<(string? GrantId, string? LeadsOutside)> CoveredByGrantAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            var marked = _covered.TryGetValue(request.CallId, out var mark);
            _covered.Remove(request.CallId); // counts once
            if (!marked || mark.Tool != request.ToolName || !Agent.Grants.Available)
            {
                return (null, null);
            }
            var grantId = mark.GrantId;
            if (!Agent.Grants.Knows(grantId))
            {
                try
                {
                    await Agent.Grants.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return (null, null); // no copy to check against: ask
                }
            }
            var covered = Agent.Grants.Covers(grantId, request.ToolName, request.ToolClass, PlaceOf(request), out var leadsOutside);
            if (covered && Agent.Grants.Find(grantId) is { Kind: GrantKinds.Bypass } bypass
                && (bypass.ConversationId != _session.Id || _mode != ToolMode.Bypass))
            {
                covered = false; // another conversation's Bypass, or a turn that does not run in Bypass
            }
            return (covered ? grantId : null, leadsOutside);
        }

        /// <summary>Where the call acts, as its tool names it (the agent's contract points at an argument the call carries); null when it names none.</summary>
        private GrantPlace? PlaceOf(ApprovalRequest request)
        {
            var entry = Agent.ProfileOrNull?.HostToolContract.FirstOrDefault(e => e.Name == request.ToolName);
            return entry is { TargetArgument: { Length: > 1 } pointer, TargetType: { Length: > 0 } type }
                && GrantPointer.Resolve(request.Arguments, pointer) is { Length: > 0 } value
                ? new GrantPlace(type, value, value) : null;
        }

        /// <summary>
        /// The grant for a time the user picked on the card, as the server confirmed it (it may be shorter); null when it did not (offline,
        /// refused): this call is approved once, and the chat says the grant was not made (L-59: no answer, no grant).
        /// </summary>
        private async Task<AgentGrant?> GrantForTimeAsync(string tool, TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                return await Agent.Grants.GrantForTimeAsync(tool, duration, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await NoticeAsync(new GovernanceNotice
                {
                    Outcome = "grant_failed", Stage = "tool",
                    Message = $"The server did not confirm the grant ({e.Message}); this call alone was allowed.",
                }).ConfigureAwait(false);
                return null;
            }
        }

        /// <summary>What let a call through: an admin's rule, else the scope the user gave (once, chat, always), a grant, or bypass; null if declined.</summary>
        private static string? KindOf(string? scope, bool rule) => scope is null ? null : rule ? RuleKind : scope;

        /// <summary>The approval's transcript entry, and the server's record of it (names only).</summary>
        private async Task RecordAsync(ApprovalRequest request, string? scope, bool asked, bool rule, bool timedOut, long? waitedMs,
            string? grantId = null, int? minutes = null, int? confirmedMinutes = null)
        {
            await _session.AppendAsync(EntryTypes.ApprovalDecision, _id, new
            {
                call_id = request.CallId,
                name = request.ToolName,
                source = request.Source,
                tool_class = request.ToolClass.ToWire(), // so a reopened chat's card says what the tool does
                server = request.Server, // a local MCP server's name, for the card's source line
                approved = scope is not null,
                for_session = asked && scope == ApprovalScopes.Chat, // for older readers
                scope,
                grant_id = grantId,
                grant_minutes = minutes, // the time the person picked
                grant_confirmed_minutes = confirmedMinutes, // what the server confirmed (sdk/43 §6: the same keys as every SDK's)
                asked,
                rule,
                outside_grant = request.LeadsOutside is not null, // a link led out of the grant's places: asked
                timed_out = timedOut,
                wait_ms = waitedMs ?? 0,
            }).ConfigureAwait(false);
            Agent.Reporter.Add(new ApprovalDecidedEvent
            {
                ConversationId = _session.Id, Tool = request.ToolName, WaitMs = waitedMs, Scope = scope, GrantId = grantId,
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

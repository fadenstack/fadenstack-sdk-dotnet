using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Chat;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Provenance;

/// <summary>The transcript entries the module writes (names, counts and outcomes, never content); the TypeScript SDK's are the same.</summary>
public static class ProvenanceEntries
{
    /// <summary>One placement: tool, sink, media type, edit kind, length class, record, layers and reasons.</summary>
    public const string Placed = "provenance.placed";

    /// <summary>Content not placed, as the policy says (<c>on_missing: refuse</c>): a layer it needs was not possible.</summary>
    public const string Refused = "provenance.refused";

    /// <summary>A declared output a tool call did not place through the pipeline.</summary>
    public const string Unmarked = "provenance.unmarked";
}

/// <summary>What the module keeps across calls: its options, the agent's policy, the turns' receipts.</summary>
internal sealed class ProvenanceState
{
    private static readonly TimeSpan PolicyKept = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PolicyRetry = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProvenancePolicy? _policy;
    private DateTimeOffset _until;

    public ProvenanceState(ProvenanceOptions options)
    {
        Options = options;
    }

    public ProvenanceOptions Options { get; }

    public ReceiptKeeper Receipts { get; } = new();

    /// <summary>
    /// The agent's policy, tightened by the options; while the server does not answer, the last one it gave, else the
    /// contract's default.
    /// </summary>
    public async Task<ProvenancePolicy> PolicyAsync(FadenstackAgent? agent, CancellationToken cancellationToken)
    {
        if (_policy is { } kept && DateTimeOffset.UtcNow < _until)
        {
            return kept;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_policy is { } again && DateTimeOffset.UtcNow < _until)
            {
                return again;
            }
            ProvenancePolicy policy;
            var fresh = false;
            try
            {
                policy = agent is null ? new ProvenancePolicy() : await Options.Backend.GetPolicyAsync(agent, cancellationToken).ConfigureAwait(false);
                fresh = agent is not null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning($"Provenance: the policy could not be read ({e.GetType().Name}); the last known or the default applies.");
                if (_policy is { } last)
                {
                    _until = DateTimeOffset.UtcNow + PolicyRetry;
                    return last;
                }
                policy = new ProvenancePolicy();
            }
            _policy = Options.Tighten(policy);
            _until = DateTimeOffset.UtcNow + (fresh ? PolicyKept : PolicyRetry);
            return _policy;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// The receipts of the turns, by turn: every receipt of the turn, and the one of its latest model call -- the call a
/// tool call that runs now came from. A call without one (tool calls only) leaves none for its tools.
/// </summary>
internal sealed class ReceiptKeeper
{
    private const int MaxTurns = 256;

    private readonly Dictionary<string, (List<ProvenanceReceiptContent> All, ProvenanceReceiptContent? Latest)> _turns = new();
    private readonly Queue<string> _order = new();

    public void BeginCall(string? turn)
    {
        if (turn is null)
        {
            return;
        }
        lock (_turns)
        {
            _turns[turn] = (Turn(turn).All, null);
        }
    }

    public void Keep(string? turn, ProvenanceReceiptContent receipt)
    {
        if (turn is null)
        {
            return;
        }
        lock (_turns)
        {
            var (all, _) = Turn(turn);
            all.Add(receipt);
            _turns[turn] = (all, receipt);
        }
    }

    /// <summary>The receipt of the turn's latest model call.</summary>
    public ProvenanceReceiptContent? Latest(string turn)
    {
        lock (_turns)
        {
            return _turns.TryGetValue(turn, out var t) ? t.Latest : null;
        }
    }

    public IReadOnlyList<ProvenanceReceiptContent> Of(string turn)
    {
        lock (_turns)
        {
            return _turns.TryGetValue(turn, out var t) ? t.All.ToList() : new List<ProvenanceReceiptContent>();
        }
    }

    private (List<ProvenanceReceiptContent> All, ProvenanceReceiptContent? Latest) Turn(string turn)
    {
        if (_turns.TryGetValue(turn, out var known))
        {
            return known;
        }
        _order.Enqueue(turn);
        while (_order.Count > MaxTurns)
        {
            _turns.Remove(_order.Dequeue());
        }
        var made = (new List<ProvenanceReceiptContent>(), (ProvenanceReceiptContent?)null);
        _turns[turn] = made;
        return made;
    }
}

/// <summary>Keeps the receipts the gateway sends with each answer (<c>faden.provenance</c>), by turn.</summary>
internal sealed class ReceiptStage : DelegatingChatClient
{
    private readonly ReceiptKeeper _receipts;

    public ReceiptStage(IChatClient inner, ReceiptKeeper receipts) : base(inner)
    {
        _receipts = receipts;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turn = TurnOf(options);
        _receipts.BeginCall(turn);
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (response.GetProvenanceReceipt() is { } receipt)
        {
            _receipts.Keep(turn, receipt);
        }
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = TurnOf(options);
        _receipts.BeginCall(turn);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var receipt in update.Contents.OfType<ProvenanceReceiptContent>())
            {
                _receipts.Keep(turn, receipt);
            }
            yield return update;
        }
    }

    private static string? TurnOf(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(FadenstackChatOptions.TurnId, out var turn) == true ? turn?.ToString() : null;
}

/// <summary>
/// After a tool that declares outputs (<c>[Emits]</c>) ran: each declared argument that carried content must have gone
/// through <see cref="IProvenance.PlaceAsync"/>. One that did not is recorded as <c>provenance.unmarked</c> and reported
/// without a record (<c>sink_kind</c> <c>unmarked</c>, no layers); in development mode the call fails, so an integrator
/// cannot miss it. Content exempt from marking (the policy's internal switch, an exempt edit kind, code, a label) is
/// left alone, and so is a call whose declaration says it writes no content (<see cref="ToolOutput.EditKindOf"/> null).
/// </summary>
internal sealed class OutputCheckStage : IToolStage
{
    /// <summary>How long the report of unmarked content may take before the call goes on without it.</summary>
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private readonly ProvenanceState _state;

    public OutputCheckStage(ProvenanceState state)
    {
        _state = state;
    }

    public async Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
    {
        // Made now, so the tool gets the same one and the placements it makes are counted here.
        var provenance = invocation.GetService<IProvenance>();
        var result = await next(invocation, cancellationToken).ConfigureAwait(false);
        if (invocation.Tool.Outputs.Count == 0 || provenance is null)
        {
            return result;
        }
        var policy = await provenance.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var unmarked = Unplaced(invocation, provenance.Placements).Where(o => !IsExempt(o, invocation, policy)).ToList();
        if (unmarked.Count == 0)
        {
            return result;
        }
        foreach (var output in unmarked)
        {
            var text = TextOf(invocation.Arguments.TryGetValue(output.Argument, out var value) ? value : null);
            var kind = output.EditKindFor(new Dictionary<string, object?>(invocation.Arguments)) ?? EditKind.Generated;
            var tokens = _state.Options.CountTokens(text);
            var length = tokens < policy.ShortTextTokens ? LengthClass.Short : tokens < policy.ShortTextTokens * 5 ? LengthClass.Medium : LengthClass.Long;
            var reported = await ReportAsync(invocation, new PlacementReport
            {
                Tool = invocation.Tool.Name, SinkKind = "unmarked", MediaType = output.MediaType, EditKind = kind, LengthClass = length,
            }, cancellationToken).ConfigureAwait(false);
            await invocation.RecordAsync(ProvenanceEntries.Unmarked, new
            {
                call_id = invocation.Call.CallId, tool = invocation.Tool.Name, argument = output.Argument, media_type = output.MediaType,
                edit_kind = kind.ToWire(), length_class = length.ToString().ToLowerInvariant(), reported,
            }).ConfigureAwait(false);
        }
        var names = string.Join(", ", unmarked.Select(o => $"'{o.Argument}'"));
        Trace.TraceWarning($"Provenance: '{invocation.Tool.Name}' declares {names} as content for people and did not place it through IProvenance.PlaceAsync.");
        if (_state.Options.DevelopmentMode)
        {
            throw new ProvenanceUnmarkedException(
                $"'{invocation.Tool.Name}' declares {names} as content for people (Emits) but did not place it through IProvenance.PlaceAsync, so it is not marked.",
                unmarked);
        }
        return result;
    }

    /// <summary>Whether a declared output needs no mark: the policy's internal switch, an exempt edit kind, code, a label.</summary>
    private static bool IsExempt(ToolOutput output, ToolInvocation invocation, ProvenancePolicy policy)
    {
        if (policy.InternalTechnical || MediaTypes.IsCode(output.MediaType)
            || (output.EditKindFor(new Dictionary<string, object?>(invocation.Arguments)) is { } kind && policy.ExemptEditKinds.Contains(kind)))
        {
            return true;
        }
        var value = invocation.Arguments.TryGetValue(output.Argument, out var v) ? v : null;
        return value is string or JsonElement { ValueKind: JsonValueKind.String } && Labels.IsLabel(TextOf(value), output.MediaType);
    }

    /// <summary>An argument's content as text: a string as it is, anything else as JSON.</summary>
    private static string TextOf(object? value) => value switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => JsonSerializer.Serialize(value),
    };

    /// <summary>Reports unmarked content; whether the server took the report.</summary>
    private async Task<bool> ReportAsync(ToolInvocation invocation, PlacementReport report, CancellationToken cancellationToken)
    {
        if (invocation.Agent is not { } agent)
        {
            return false;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReportTimeout);
        try
        {
            await _state.Options.Backend.ReportPlacementAsync(agent, report, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning($"Provenance: the unmarked content of '{report.Tool}' was not reported ({e.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// The declared outputs that carried content and were not placed: by argument name first, then in order. An output
    /// whose <see cref="ToolOutput.EditKindOf"/> says this call writes no content is none.
    /// </summary>
    internal static List<ToolOutput> Unplaced(ToolInvocation invocation, IReadOnlyList<Placement> placements)
    {
        var named = new HashSet<string>(placements.Where(p => p.Argument is not null).Select(p => p.Argument!), StringComparer.Ordinal);
        var unnamed = placements.Count(p => p.Argument is null);
        var unplaced = new List<ToolOutput>();
        var arguments = new Dictionary<string, object?>(invocation.Arguments);
        foreach (var output in invocation.Tool.Outputs)
        {
            if (!invocation.Arguments.TryGetValue(output.Argument, out var value) || IsEmpty(value) || named.Contains(output.Argument)
                || !output.IsContentFor(arguments))
            {
                continue;
            }
            if (unnamed > 0)
            {
                unnamed--;
                continue;
            }
            unplaced.Add(output);
        }
        return unplaced;
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } e => string.IsNullOrWhiteSpace(e.GetString()),
        JsonElement { ValueKind: JsonValueKind.Array } e => e.GetArrayLength() == 0,
        _ => false,
    };
}

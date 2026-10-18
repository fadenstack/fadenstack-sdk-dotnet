using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Chat;
using Fadenstack.Models;
using Microsoft.Extensions.AI;

namespace Fadenstack.Provenance;

/// <summary>The transcript entries the module writes (names, counts and outcomes, never content).</summary>
public static class ProvenanceEntries
{
    /// <summary>One placement: tool, sink, media type, edit kind, length class, record, layers and reasons.</summary>
    public const string Placed = "provenance.placed";

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

    /// <summary>The agent's policy, tightened by the options; the contract's default while the server does not answer.</summary>
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
                Trace.TraceWarning($"Provenance: the policy could not be read ({e.GetType().Name}); the default applies.");
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
/// through <see cref="IProvenance.PlaceAsync"/>. One that did not is recorded as <c>provenance.unmarked</c>; in
/// development mode the call fails, so an integrator cannot miss it.
/// </summary>
internal sealed class OutputCheckStage : IToolStage
{
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
        var unmarked = Unplaced(invocation, provenance.Placements);
        if (unmarked.Count == 0)
        {
            return result;
        }
        foreach (var output in unmarked)
        {
            await invocation.RecordAsync(ProvenanceEntries.Unmarked, new
            {
                call_id = invocation.Call.CallId, tool = invocation.Tool.Name, argument = output.Argument, media_type = output.MediaType,
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

    /// <summary>The declared outputs that carried content and were not placed: by argument name first, then in order.</summary>
    internal static List<ToolOutput> Unplaced(ToolInvocation invocation, IReadOnlyList<Placement> placements)
    {
        var named = new HashSet<string>(placements.Where(p => p.Argument is not null).Select(p => p.Argument!), StringComparer.Ordinal);
        var unnamed = placements.Count(p => p.Argument is null);
        var unplaced = new List<ToolOutput>();
        foreach (var output in invocation.Tool.Outputs)
        {
            if (!invocation.Arguments.TryGetValue(output.Argument, out var value) || IsEmpty(value) || named.Contains(output.Argument))
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

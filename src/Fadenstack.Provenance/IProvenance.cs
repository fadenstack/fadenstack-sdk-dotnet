using Fadenstack.Agents;
using Fadenstack.Chat;
using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>
/// How a tool places content a model wrote: <see cref="PlaceAsync"/> marks it for its sink and records the placement.
/// A tool gets it as a parameter (bound by the SDK, not shown to the model) once <see cref="ProvenanceModule"/> is on
/// the builder: <c>public Task&lt;string&gt; InsertReplyAsync(string text, IProvenance provenance, CancellationToken ct)</c>.
/// One per tool call.
/// </summary>
[ToolService]
public interface IProvenance
{
    /// <summary>
    /// Places <paramref name="content"/>: an exempt edit, code or a label is inserted and recorded as exempt; otherwise
    /// the server signs a record of it (its hash only), the markers prepare it, <paramref name="insert"/> puts it into
    /// the host, the markers mark what landed, and the placement is reported (layers, reason codes, never content).
    /// With the policy <c>refuse</c>, content that cannot get a layer it needs is not inserted:
    /// <see cref="ProvenanceRefusedException"/>, which the model and the user are told about.
    /// </summary>
    /// <param name="content">What to place, and where.</param>
    /// <param name="insert">The host's own insert, with the content as the markers prepared it; it may return what landed
    /// (a handle, a way to read it back) or null.</param>
    /// <param name="cancellationToken">The turn's token.</param>
    Task<Placement> PlaceAsync(MarkableContent content, Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert,
        CancellationToken cancellationToken = default);

    /// <summary>The policy the placements follow: the agent's, tightened by the module's options.</summary>
    Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>The receipt of the model answer this tool call came from; null when it had none (tool calls only).</summary>
    ProvenanceReceiptContent? Receipt { get; }

    /// <summary>The placements of this tool call so far.</summary>
    IReadOnlyList<Placement> Placements { get; }
}

/// <summary>Shorter ways to place content, for inserts that return nothing or their own result.</summary>
public static class ProvenanceExtensions
{
    public static Task<Placement> PlaceAsync(this IProvenance provenance, MarkableContent content, Func<MarkableContent, Task> insert,
        CancellationToken cancellationToken = default) =>
        provenance.PlaceAsync(content, async (marked, _) => { await insert(marked).ConfigureAwait(false); return null; }, cancellationToken);

    public static Task<Placement> PlaceAsync(this IProvenance provenance, MarkableContent content,
        Func<MarkableContent, CancellationToken, Task> insert, CancellationToken cancellationToken = default) =>
        provenance.PlaceAsync(content, async (marked, ct) => { await insert(marked, ct).ConfigureAwait(false); return null; }, cancellationToken);

    /// <summary>Places the content and returns what the host's insert returned: <c>marked => editor.InsertAsync(marked.Text!)</c>.</summary>
    public static async Task<T> PlaceAsync<T>(this IProvenance provenance, MarkableContent content, Func<MarkableContent, Task<T>> insert,
        CancellationToken cancellationToken = default)
    {
        var result = default(T)!;
        await provenance.PlaceAsync(content, async (marked, _) => { result = await insert(marked).ConfigureAwait(false); return null; },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static async Task<T> PlaceAsync<T>(this IProvenance provenance, MarkableContent content,
        Func<MarkableContent, CancellationToken, Task<T>> insert, CancellationToken cancellationToken = default)
    {
        var result = default(T)!;
        await provenance.PlaceAsync(content, async (marked, ct) => { result = await insert(marked, ct).ConfigureAwait(false); return null; },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Adds the module: <c>builder.UseProvenance(p =&gt; p.AddMarker(new CrmNoteMarker()))</c>, the same as
    /// <c>builder.Use(new ProvenanceModule(...))</c>.
    /// </summary>
    public static AgentBuilder UseProvenance(this AgentBuilder builder, Action<ProvenanceOptions>? configure = null) =>
        builder.Use(new ProvenanceModule(configure));
}

/// <summary>
/// The content was not placed: a layer it needs cannot be applied there, and the agent's policy refuses in that
/// case. The message says why, in words for the model; the user gets a notice.
/// </summary>
public sealed class ProvenanceRefusedException : FadenstackException
{
    public ProvenanceRefusedException(string message, IReadOnlyList<MarkOutcome> missing, Placement placement) : base(message)
    {
        Missing = missing;
        Placement = placement;
    }

    /// <summary>The layers that could not be applied, with why.</summary>
    public IReadOnlyList<MarkOutcome> Missing { get; }

    public Placement Placement { get; }
}

/// <summary>
/// In development mode: a tool said it writes content for people (<c>[Emits]</c>) and returned without placing it
/// through <see cref="IProvenance.PlaceAsync"/>, so nothing marked it.
/// </summary>
public sealed class ProvenanceUnmarkedException : FadenstackException
{
    public ProvenanceUnmarkedException(string message, IReadOnlyList<ToolOutput> unmarked) : base(message)
    {
        Unmarked = unmarked;
    }

    public IReadOnlyList<ToolOutput> Unmarked { get; }
}

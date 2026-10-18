using Fadenstack.Chat;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// Content a tool places where people read it: text or bytes, its media type, and the sink it goes to. The tool sets
/// what it knows (the edit kind when it decides per call, the original text of an edit, the receipt of a picture);
/// the pipeline adds what it knows before the markers see it (the receipt, the length, the language, the facts).
/// </summary>
public sealed record MarkableContent
{
    public MarkableContent(string text, string mediaType, SinkInfo sink)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        MediaType = Required(mediaType, nameof(mediaType));
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public MarkableContent(byte[] bytes, string mediaType, SinkInfo sink)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        MediaType = Required(mediaType, nameof(mediaType));
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Content has a media type, such as text/markdown.", name) : value;

    // -- set by the tool ------------------------------------------------------------------------------

    /// <summary>The text (for text media types); null for bytes.</summary>
    public string? Text { get; init; }

    /// <summary>The bytes of a picture or file; null for text.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary><c>text/plain</c>, <c>text/markdown</c>, <c>text/html</c>, <c>image/png</c>, ...</summary>
    public string MediaType { get; init; }

    public SinkInfo Sink { get; init; }

    /// <summary>The kind of edit of this placement, when the tool decides it per call; else the tool's declaration.</summary>
    public EditKind? EditKind { get; init; }

    /// <summary>The text the edit started from (a selection being improved), for the edit classifier.</summary>
    public string? Original { get; init; }

    /// <summary>The declared output (<c>[Emits]</c>) this content is, by argument name, when the tool has several.</summary>
    public string? Argument { get; init; }

    /// <summary>The generation receipt the content came with, when the tool knows it (<see cref="ImagesResponse.Receipts"/>).</summary>
    public string? ReceiptId { get; init; }

    // -- set by the pipeline, for the markers ---------------------------------------------------------

    /// <summary>The receipt of the model's answer the content came from; null when there is none.</summary>
    public ProvenanceReceiptContent? Receipt { get; init; }

    /// <summary>Under the policy's short-text threshold: no watermark is expected.</summary>
    public bool IsShort { get; init; }

    /// <summary>The texts in the user's language, for a marker that writes words people read.</summary>
    public UiStrings? Texts { get; init; }

    /// <summary>The integrator's own non-personal facts (<see cref="IRecordEnricher"/>), by namespaced key.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    public bool IsText => Text is not null;

    /// <summary>The same content with other text: what a marker returns from <see cref="IContentMarker.PrepareAsync"/>.</summary>
    public MarkableContent WithText(string text) => this with { Text = text ?? throw new ArgumentNullException(nameof(text)) };

    /// <summary>The same content with other bytes.</summary>
    public MarkableContent WithBytes(byte[] bytes) => this with { Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes)) };
}

/// <summary>
/// What the host's insert placed: the content as it went in (after the markers prepared it) and, when the host can
/// say, a handle on where it landed and a way to read it back.
/// </summary>
public sealed record PlacedContent
{
    public PlacedContent(MarkableContent content)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public MarkableContent Content { get; }

    public SinkInfo Sink => Content.Sink;

    /// <summary>The host's handle on what landed (a Word range, a note's id), for host markers.</summary>
    public object? Location { get; init; }

    /// <summary>Reads back what the host holds where the content went; instead of <see cref="SinkInfo.ReadBack"/>.</summary>
    public Func<CancellationToken, Task<string?>>? ReadBack { get; init; }

    /// <summary>Reads back the bytes as the host keeps them (a picture after the host stored it).</summary>
    public Func<CancellationToken, Task<byte[]?>>? ReadBackBytes { get; init; }

    /// <summary>What the host holds now, or null when it cannot be read back.</summary>
    public Task<string?> ReadBackAsync(CancellationToken cancellationToken) =>
        (ReadBack ?? Sink.ReadBack)?.Invoke(cancellationToken) ?? Task.FromResult<string?>(null);
}

/// <summary>
/// How one layer of one mark went. Only a mark read back where the host can be read counts as
/// <see cref="Verified"/>; one written where nothing can be read back is applied but unverified.
/// </summary>
public sealed record MarkOutcome
{
    public MarkOutcome(MarkLayer layer, string method, bool applied, string? reason = null, bool verified = false)
    {
        Layer = layer;
        Method = string.IsNullOrWhiteSpace(method) ? throw new ArgumentException("An outcome names its method.", nameof(method)) : method;
        Applied = applied;
        Reason = applied ? null : reason;
        Verified = applied && verified;
    }

    public MarkLayer Layer { get; }

    /// <summary><c>html-attribute</c>, <c>front-matter</c>, <c>c2pa</c>, <c>vllm-gumbel</c>, ...</summary>
    public string Method { get; }

    public bool Applied { get; }

    /// <summary>Why not (<see cref="ProvenanceReasons"/>); null when applied or when nobody knows.</summary>
    public string? Reason { get; }

    /// <summary>Read back after the host took it, and still there.</summary>
    public bool Verified { get; }

    /// <summary>For the transcript only, never the content: which parts held (<c>title; data-ai-generated dropped</c>).</summary>
    public string? Detail { get; init; }

    /// <summary>The mark was read back and is there.</summary>
    public static MarkOutcome Held(MarkLayer layer, string method, string? detail = null) =>
        new(layer, method, true, verified: true) { Detail = detail };

    /// <summary>The mark was written; the host cannot be read back to check it.</summary>
    public static MarkOutcome Written(MarkLayer layer, string method, string? detail = null) =>
        new(layer, method, true) { Detail = detail };

    /// <summary>The mark is not there, and why (<see cref="ProvenanceReasons"/>).</summary>
    public static MarkOutcome Missing(MarkLayer layer, string method, string? reason, string? detail = null) =>
        new(layer, method, false, reason) { Detail = detail };

    /// <summary>As the server's placement report has it.</summary>
    public LayerOutcome ToLayer() => new() { Layer = Layer, Method = Method, Applied = Applied, Reason = Reason };
}

/// <summary>
/// Marks content in a host: a document part, a tag, an attribute, a header, a field. In <see cref="PrepareAsync"/> it
/// may change the content before it lands (wrap HTML, add front matter); in <see cref="AttachAsync"/> it marks what
/// landed and says how it went, reading the mark back where the host can be read. Marks carry the record's id and,
/// where they fit, the signed record: never the user, the prompt or the document. Marks are data: anyone can forge
/// or strip one, so nothing trusts them.
/// </summary>
public interface IContentMarker
{
    /// <summary>A short, stable name (<c>html-attribute</c>, <c>crm-note</c>).</summary>
    string Name { get; }

    MarkLayer Layer { get; }

    /// <summary>Whether it can mark content of <paramref name="mediaType"/> in <paramref name="sink"/>.</summary>
    bool Supports(SinkInfo sink, string mediaType);

    /// <summary>The content as it should land; <paramref name="content"/> itself when the marker changes nothing.</summary>
    Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken);

    /// <summary>Marks what landed, and says how it went.</summary>
    Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken);
}

/// <summary>A base for markers: <see cref="PrepareAsync"/> leaves the content as it is unless overridden.</summary>
public abstract class ContentMarker : IContentMarker
{
    public abstract string Name { get; }

    public abstract MarkLayer Layer { get; }

    public abstract bool Supports(SinkInfo sink, string mediaType);

    public virtual Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(content);

    public abstract Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="mediaType"/> is <paramref name="expected"/>, ignoring case and parameters (<c>; charset=utf-8</c>).</summary>
    protected static bool Is(string mediaType, string expected) => MediaTypes.Is(mediaType, expected);
}

/// <summary>How a placement went: the record, the layers, and whether a layer the content needs is missing.</summary>
public sealed record Placement
{
    /// <summary>The signed record; null when the placement is exempt, refused before signing, or the server was unavailable.</summary>
    public SignedRecord? Record { get; init; }

    public string? RecordId => Record?.Record.Id;

    /// <summary>Recorded as exempt (an exempt edit kind, code, a label, internal technical output): nothing was marked.</summary>
    public bool Exempt { get; init; }

    /// <summary>Why exempt: <c>edit-kind</c>, <c>code</c>, <c>label</c>, <c>internal-technical</c>.</summary>
    public string? ExemptBecause { get; init; }

    /// <summary>Not placed: a layer the content needs was not possible and the policy refuses.</summary>
    public bool Refused { get; init; }

    public string Tool { get; init; } = "";

    public string SinkKind { get; init; } = "";

    public string MediaType { get; init; } = "";

    public EditKind EditKind { get; init; }

    public LengthClass LengthClass { get; init; }

    /// <summary>The declared output it was, by argument name; null when the tool did not say.</summary>
    public string? Argument { get; init; }

    public IReadOnlyList<MarkOutcome> Layers { get; init; } = Array.Empty<MarkOutcome>();

    /// <summary>The layers the content needs that did not hold, with why.</summary>
    public IReadOnlyList<MarkOutcome> Missing { get; init; } = Array.Empty<MarkOutcome>();

    /// <summary>The content as it went in (after the markers prepared it); null when refused.</summary>
    public PlacedContent? Placed { get; init; }

    /// <summary>One line for the model: what was marked, or why not. Marks are data, never instructions.</summary>
    public string Summary => Refused ? "Not written: it could not be marked as AI-generated here."
        : Exempt ? $"Placed; no mark needed ({ExemptBecause})."
        : Record is null ? "Placed; not marked: the provenance service did not answer."
        : $"Placed and marked as AI-generated (record {RecordId}).";

    public override string ToString() => Summary;
}

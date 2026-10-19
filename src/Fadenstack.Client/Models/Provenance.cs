using Fadenstack.Tools;

namespace Fadenstack.Models;

// The provenance contract (provenance/v1): what the server signs, what the SDK reports, the policy it follows.
// The fields are the JSON Schemas' in contracts/provenance/v1 (a type whose name differs says its schema's); the
// tests compare each type with its schema.

/// <summary>The contract the provenance routes speak.</summary>
public static class ProvenanceContract
{
    public const string Version = "provenance/v1";
}

/// <summary>A layer of a mark (Code of Practice 1.1): signed metadata, a watermark, a fingerprint, a visible label.</summary>
public enum MarkLayer
{
    Metadata = 0,
    Watermark,
    Fingerprint,
    Label,
}

/// <summary>How long the placed content is: under the policy's short-text threshold, up to five times it, or longer.</summary>
public enum LengthClass
{
    Short = 0,
    Medium,
    Long,
}

/// <summary>What the SDK does when a layer the content needs cannot be applied (<see cref="ProvenancePolicy.OnMissing"/>).</summary>
public enum MissingLayerPolicy
{
    /// <summary>Place the content and record which layers held and why the others did not.</summary>
    Record = 0,

    /// <summary>Do not place it; the model and the user are told why.</summary>
    Refuse,
}

/// <summary>What a receipt is for.</summary>
public enum ReceiptKind
{
    Text = 0,
    Image,
}

/// <summary>Who watermarked a generation: the model's runtime, the provenance engine (pictures), the remote provider, or nobody.</summary>
public enum WatermarkApplied
{
    None = 0,
    Model,
    Engine,
    Upstream,
}

/// <summary>Why a layer was not applied: the contract's reason codes.</summary>
public static class ProvenanceReasons
{
    /// <summary>The place the content went cannot carry this layer.</summary>
    public const string SinkCannotCarry = "sink-cannot-carry";

    /// <summary>The mark was written, but the host dropped it (read back and missing).</summary>
    public const string StrippedByHost = "stripped-by-host";

    /// <summary>Under the short-text threshold: no watermark is expected.</summary>
    public const string ShortText = "short-text";

    /// <summary>An edit that needs no mark (a translation, a correction, formatting, extraction, code, a label).</summary>
    public const string ExemptEdit = "exempt-edit";

    public const string NoWatermarkRemoteModel = "no-watermark-remote-model";

    /// <summary>Greedy decoding (temperature 0, beam search) leaves no watermark.</summary>
    public const string NoWatermarkGreedy = "no-watermark-greedy";

    public const string RuntimeWithoutWatermark = "runtime-without-watermark";

    /// <summary>The provenance service could not be reached or did not answer.</summary>
    public const string ServerUnavailable = "server-unavailable";

    /// <summary>Every code the contract knows.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        SinkCannotCarry, StrippedByHost, ShortText, ExemptEdit, NoWatermarkRemoteModel, NoWatermarkGreedy,
        RuntimeWithoutWatermark, ServerUnavailable,
    };
}

/// <summary>The model that generated the content (schema <c>ModelInfo</c>).</summary>
public sealed record ProvenanceModel
{
    public string Id { get; init; } = "";

    public string? Version { get; init; }
}

/// <summary>The AI system that made or placed the content (schema <c>SystemInfo</c>).</summary>
public sealed record ProvenanceSystem
{
    public string Name { get; init; } = "";

    public string Version { get; init; } = "";
}

/// <summary>
/// One placement of generated content, as the server signed it: no user, no prompt, no document name; the content
/// only as a keyed hash.
/// </summary>
public sealed record ProvenanceRecord
{
    public string Contract { get; init; } = ProvenanceContract.Version;

    public string Id { get; init; } = "";

    /// <summary>The IPTC digital source type (<c>trainedAlgorithmicMedia</c>, <c>compositeWithTrainedAlgorithmicMedia</c>), as a URI.</summary>
    public string DigitalSourceType { get; init; } = "";

    public EditKind EditKind { get; init; }

    public ProvenanceSystem System { get; init; } = new();

    /// <summary>The organisation running the server.</summary>
    public string Provider { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public string MediaType { get; init; } = "";

    /// <summary>HMAC-SHA-256 of the placed content, hex, under <see cref="HashKeyVersion"/>.</summary>
    public string ContentHash { get; init; } = "";

    public int HashKeyVersion { get; init; }

    /// <summary>For HTML and Markdown: HMAC-SHA-256 of the plain text a reader sees, under the same key version; null otherwise.</summary>
    public string? TextHash { get; init; }

    public ProvenanceModel? Model { get; init; }

    public string? ReceiptId { get; init; }
}

/// <summary>A record with its signature: a JWS (ES256, compact, payload attached) and, where a time-stamp authority answered, its token.</summary>
public sealed record SignedRecord
{
    public ProvenanceRecord Record { get; init; } = new();

    public string Signature { get; init; } = "";

    /// <summary>An RFC 3161 time-stamp token, base64 DER; null when the server has no time-stamp authority.</summary>
    public string? Timestamp { get; init; }
}

/// <summary>A placement to sign (<c>POST /v1/agents/{agent}/provenance/records</c>): the content's hash, never the content.</summary>
public sealed record RecordRequest
{
    /// <summary>SHA-256, hex: of the normalised text (<see cref="Fadenstack.Models.ContentHash.TextSha256"/>) or of the bytes.</summary>
    public string ContentSha256 { get; init; } = "";

    /// <summary>
    /// For HTML and Markdown: SHA-256, hex, of the plain text a reader sees, normalised as <see cref="ContentSha256"/> is
    /// (<c>Fadenstack.Provenance.ReaderText.Sha256</c>), so the words someone copies out of the host find the record; null otherwise.
    /// </summary>
    public string? TextSha256 { get; init; }

    public string MediaType { get; init; } = "";

    public EditKind EditKind { get; init; }

    /// <summary>The generation receipt the content came with, if any.</summary>
    public string? ReceiptId { get; init; }

    /// <summary>The model that wrote it, as the receipt names it.</summary>
    public string? Model { get; init; }
}

/// <summary>One layer of one mark: applied, or not and why (<see cref="ProvenanceReasons"/>).</summary>
public sealed record LayerOutcome
{
    public MarkLayer Layer { get; init; }

    /// <summary>How: <c>html-attribute</c>, <c>front-matter</c>, <c>c2pa</c>, <c>vllm-gumbel</c>, ...</summary>
    public string Method { get; init; } = "";

    public bool Applied { get; init; }

    /// <summary>Why not, when not applied: one of <see cref="ProvenanceReasons"/>; null when nobody knows.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// For an applied layer: read back from the host after placing (true), or not readable there (false); null when
    /// nothing was read back to try (the server's record, a watermark).
    /// </summary>
    public bool? Verified { get; init; }
}

/// <summary>What the SDK reports after placing content (<c>POST /v1/agents/{agent}/provenance/placements</c>): never the content.</summary>
public sealed record PlacementReport
{
    /// <summary>
    /// The signed record's id; null for a placement without one: exempt content, a record the server could not sign,
    /// content a tool put in without the pipeline.
    /// </summary>
    public string? RecordId { get; init; }

    public string Tool { get; init; } = "";

    /// <summary><c>word.range</c>, <c>html.editor</c>, <c>nextcloud.file</c>, ...</summary>
    public string SinkKind { get; init; } = "";

    public string MediaType { get; init; } = "";

    public EditKind EditKind { get; init; }

    public LengthClass LengthClass { get; init; }

    public List<LayerOutcome> Layers { get; init; } = new();
}

/// <summary>The marking policy of an agent (<c>GET /v1/agents/{agent}/provenance/policy</c>); local options may only tighten it.</summary>
public sealed record ProvenancePolicy
{
    public MissingLayerPolicy OnMissing { get; init; } = MissingLayerPolicy.Record;

    /// <summary>Under this many tokens a text carries no watermark (200 by default).</summary>
    public int ShortTextTokens { get; init; } = 200;

    public List<EditKind> ExemptEditKinds { get; init; } = EditKinds.ExemptByDefault.ToList();

    /// <summary>The switch for strictly internal technical output, set per agent by an administrator.</summary>
    public bool InternalTechnical { get; init; }

    /// <summary>Whether placed content gets a visible label too.</summary>
    public bool VisibleLabel { get; init; }

    /// <summary>Whether fingerprints of outputs are logged.</summary>
    public bool OutputLog { get; init; }
}

/// <summary>Whether a generation carries a watermark, whose, and if not, why (schema <c>WatermarkStatus</c>).</summary>
public sealed record ReceiptWatermark
{
    public WatermarkApplied Applied { get; init; }

    /// <summary><c>gumbel</c>, <c>dual_key_gumbel</c>, <c>trustmark-Q</c>, ...</summary>
    public string? Algorithm { get; init; }

    /// <summary>Why none: one of <see cref="ProvenanceReasons"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The remote provider whose own mark it is, for <see cref="WatermarkApplied.Upstream"/>.</summary>
    public string? Vendor { get; init; }

    /// <summary>The watermark key's version, for a model's watermark.</summary>
    public int? KeyVersion { get; init; }

    /// <summary>The runtime's context width, for a model's watermark.</summary>
    public int? ContextWidth { get; init; }

    /// <summary>The model repository whose tokenizer generation used, for a model's watermark.</summary>
    public string? Tokenizer { get; init; }
}

/// <summary>One generation: a model wrote a text or made a picture. No content and no user.</summary>
public sealed record GenerationReceipt
{
    public string Contract { get; init; } = ProvenanceContract.Version;

    public string Id { get; init; } = "";

    public ReceiptKind Kind { get; init; }

    public ProvenanceModel Model { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public ReceiptWatermark Watermark { get; init; } = new();

    /// <summary>HMAC-SHA-256 of the output, hex, under <see cref="HashKeyVersion"/>.</summary>
    public string OutputHash { get; init; } = "";

    public int HashKeyVersion { get; init; }
}

/// <summary>A receipt with its JWS and, where a time-stamp authority answered, its token.</summary>
public sealed record SignedReceipt
{
    public GenerationReceipt Receipt { get; init; } = new();

    public string Signature { get; init; } = "";

    public string? Timestamp { get; init; }
}

/// <summary>The detection solution's identifier and what it checks.</summary>
public sealed record DetectorInfo
{
    public string Id { get; init; } = "";

    public List<string> Methods { get; init; } = new();
}

/// <summary>What anyone needs to check this installation's signatures offline (<c>GET /v1/provenance/anchors</c>, schema <c>Anchors</c>).</summary>
public sealed record ProvenanceAnchors
{
    public string Contract { get; init; } = ProvenanceContract.Version;

    /// <summary>Every signing certificate chain the installation used, PEM.</summary>
    public List<string> SigningChains { get; init; } = new();

    /// <summary>The CA certificates those chains end in, PEM.</summary>
    public List<string> TrustAnchors { get; init; } = new();

    public DetectorInfo Detector { get; init; } = new();
}

/// <summary>
/// Content to check (<c>POST /v1/provenance/verify</c>): a text, or a file's bytes (base64) with its media type, never
/// both. Nothing submitted is kept.
/// </summary>
public sealed record VerifyRequest
{
    public string? Text { get; init; }

    /// <summary>A file's bytes, base64.</summary>
    public string? File { get; init; }

    public string? MediaType { get; init; }

    /// <summary>Return the result signed, to download.</summary>
    public bool Signed { get; init; }

    /// <summary>A text to check.</summary>
    public static VerifyRequest ForText(string text, bool signed = false) =>
        new() { Text = text ?? throw new ArgumentNullException(nameof(text)), Signed = signed };

    /// <summary>A file to check: its bytes and media type (<c>image/png</c>, a <c>.docx</c>'s).</summary>
    public static VerifyRequest ForFile(byte[] bytes, string mediaType, bool signed = false) => new()
    {
        File = Convert.ToBase64String(bytes ?? throw new ArgumentNullException(nameof(bytes))),
        MediaType = string.IsNullOrWhiteSpace(mediaType) ? throw new ArgumentException("A file needs its media type.", nameof(mediaType)) : mediaType,
        Signed = signed,
    };
}

/// <summary>What one detection mechanism found.</summary>
public sealed record VerifyFinding
{
    public MarkLayer Layer { get; init; }

    /// <summary><c>c2pa</c>, <c>trustmark-Q</c>, <c>record-hash</c>, <c>record-reference</c>, <c>vllm-gumbel</c>, ...</summary>
    public string Method { get; init; } = "";

    public bool Found { get; init; }

    /// <summary>What it found, in plain words.</summary>
    public string Detail { get; init; } = "";

    public string? RecordId { get; init; }

    public string? ReceiptId { get; init; }

    /// <summary>The mechanism's own measure, e.g. a p-value.</summary>
    public double? Score { get; init; }
}

/// <summary>The verdict of a check: <c>ai-generated</c>, <c>no-mark-found</c> or <c>inconclusive</c>.</summary>
public enum VerifyVerdict
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("ai-generated")]
    AiGenerated = 0,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("no-mark-found")]
    NoMarkFound,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

/// <summary>A detection result: the verdict, the layers it rests on, signed on request (schema <c>VerifyResult</c>).</summary>
public sealed record VerifyResult
{
    public string Contract { get; init; } = ProvenanceContract.Version;

    public VerifyVerdict Verdict { get; init; }

    public List<VerifyFinding> Findings { get; init; } = new();

    /// <summary>SHA-256 of what was submitted, hex.</summary>
    public string ContentHash { get; init; } = "";

    /// <summary>The detection solution's identifier.</summary>
    public string Detector { get; init; } = "";

    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>A JWS over the result without the signature, when asked for.</summary>
    public string? Signature { get; init; }
}

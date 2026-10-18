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
}

/// <summary>What the SDK reports after placing content (<c>POST /v1/agents/{agent}/provenance/placements</c>): never the content.</summary>
public sealed record PlacementReport
{
    /// <summary>The signed record's id; empty for a placement recorded as exempt, which has no record.</summary>
    public string RecordId { get; init; } = "";

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

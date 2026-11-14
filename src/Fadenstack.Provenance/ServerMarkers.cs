using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>
/// The server's signed record of the placement: the record with the content's keyed hash, signed with the
/// installation's key and time-stamped where a time-stamp authority answers. The detector finds unchanged content
/// by its hash and answers with the record. It is evidence kept by the server, not a mark in the host: where the
/// content lands in a container, a host marker is needed as well. It runs only with a signed record (as every
/// marker); without one the pipeline reports <c>signed-record</c> not applied, <c>server-unavailable</c>.
/// </summary>
public sealed class ServerSignedMarker : ContentMarker
{
    public const string MarkerName = "server-record";

    /// <summary>The method the placement report names.</summary>
    public const string Method = "signed-record";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => true;

    public override Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(MarkOutcome.Recorded(MarkLayer.Metadata, Method, record.Timestamp is null ? "signed" : "signed, time-stamped"));
}

/// <summary>
/// Records from the receipt whether the model's text carries a watermark (from the model's runtime, or from the
/// remote provider), or a picture the provenance engine's; and if not, why. It never watermarks anything itself,
/// and cannot check a watermark: the server's detector does, so it never says verified. The methods:
/// <c>vllm-&lt;algorithm&gt;</c> (a local model's runtime), <c>upstream-&lt;vendor&gt;</c> (a remote provider's own mark),
/// the engine's algorithm, <c>trustmark</c> (a picture); <c>model</c> when the text carries none.
/// </summary>
public sealed class WatermarkStatus : ContentMarker
{
    public const string MarkerName = "watermark-status";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Watermark;

    public override bool Supports(SinkInfo sink, string mediaType) => MediaTypes.IsText(mediaType) || MediaTypes.IsImage(mediaType);

    public override Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(Of(placed.Content));

    /// <summary>
    /// Without a server (sdk/51 F10): the person's endpoint's watermark, which the agent relies on and never claims. Applied only
    /// where the provider marks by default or the server said so for this answer (<c>x-watermark</c>); not possible where the
    /// runtime has none or the sampling was deterministic; otherwise not claimed, with why.
    /// </summary>
    private static MarkOutcome OfEndpoint(EndpointLayer endpoint)
    {
        var method = "endpoint-" + endpoint.Server;
        var answer = endpoint.Answer;
        if (answer?.Deterministic == true)
        {
            return MarkOutcome.Missing(MarkLayer.Watermark, method, ProvenanceReasons.NoWatermarkGreedy, "deterministic sampling");
        }
        if (answer?.Confirmed == true)
        {
            return MarkOutcome.Recorded(MarkLayer.Watermark, method, "the endpoint said this answer is watermarked");
        }
        return endpoint.Connection.Status switch
        {
            Endpoints.TextWatermark.Marks when answer?.Confirmed != false =>
                MarkOutcome.Recorded(MarkLayer.Watermark, method, "relied on: the provider marks text by default"),
            Endpoints.TextWatermark.Never => MarkOutcome.Missing(MarkLayer.Watermark, method, ProvenanceReasons.RuntimeWithoutWatermark),
            _ when answer?.Confirmed == false =>
                MarkOutcome.Missing(MarkLayer.Watermark, method, ProvenanceReasons.NoWatermarkRemoteModel, "the endpoint said this answer is not watermarked"),
            Endpoints.TextWatermark.IfOperatorConfigured =>
                MarkOutcome.Missing(MarkLayer.Watermark, method, null, "not claimed: only if the server's operator configured it"),
            Endpoints.TextWatermark.IfAccountEnabled =>
                MarkOutcome.Missing(MarkLayer.Watermark, method, null, "not claimed: only if the account turned it on"),
            _ => MarkOutcome.Missing(MarkLayer.Watermark, method, null, "not claimed: not known for this endpoint"),
        };
    }

    /// <summary>The watermark layer of <paramref name="content"/>, from its receipt and its length.</summary>
    public static MarkOutcome Of(MarkableContent content)
    {
        var receipt = content.Receipt;
        if (MediaTypes.IsImage(content.MediaType))
        {
            // The gateway marks every picture it makes before it leaves, or refuses it.
            return receipt?.ReceiptId is not null
                ? MarkOutcome.Recorded(MarkLayer.Watermark, "trustmark", "picture with a receipt")
                : MarkOutcome.Missing(MarkLayer.Watermark, "trustmark", null, "no receipt");
        }
        if (content.IsShort)
        {
            return MarkOutcome.Missing(MarkLayer.Watermark, content.Endpoint is null ? "model" : "endpoint", ProvenanceReasons.ShortText);
        }
        if (content.Endpoint is { } endpoint)
        {
            return OfEndpoint(endpoint);
        }
        if (receipt is { Signed: null, Reason: { } unsigned })
        {
            var reason = ProvenanceReasons.All.Contains(unsigned) ? unsigned : ProvenanceReasons.ServerUnavailable;
            return MarkOutcome.Missing(MarkLayer.Watermark, "model", reason, "unsigned answer");
        }
        if (receipt?.Signed?.Receipt.Watermark is not { } watermark)
        {
            return MarkOutcome.Missing(MarkLayer.Watermark, "model", null, receipt is null ? "no receipt" : "receipt without its details");
        }
        return watermark.Applied switch
        {
            WatermarkApplied.Model => MarkOutcome.Recorded(MarkLayer.Watermark, "vllm-" + (watermark.Algorithm ?? "gumbel")),
            WatermarkApplied.Engine => MarkOutcome.Recorded(MarkLayer.Watermark, watermark.Algorithm ?? "engine"),
            WatermarkApplied.Upstream => MarkOutcome.Recorded(MarkLayer.Watermark, "upstream-" + (watermark.Vendor ?? "vendor")),
            _ => MarkOutcome.Missing(MarkLayer.Watermark, "model",
                watermark.Reason is { } why && ProvenanceReasons.All.Contains(why) ? why : null),
        };
    }
}

/// <summary>
/// Checks that a picture the server made still carries its C2PA manifest and XMP when it is placed, and, where the
/// host can give the bytes back, after: a host that re-encodes it (Word's compression of small pictures, a
/// "compress pictures" setting) drops both, and that is recorded as <c>stripped-by-host</c>. Pictures go in as the
/// same bytes; this marker changes nothing.
/// </summary>
public sealed class ImageProvenanceGuard : ContentMarker
{
    public const string MarkerName = "image-guard";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => MediaTypes.IsImage(mediaType);

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        if (placed.Content.Bytes is not { } bytes)
        {
            return MarkOutcome.Missing(MarkLayer.Metadata, "c2pa", null, "no bytes");
        }
        var given = ImageMarks.Read(bytes);
        if (!given.HasC2pa)
        {
            return MarkOutcome.Missing(MarkLayer.Metadata, "c2pa", ProvenanceReasons.StrippedByHost,
                given.HasXmp ? "no C2PA manifest before it was placed; XMP there" : "no C2PA manifest or XMP before it was placed");
        }
        if (placed.ReadBackBytes is null || await placed.ReadBackBytes(cancellationToken).ConfigureAwait(false) is not { } kept)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, "c2pa", given.HasXmp ? "c2pa, xmp; not read back" : "c2pa; not read back");
        }
        var after = ImageMarks.Read(kept);
        return after.HasC2pa
            ? MarkOutcome.Held(MarkLayer.Metadata, "c2pa", after.HasXmp ? "c2pa, xmp" : "c2pa")
            : MarkOutcome.Missing(MarkLayer.Metadata, "c2pa", ProvenanceReasons.StrippedByHost,
                after.HasXmp ? "the host kept XMP and dropped C2PA" : "the host re-encoded the picture");
    }
}

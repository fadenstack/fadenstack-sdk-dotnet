using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Fadenstack.Agents;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>
/// The <see cref="IProvenance"/> of one tool call: places content through the markers and records each placement, in
/// the order the TypeScript SDK's <c>placeContent</c> follows too:
/// <list type="number">
/// <item>the policy; the edit kind (the content's, else the tool's declared output's, else <c>generated</c>; then the
/// classifier); the receipt (a picture's own, else the answer's); the integrator's facts;</item>
/// <item>content exempt from marking (<c>internal-technical</c>, an exempt <c>edit-kind</c>, <c>code</c>, a <c>label</c>)
/// is inserted as it is, with no record, and recorded as exempt;</item>
/// <item>the markers that fit the sink, by layer (metadata, watermark, fingerprint, label);</item>
/// <item>the layers the content needs -- signed metadata in a container or for a picture, the watermark for text from
/// the short-text threshold and for pictures, the label while the policy asks for one -- checked as far as they can be
/// before anything is signed; with <c>refuse</c>, a missing one refuses here;</item>
/// <item>the server signs a record by the content's hash; without one, no marker runs (no mark carries an id the
/// server does not know), and with <c>refuse</c> a needed metadata layer refuses here;</item>
/// <item>the markers prepare, the host inserts, the markers attach and say whether their marks held;</item>
/// <item>the placement is reported (no content), the observers told, the transcript written.</item>
/// </list>
/// A refusal is recorded (<c>provenance.refused</c>) and told to the user as a notice; nothing is signed or reported. An
/// insert that throws after the record was signed is reported as not written (<see cref="PlacementReport.Written"/> false,
/// its exception's name as <see cref="PlacementReport.Failure"/>) with every layer not applied, so the server's placements
/// match its records, and its exception goes on. A call whose declared output says it writes no content
/// (<see cref="ToolOutput.EditKindOf"/> null, no <see cref="MarkableContent.EditKind"/>) is inserted as it is: not
/// marked, not reported.
/// </summary>
internal sealed class ProvenanceCall : IProvenance
{
    /// <summary>How long a placement report may take before the tool goes on without it.</summary>
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex Namespace = new(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex FactName = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Reserved = { "c2pa", "stds", "iptc", "org.iptc", "com.fadenstack", "fadenstack", "provenance" };

    /// <summary>The method of the layer an exempt placement reports: no record made.</summary>
    private const string ExemptMethod = "record";

    /// <summary>The method of a metadata layer no host marker fits.</summary>
    private const string HostMethod = "host";

    private readonly ProvenanceState _state;
    private readonly ToolInvocation _call;
    private readonly List<Placement> _placements = new();

    public ProvenanceCall(ProvenanceState state, ToolInvocation call)
    {
        _state = state;
        _call = call;
    }

    private ProvenanceOptions Options => _state.Options;

    public ProvenanceReceiptContent? Receipt => _state.Receipts.Latest(_call.TurnId);

    public IReadOnlyList<Placement> Placements
    {
        get
        {
            lock (_placements)
            {
                return _placements.ToList();
            }
        }
    }

    public Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        _state.PolicyAsync(_call.Agent, cancellationToken);

    public async Task<Placement> PlaceAsync(MarkableContent content, Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }
        if (insert is null)
        {
            throw new ArgumentNullException(nameof(insert));
        }
        if ((content.Text is null) == (content.Bytes is null))
        {
            throw new ArgumentException("Content is text or bytes, not both and not neither.", nameof(content));
        }
        var output = Declared(content);
        var outputKind = output?.EditKindFor(new Dictionary<string, object?>(_call.Arguments));
        if (content.EditKind is null && output is not null && outputKind is null)
        {
            return await NotContentAsync(content, output, insert, cancellationToken).ConfigureAwait(false);
        }
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var declared = content.EditKind ?? outputKind ?? EditKind.Generated;
        var (kind, classified) = Classify(declared, content, policy);
        var tokens = content.Text is { } text ? Options.CountTokens(text) : 0;
        var isShort = content.IsText && tokens < policy.ShortTextTokens;
        var length = !content.IsText ? LengthClass.Long
            : tokens < policy.ShortTextTokens ? LengthClass.Short
            : tokens < policy.ShortTextTokens * 5 ? LengthClass.Medium : LengthClass.Long;
        var isImage = MediaTypes.IsImage(content.MediaType);
        // A picture's receipt is its own (the images route names it); the answer's receipt is about its text.
        var receipt = content.ReceiptId is { Length: > 0 } id ? new ProvenanceReceiptContent(null, id)
            : isImage ? null : Receipt;
        var (facts, dropped) = Enrich(content, kind);
        var given = content with { Receipt = receipt, IsShort = isShort, Texts = Options.TextsFor(Language()), Facts = facts };
        var placement = new Placement
        {
            Tool = _call.Tool.Name, SinkKind = content.Sink.Kind, MediaType = content.MediaType, EditKind = kind, LengthClass = length,
            Argument = content.Argument ?? output?.Argument,
        };
        var extra = new Extra(receipt?.ReceiptId, classified, facts, dropped);

        if (Exempt(kind, content, policy) is { } exempt)
        {
            var landed = await insert(given, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(given);
            placement = placement with
            {
                Exempt = true, ExemptBecause = exempt, Placed = landed,
                Layers = new[] { MarkOutcome.Missing(MarkLayer.Metadata, ExemptMethod, ProvenanceReasons.ExemptEdit, exempt) },
            };
            await FinishAsync(placement, extra, cancellationToken).ConfigureAwait(false);
            return placement;
        }

        var markers = Options.MarkersFor(policy).Where(m => Supports(m, content)).OrderBy(m => (int)m.Layer).ToList();
        var inHost = markers.Where(m => m.Layer == MarkLayer.Metadata && m is not ServerSignedMarker).ToList();
        var needs = new Needs(
            Metadata: content.Sink.Container || isImage,
            Watermark: (content.IsText && !isShort) || isImage,
            Label: policy.VisibleLabel && content.IsText);
        var watermark = WatermarkStatus.Of(given);

        // What the content needs, checked before anything is signed or placed.
        var before = new List<MarkOutcome>();
        if (needs.Metadata)
        {
            if (isImage && !(content.Bytes is { } picture && ImageMarks.Read(picture).HasC2pa))
            {
                before.Add(MarkOutcome.Missing(MarkLayer.Metadata, "c2pa", ProvenanceReasons.StrippedByHost, "no C2PA manifest before it was placed"));
            }
            else if (inHost.Count == 0)
            {
                before.Add(MarkOutcome.Missing(MarkLayer.Metadata, HostMethod, ProvenanceReasons.SinkCannotCarry));
            }
        }
        if (needs.Watermark && !watermark.Applied)
        {
            before.Add(watermark);
        }
        if (needs.Label && !markers.Any(m => m.Layer == MarkLayer.Label))
        {
            before.Add(MarkOutcome.Missing(MarkLayer.Label, VisibleLabelMarker.MarkerName, ProvenanceReasons.SinkCannotCarry));
        }
        if (before.Count > 0 && policy.OnMissing == MissingLayerPolicy.Refuse)
        {
            await RefuseAsync(placement, extra, before).ConfigureAwait(false);
        }

        var signed = await SignAsync(given, kind, receipt, cancellationToken).ConfigureAwait(false);
        if (signed is null)
        {
            var unsigned = new List<MarkOutcome>();
            if (needs.Metadata && !before.Any(m => m.Layer == MarkLayer.Metadata))
            {
                unsigned.Add(MarkOutcome.Missing(MarkLayer.Metadata, ServerSignedMarker.Method, ProvenanceReasons.ServerUnavailable));
            }
            if (needs.Label && !before.Any(m => m.Layer == MarkLayer.Label))
            {
                unsigned.Add(MarkOutcome.Missing(MarkLayer.Label, VisibleLabelMarker.MarkerName, ProvenanceReasons.ServerUnavailable));
            }
            if (unsigned.Count > 0 && policy.OnMissing == MissingLayerPolicy.Refuse)
            {
                await RefuseAsync(placement, extra, unsigned).ConfigureAwait(false);
            }
        }

        var outcomes = new List<(MarkOutcome Outcome, bool Counts)>();
        var failed = new Dictionary<IContentMarker, string>();
        var marked = given;
        if (signed is not null)
        {
            foreach (var marker in markers)
            {
                try
                {
                    marked = await marker.PrepareAsync(marked, signed, cancellationToken).ConfigureAwait(false) ?? marked;
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed[marker] = e.GetType().Name;
                }
            }
        }
        PlacedContent placed;
        Exception? failedInsert = null;
        try
        {
            placed = await insert(marked, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(marked);
        }
        catch (Exception e) when (signed is not null)
        {
            // A signed record with no placement: reported as nothing applied, then the host's error goes on.
            failedInsert = e;
            placed = new PlacedContent(marked);
        }
        var failedDetail = failedInsert is null ? null : "insert failed: " + failedInsert.GetType().Name;
        if (signed is not null && failedInsert is not null)
        {
            // Every marker's layer, not applied, by the method it reports; the watermark by its own method, below.
            foreach (var marker in markers.Where(m => m.Layer != MarkLayer.Watermark))
            {
                var method = marker is ServerSignedMarker ? ServerSignedMarker.Method : marker.Name;
                outcomes.Add((MarkOutcome.Missing(marker.Layer, method, null, failedDetail), marker is not ServerSignedMarker));
            }
        }
        else if (signed is not null)
        {
            foreach (var marker in markers)
            {
                // The server's record is no mark in the content: it cannot make the metadata layer hold.
                var counts = marker is not ServerSignedMarker;
                if (failed.TryGetValue(marker, out var why))
                {
                    outcomes.Add((MarkOutcome.Missing(marker.Layer, marker.Name, null, "failed: " + why), counts));
                    continue;
                }
                try
                {
                    outcomes.Add((await marker.AttachAsync(placed, signed, cancellationToken).ConfigureAwait(false), counts));
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    outcomes.Add((MarkOutcome.Missing(marker.Layer, marker.Name, null, "failed: " + e.GetType().Name), counts));
                }
            }
        }

        // What the pipeline knows itself: no record, no host marker, the watermark when no marker said.
        if (signed is null)
        {
            outcomes.Add((MarkOutcome.Missing(MarkLayer.Metadata, ServerSignedMarker.Method, ProvenanceReasons.ServerUnavailable), true));
        }
        else if (inHost.Count == 0)
        {
            outcomes.Add((MarkOutcome.Missing(MarkLayer.Metadata, HostMethod, ProvenanceReasons.SinkCannotCarry), true));
        }
        if ((content.IsText || isImage) && !outcomes.Any(o => o.Outcome.Layer == MarkLayer.Watermark))
        {
            outcomes.Add((failedDetail is null ? watermark : MarkOutcome.Missing(MarkLayer.Watermark, watermark.Method, null, failedDetail), true));
        }
        foreach (var layer in new[] { MarkLayer.Metadata, MarkLayer.Watermark, MarkLayer.Label })
        {
            if (needs.Of(layer) && before.FirstOrDefault(m => m.Layer == layer) is { } known && !outcomes.Any(o => o.Outcome.Layer == layer && o.Counts))
            {
                outcomes.Add((known, true));
            }
        }
        if (signed is null && needs.Label && !outcomes.Any(o => o.Outcome.Layer == MarkLayer.Label))
        {
            outcomes.Add((MarkOutcome.Missing(MarkLayer.Label, VisibleLabelMarker.MarkerName, ProvenanceReasons.ServerUnavailable), true));
        }
        // By layer, in the order they came within one.
        var layers = outcomes.Select((o, i) => (o, i)).OrderBy(x => (int)x.o.Outcome.Layer).ThenBy(x => x.i).Select(x => x.o).ToList();

        var missing = new List<MarkOutcome>();
        foreach (var layer in new[] { MarkLayer.Metadata, MarkLayer.Watermark, MarkLayer.Label })
        {
            if (!needs.Of(layer))
            {
                continue;
            }
            var ofLayer = layers.Where(o => o.Outcome.Layer == layer && o.Counts).Select(o => o.Outcome).ToList();
            if (ofLayer.Any(o => o.Applied))
            {
                continue;
            }
            missing.Add(ofLayer.FirstOrDefault(o => o.Reason is not null) ?? ofLayer.FirstOrDefault()
                ?? before.FirstOrDefault(m => m.Layer == layer) ?? MarkOutcome.Missing(layer, "none", null));
        }
        placement = placement with { Record = signed, Layers = layers.Select(o => o.Outcome).ToList(), Missing = missing, Placed = placed };
        if (failedInsert is not null)
        {
            try
            {
                // Reported even when the turn stops: the server's placements match its records.
                await FinishAsync(placement, extra, CancellationToken.None, PlacementReport.FailureCode(failedInsert.GetType().Name)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Provenance: the failed placement by '{placement.Tool}' was not recorded ({e.GetType().Name}).");
            }
            ExceptionDispatchInfo.Capture(failedInsert).Throw();
        }
        await FinishAsync(placement, extra, cancellationToken).ConfigureAwait(false);
        return placement;
    }

    /// <summary>
    /// A call whose declared output says it writes no content (<see cref="ToolOutput.EditKindOf"/> null) and whose content
    /// names no edit kind of its own: inserted as it is -- no record, no mark, no report, no transcript entry.
    /// </summary>
    private async Task<Placement> NotContentAsync(MarkableContent content, ToolOutput output,
        Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert, CancellationToken cancellationToken)
    {
        var landed = await insert(content, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(content);
        var placement = new Placement
        {
            Tool = _call.Tool.Name, SinkKind = content.Sink.Kind, MediaType = content.MediaType, Argument = content.Argument ?? output.Argument,
            Exempt = true, ExemptBecause = ExemptReasons.NotContent, Placed = landed,
        };
        lock (_placements)
        {
            _placements.Add(placement);
        }
        return placement;
    }

    /// <summary>The layers a placement needs.</summary>
    private sealed record Needs(bool Metadata, bool Watermark, bool Label)
    {
        public bool Of(MarkLayer layer) => layer switch
        {
            MarkLayer.Metadata => Metadata,
            MarkLayer.Watermark => Watermark,
            MarkLayer.Label => Label,
            _ => false,
        };
    }

    /// <summary>What the transcript keeps of a placement beside the placement itself.</summary>
    private sealed record Extra(string? ReceiptId, object? Classified, IReadOnlyDictionary<string, string> Facts, IReadOnlyList<string> Dropped);

    /// <summary>The declared output this content is: by argument name, else the one of its media type, else the only one.</summary>
    private ToolOutput? Declared(MarkableContent content)
    {
        var outputs = _call.Tool.Outputs;
        if (content.Argument is { } argument)
        {
            return outputs.FirstOrDefault(o => o.Argument == argument);
        }
        return outputs.FirstOrDefault(o => MediaTypes.Bare(o.MediaType) == MediaTypes.Bare(content.MediaType))
            ?? (outputs.Count == 1 ? outputs[0] : null);
    }

    /// <summary>The classifier's kind: it may lower a marked kind to an exempt one only with a reason.</summary>
    private (EditKind Kind, object? Classified) Classify(EditKind declared, MarkableContent content, ProvenancePolicy policy)
    {
        if (Options.Classifier is not { } classifier || !content.IsText)
        {
            return (declared, null);
        }
        EditDecision decision;
        try
        {
            decision = classifier.Classify(new EditInput(content.Original, content.Text, declared, content.MediaType));
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Provenance: the edit classifier failed ({e.GetType().Name}); the declared kind stays.");
            return (declared, new { from = declared.ToWire(), to = declared.ToWire(), reason = "the classifier failed" });
        }
        if (decision is null || decision.Kind == declared)
        {
            return (declared, null);
        }
        var reason = decision.Reason?.Trim() is { Length: > 0 } r ? (r.Length > 200 ? r.Substring(0, 200) : r) : null;
        var lowers = policy.ExemptEditKinds.Contains(decision.Kind) && !policy.ExemptEditKinds.Contains(declared);
        if (lowers && reason is null)
        {
            return (declared, new { from = declared.ToWire(), to = declared.ToWire(), reason = $"{decision.Kind.ToWire()} without a reason: not taken" });
        }
        return (decision.Kind, new { from = declared.ToWire(), to = decision.Kind.ToWire(), reason });
    }

    /// <summary>Why the content needs no mark at all, in this order, or null.</summary>
    private static string? Exempt(EditKind kind, MarkableContent content, ProvenancePolicy policy) =>
        policy.InternalTechnical ? ExemptReasons.InternalTechnical
        : policy.ExemptEditKinds.Contains(kind) ? ExemptReasons.EditKind
        : MediaTypes.IsCode(content.MediaType) ? ExemptReasons.Code
        : content.Text is { } text && Labels.IsLabel(text, content.MediaType) ? ExemptReasons.Label
        : null;

    private static bool Supports(IContentMarker marker, MarkableContent content)
    {
        try
        {
            return marker.Supports(content.Sink, content.MediaType);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Provenance: the marker '{marker.Name}' failed to say what it supports ({e.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// The server's signed record of the placement, with the content's hash only -- and for HTML and Markdown the hash of
    /// the text a reader sees (<see cref="ReaderText.Sha256"/>), so the words copied out of the host find it; null when
    /// it did not sign one.
    /// </summary>
    private async Task<SignedRecord?> SignAsync(MarkableContent content, EditKind kind, ProvenanceReceiptContent? receipt,
        CancellationToken cancellationToken)
    {
        if (_call.Agent is not { } agent)
        {
            return null;
        }
        var request = new RecordRequest
        {
            ContentSha256 = content.Text is { } text ? ContentHash.TextSha256(text) : ContentHash.BytesSha256(content.Bytes!),
            TextSha256 = content.Text is { } plain ? ReaderText.Sha256(plain, content.MediaType) : null,
            MediaType = content.MediaType,
            EditKind = kind,
            ReceiptId = receipt?.ReceiptId,
            Model = receipt?.Signed?.Receipt.Model.Id is { Length: > 0 } model ? model
                : MediaTypes.IsImage(content.MediaType) ? null : Model(agent), // the agent's model writes its text, not its pictures
        };
        try
        {
            var signed = await Options.Backend.SignRecordAsync(agent, request, cancellationToken).ConfigureAwait(false);
            // Marks carry the id: one that is not the server's kind of id is no record to carry.
            return ProvenanceJson.IsRecordId(signed?.Record.Id) ? signed : null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning($"Provenance: the record of a placement by '{_call.Tool.Name}' was not signed ({e.GetType().Name}).");
            return null;
        }
    }

    private static string? Model(FadenstackAgent agent)
    {
        try
        {
            return agent.Profile.Model;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string? Language()
    {
        try
        {
            return _call.Agent?.Profile.User.Language;
        }
        catch (InvalidOperationException)
        {
            return null; // not started
        }
    }

    /// <summary>
    /// The enrichers' facts that pass: under their namespace, short, and nothing that looks like personal data. What is
    /// dropped is said why, as the TypeScript SDK says it.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Facts, IReadOnlyList<string> Dropped) Enrich(MarkableContent content, EditKind kind)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        foreach (var enricher in Options.Enrichers)
        {
            var space = enricher.Namespace?.Trim() ?? "";
            if (!Namespace.IsMatch(space) || Reserved.Any(r => space == r || space.StartsWith(r + ".", StringComparison.Ordinal)))
            {
                dropped.Add($"{space}: not a namespace of the integrator's own");
                continue;
            }
            IReadOnlyDictionary<string, string> given;
            try
            {
                given = enricher.Enrich(new EnrichmentContext(_call.Tool.Name, content.Sink, content.MediaType, kind, _call));
            }
            catch (Exception e)
            {
                dropped.Add($"{space}: failed ({e.GetType().Name})");
                continue;
            }
            foreach (var pair in given ?? new Dictionary<string, string>())
            {
                if (!pair.Key.StartsWith(space + ".", StringComparison.Ordinal) || !FactName.IsMatch(pair.Key.Substring(space.Length + 1)))
                {
                    dropped.Add($"{pair.Key}: outside {space}");
                }
                else if (string.IsNullOrEmpty(pair.Value) || pair.Value.Length > 200)
                {
                    dropped.Add($"{pair.Key}: empty or longer than 200 characters");
                }
                else if (PersonalData.Find(pair.Value) is { } what)
                {
                    dropped.Add($"{pair.Key}: looks like personal data ({what})");
                }
                else
                {
                    facts[pair.Key] = pair.Value;
                }
            }
        }
        return (facts, dropped);
    }

    /// <summary>Records the refusal, tells the user, and throws it with why in words the model can pass on. Nothing is reported.</summary>
    private async Task RefuseAsync(Placement placement, Extra extra, IReadOnlyList<MarkOutcome> missing)
    {
        var refused = placement with { Refused = true, Missing = missing, Layers = missing };
        lock (_placements)
        {
            _placements.Add(refused);
        }
        var message = RefusalMessage(missing);
        await _call.RecordAsync(ProvenanceEntries.Refused, new
        {
            call_id = _call.Call.CallId,
            tool = placement.Tool,
            argument = placement.Argument,
            sink_kind = placement.SinkKind,
            media_type = placement.MediaType,
            edit_kind = placement.EditKind.ToWire(),
            length_class = placement.LengthClass.ToString().ToLowerInvariant(),
            missing = missing.Select(m => new { layer = m.Layer.ToString().ToLowerInvariant(), method = m.Method, reason = m.Reason }).ToList(),
        }).ConfigureAwait(false);
        await _call.NoticeAsync(new GovernanceNotice
        {
            Outcome = "provenance_refused", Stage = "tool", Category = "provenance", Message = message,
        }).ConfigureAwait(false);
        throw new ProvenanceRefusedException(message, missing, refused);
    }

    /// <summary>
    /// Reports the placement, tells the observers, writes the transcript entry, and counts it for the call. A
    /// <paramref name="failure"/> (an insert that failed after the record was signed) reports it as not written.
    /// </summary>
    private async Task FinishAsync(Placement placement, Extra extra, CancellationToken cancellationToken, string? failure = null)
    {
        lock (_placements)
        {
            _placements.Add(placement);
        }
        var report = new PlacementReport
        {
            RecordId = placement.RecordId,
            Tool = placement.Tool,
            SinkKind = placement.SinkKind,
            MediaType = placement.MediaType,
            EditKind = placement.EditKind,
            LengthClass = placement.LengthClass,
            Layers = placement.Layers.Take(32).Select(l => l.ToLayer()).ToList(),
            // Only then: a server that does not know the fields yet takes every other report as before.
            Written = failure is null ? null : false,
            Failure = failure,
        };
        var reported = false;
        if (_call.Agent is { } agent)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ReportTimeout);
            try
            {
                await Options.Backend.ReportPlacementAsync(agent, report, limit.Token).ConfigureAwait(false);
                reported = true;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning($"Provenance: the placement by '{placement.Tool}' was not reported ({e.GetType().Name}).");
            }
        }
        foreach (var observer in Options.Observers)
        {
            try
            {
                await observer.OnPlacedAsync(report, placement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning($"Provenance: a placement observer failed ({e.GetType().Name}).");
            }
        }
        await _call.RecordAsync(ProvenanceEntries.Placed, new
        {
            call_id = _call.Call.CallId,
            tool = placement.Tool,
            argument = placement.Argument,
            record_id = placement.RecordId,
            signed = placement.Record is not null,
            receipt_id = extra.ReceiptId,
            sink_kind = placement.SinkKind,
            media_type = placement.MediaType,
            edit_kind = placement.EditKind.ToWire(),
            length_class = placement.LengthClass.ToString().ToLowerInvariant(),
            exempt = placement.Exempt,
            exempt_because = placement.ExemptBecause,
            classified = extra.Classified,
            layers = placement.Layers.Select(l => new
            {
                layer = l.Layer.ToString().ToLowerInvariant(), method = l.Method, applied = l.Applied, reason = l.Reason, verified = l.Verified,
                detail = l.Detail,
            }).ToList(),
            missing = placement.Missing.Select(m => new { layer = m.Layer.ToString().ToLowerInvariant(), method = m.Method, reason = m.Reason }).ToList(),
            reported,
            facts = extra.Facts.Count == 0 ? null : extra.Facts,
            dropped_facts = extra.Dropped.Count == 0 ? null : extra.Dropped,
        }).ConfigureAwait(false);
    }

    /// <summary>What the model and the user are told when the policy refuses (the TypeScript SDK says the same).</summary>
    internal static string RefusalMessage(IReadOnlyList<MarkOutcome> missing)
    {
        var why = missing.Select(m => m.Layer switch
        {
            MarkLayer.Watermark when m.Method == "trustmark" => "the picture carries no watermark of this installation",
            MarkLayer.Watermark => m.Reason switch
            {
                null => "it is not known whether the text carries the model's watermark",
                ProvenanceReasons.NoWatermarkRemoteModel => "the text comes from a remote model without a watermark",
                ProvenanceReasons.NoWatermarkGreedy => "the text was generated without a watermark (greedy decoding)",
                ProvenanceReasons.RuntimeWithoutWatermark => "the model's runtime does not watermark text",
                ProvenanceReasons.ServerUnavailable => "the answer's receipt could not be signed",
                _ => "the text carries no watermark",
            },
            _ => m.Reason switch
            {
                ProvenanceReasons.ServerUnavailable => "the provenance service could not sign a record of it",
                ProvenanceReasons.SinkCannotCarry when m.Layer == MarkLayer.Label => "this place cannot show a visible label",
                ProvenanceReasons.SinkCannotCarry => "this place cannot carry a machine-readable mark",
                ProvenanceReasons.StrippedByHost => "the picture lost its signed metadata before it was placed",
                _ => "it could not be marked",
            },
        }).Distinct();
        return $"Not written: this agent only places content it can mark as AI-generated, and here {string.Join("; ", why)}.";
    }
}

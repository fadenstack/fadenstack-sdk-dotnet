using System.Diagnostics;
using System.Text.RegularExpressions;
using Fadenstack.Agents;
using Fadenstack.Chat;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>The <see cref="IProvenance"/> of one tool call: places content through the markers and records each placement.</summary>
internal sealed class ProvenanceCall : IProvenance
{
    /// <summary>How long a placement report may take before the tool goes on without it.</summary>
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex Namespace = new(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex FactName = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Reserved = { "c2pa", "com.fadenstack", "fadenstack", "iptc", "provenance" };

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
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var output = Declared(content);
        var declared = content.EditKind ?? output?.EditKindFor(new Dictionary<string, object?>(_call.Arguments)) ?? EditKind.Generated;
        var (kind, classified) = Classify(declared, content, policy);
        var tokens = content.Text is { } text ? Options.CountTokens(text) : 0;
        var isShort = content.IsText && tokens < policy.ShortTextTokens;
        var length = !content.IsText ? LengthClass.Long
            : tokens < policy.ShortTextTokens ? LengthClass.Short
            : tokens < policy.ShortTextTokens * 5 ? LengthClass.Medium : LengthClass.Long;
        // A picture's receipt is its own (the images route names it); the answer's receipt is about its text.
        var receipt = content.ReceiptId is { Length: > 0 } id ? new ProvenanceReceiptContent(null, id)
            : MediaTypes.IsImage(content.MediaType) ? null : Receipt;
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
                Layers = new[] { MarkOutcome.Missing(MarkLayer.Metadata, "record", ProvenanceReasons.ExemptEdit, exempt) },
            };
            await FinishAsync(placement, extra, cancellationToken).ConfigureAwait(false);
            return placement;
        }

        var signed = await SignAsync(given, kind, receipt, cancellationToken).ConfigureAwait(false);
        var markers = Options.MarkersFor(policy).Where(m => Supports(m, content)).OrderBy(m => (int)m.Layer).ToList();
        var inHost = markers.Where(m => m.Layer == MarkLayer.Metadata && m is not ServerSignedMarker).ToList();
        var needsMetadata = content.Sink.Container || MediaTypes.IsImage(content.MediaType);
        var needsWatermark = (content.IsText && !isShort) || MediaTypes.IsImage(content.MediaType);
        var watermark = WatermarkStatus.Of(given);

        var missing = new List<MarkOutcome>();
        if (needsMetadata)
        {
            if (signed is null)
            {
                missing.Add(MarkOutcome.Missing(MarkLayer.Metadata, "record", ProvenanceReasons.ServerUnavailable));
            }
            else if (inHost.Count == 0)
            {
                missing.Add(MarkOutcome.Missing(MarkLayer.Metadata, "host", ProvenanceReasons.SinkCannotCarry));
            }
            else if (content.Bytes is { } picture && MediaTypes.IsImage(content.MediaType) && !ImageMarks.Read(picture).HasC2pa)
            {
                missing.Add(MarkOutcome.Missing(MarkLayer.Metadata, "c2pa", ProvenanceReasons.StrippedByHost, "no C2PA manifest before it was placed"));
            }
        }
        if (needsWatermark && !watermark.Applied)
        {
            missing.Add(watermark);
        }

        if (missing.Count > 0 && policy.OnMissing == MissingLayerPolicy.Refuse)
        {
            var layers = new List<MarkOutcome>();
            if (signed is not null)
            {
                layers.Add(MarkOutcome.Held(MarkLayer.Metadata, ServerSignedMarker.Method));
            }
            layers.Add(watermark);
            layers.AddRange(missing.Where(m => m.Layer != MarkLayer.Watermark));
            placement = placement with { Refused = true, Record = signed, Layers = layers, Missing = missing };
            await FinishAsync(placement, extra, cancellationToken).ConfigureAwait(false);
            var message = RefusalMessage(missing);
            await _call.NoticeAsync(new GovernanceNotice
            {
                Outcome = "provenance_refused", Stage = "tool", Category = "provenance", Message = message,
            }).ConfigureAwait(false);
            throw new ProvenanceRefusedException(message, missing, placement);
        }

        var outcomes = new List<MarkOutcome>();
        var failed = new HashSet<IContentMarker>();
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
                    failed.Add(marker);
                    outcomes.Add(MarkOutcome.Missing(marker.Layer, marker.Name, null, "failed: " + e.GetType().Name));
                }
            }
        }
        var placed = await insert(marked, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(marked);
        foreach (var marker in markers.Where(m => !failed.Contains(m)))
        {
            if (signed is null)
            {
                continue; // the marks carry the record's id; without a record only the watermark is known
            }
            try
            {
                outcomes.Add(await marker.AttachAsync(placed, signed, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(MarkOutcome.Missing(marker.Layer, marker.Name, null, "failed: " + e.GetType().Name));
            }
        }
        if (!outcomes.Any(o => o.Layer == MarkLayer.Watermark) && (content.IsText || MediaTypes.IsImage(content.MediaType)))
        {
            outcomes.Add(watermark);
        }
        if (signed is null)
        {
            outcomes.Insert(0, MarkOutcome.Missing(MarkLayer.Metadata, "record", ProvenanceReasons.ServerUnavailable));
        }
        else if (inHost.Count == 0)
        {
            outcomes.Add(MarkOutcome.Missing(MarkLayer.Metadata, "host", ProvenanceReasons.SinkCannotCarry));
        }
        else if (needsMetadata && !outcomes.Any(o => o.Layer == MarkLayer.Metadata && o.Applied && o.Method != ServerSignedMarker.Method)
                 && !missing.Any(m => m.Layer == MarkLayer.Metadata)
                 && outcomes.FirstOrDefault(o => o.Layer == MarkLayer.Metadata && o.Method != ServerSignedMarker.Method) is { } lost)
        {
            missing.Add(lost); // written, and the host did not keep it: recorded; the content is in place already
        }
        placement = placement with { Record = signed, Layers = outcomes, Missing = missing, Placed = placed };
        await FinishAsync(placement, extra, cancellationToken).ConfigureAwait(false);
        return placement;
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
        if (decision.Kind == declared)
        {
            return (declared, null);
        }
        var lowers = policy.ExemptEditKinds.Contains(decision.Kind) && !policy.ExemptEditKinds.Contains(declared);
        if (lowers && string.IsNullOrWhiteSpace(decision.Reason))
        {
            return (declared, new { from = declared.ToWire(), to = declared.ToWire(), reason = $"{decision.Kind.ToWire()} without a reason: not taken" });
        }
        return (decision.Kind, new { from = declared.ToWire(), to = decision.Kind.ToWire(), reason = decision.Reason });
    }

    /// <summary>Why the content needs no mark, or null.</summary>
    private static string? Exempt(EditKind kind, MarkableContent content, ProvenancePolicy policy) =>
        policy.InternalTechnical ? "internal-technical"
        : policy.ExemptEditKinds.Contains(kind) ? "edit-kind"
        : MediaTypes.IsCode(content.MediaType) ? "code"
        : content.Text is { } text && Labels.IsLabel(text) ? "label"
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

    /// <summary>The server's signed record of the placement, with the content's hash only; null when it did not answer.</summary>
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
            MediaType = content.MediaType,
            EditKind = kind,
            ReceiptId = receipt?.ReceiptId,
            Model = receipt?.Signed?.Receipt.Model.Id is { Length: > 0 } model ? model
                : MediaTypes.IsImage(content.MediaType) ? null : Model(agent), // the agent's model writes its text, not its pictures
        };
        try
        {
            var signed = await Options.Backend.SignRecordAsync(agent, request, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The enrichers' facts that pass: under their namespace, short, and nothing that looks like personal data.</summary>
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
                else if (pair.Value is null || pair.Value.Length > 200)
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

    /// <summary>Reports the placement, tells the observers, writes the transcript entry, and counts it for the call.</summary>
    private async Task FinishAsync(Placement placement, Extra extra, CancellationToken cancellationToken)
    {
        lock (_placements)
        {
            _placements.Add(placement);
        }
        var report = new PlacementReport
        {
            RecordId = placement.RecordId ?? "",
            Tool = placement.Tool,
            SinkKind = placement.SinkKind,
            MediaType = placement.MediaType,
            EditKind = placement.EditKind,
            LengthClass = placement.LengthClass,
            Layers = placement.Layers.Take(32).Select(l => l.ToLayer()).ToList(),
        };
        if (_call.Agent is { } agent)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ReportTimeout);
            try
            {
                await Options.Backend.ReportPlacementAsync(agent, report, limit.Token).ConfigureAwait(false);
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
            sink_kind = placement.SinkKind,
            media_type = placement.MediaType,
            edit_kind = placement.EditKind.ToWire(),
            length_class = placement.LengthClass.ToString().ToLowerInvariant(),
            argument = placement.Argument,
            record_id = placement.RecordId,
            receipt_id = extra.ReceiptId,
            exempt = placement.Exempt,
            exempt_because = placement.ExemptBecause,
            refused = placement.Refused,
            layers = placement.Layers.Select(l => new
            {
                layer = l.Layer.ToString().ToLowerInvariant(), method = l.Method, applied = l.Applied, reason = l.Reason, verified = l.Verified,
                detail = l.Detail,
            }).ToList(),
            missing = placement.Missing.Select(m => new { layer = m.Layer.ToString().ToLowerInvariant(), method = m.Method, reason = m.Reason }).ToList(),
            classified = extra.Classified,
            facts = extra.Facts.Count == 0 ? null : extra.Facts,
            dropped_facts = extra.Dropped.Count == 0 ? null : extra.Dropped,
        }).ConfigureAwait(false);
    }

    /// <summary>What the model and the user are told when the policy refuses.</summary>
    private static string RefusalMessage(IReadOnlyList<MarkOutcome> missing)
    {
        var why = missing.Select(m => m.Layer switch
        {
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
                ProvenanceReasons.SinkCannotCarry => "this place cannot carry a machine-readable mark",
                ProvenanceReasons.StrippedByHost => "the picture lost its signed metadata before it was placed",
                _ => "it could not be marked",
            },
        }).Distinct();
        return $"Not written: this agent only places content it can mark as AI-generated, and here {string.Join("; ", why)}.";
    }
}

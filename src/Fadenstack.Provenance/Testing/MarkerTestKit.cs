using System.Text;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance.Testing;

/// <summary>
/// A backend for tests: answers with a policy, signs records with a test signature (not a JWS anyone should trust),
/// and keeps what it was sent. No server needed.
/// </summary>
public sealed class TestProvenanceBackend : IProvenanceBackend
{
    private int _next;

    public ProvenancePolicy Policy { get; set; } = new();

    /// <summary>When set, signing fails as if the server did not answer.</summary>
    public bool Unavailable { get; set; }

    public List<RecordRequest> Requests { get; } = new();

    public List<PlacementReport> Reports { get; } = new();

    public Task<ProvenancePolicy> GetPolicyAsync(FadenstackAgent agent, CancellationToken cancellationToken) => Task.FromResult(Policy);

    public Task<SignedRecord> SignRecordAsync(FadenstackAgent agent, RecordRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        return Unavailable ? throw new FadenstackException("The provenance service did not answer.") : Task.FromResult(Sign(request));
    }

    public Task ReportPlacementAsync(FadenstackAgent agent, PlacementReport report, CancellationToken cancellationToken)
    {
        lock (Reports)
        {
            Reports.Add(report);
        }
        return Task.CompletedTask;
    }

    /// <summary>A record for <paramref name="request"/> as the server would make it, with a test signature.</summary>
    public SignedRecord Sign(RecordRequest request)
    {
        var n = Interlocked.Increment(ref _next);
        var record = new ProvenanceRecord
        {
            Id = $"00000000-0000-4000-8000-{n:D12}",
            DigitalSourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/" +
                (request.EditKind == EditKind.Generated ? "trainedAlgorithmicMedia" : "compositeWithTrainedAlgorithmicMedia"),
            EditKind = request.EditKind,
            System = new ProvenanceSystem { Name = "Fadenstack", Version = "test" },
            Provider = "Test organisation",
            CreatedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
            MediaType = request.MediaType,
            ContentHash = request.ContentSha256,
            HashKeyVersion = 1,
            Model = request.Model is null ? null : new ProvenanceModel { Id = request.Model },
            ReceiptId = request.ReceiptId,
        };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(ProvenanceJson.Compact(record)));
        return new SignedRecord { Record = record, Signature = $"eyJhbGciOiJub25lIn0.{payload}.test" };
    }
}

/// <summary>What a marker test found: the outcome, what landed, the marks read back, and any problem.</summary>
public sealed record MarkerTestResult
{
    public SignedRecord Record { get; init; } = new();

    public MarkableContent Prepared { get; init; } = null!;

    public PlacedContent Placed { get; init; } = null!;

    public MarkOutcome Outcome { get; init; } = null!;

    /// <summary>Our marks <see cref="ProvenanceReader"/> found in what the host holds, the key-values and the sidecar.</summary>
    public IReadOnlyList<FoundMark> Found { get; init; } = Array.Empty<FoundMark>();

    /// <summary>Everything the marker wrote besides the content itself: added text, key-values, headers, the sidecar.</summary>
    public IReadOnlyList<string> Written { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public bool Passed => Problems.Count == 0;
}

/// <summary>A marker failed the kit: the problems, one per line.</summary>
public sealed class MarkerTestException : Exception
{
    public MarkerTestException(string marker, IReadOnlyList<string> problems)
        : base($"The marker '{marker}' failed: " + string.Join("; ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Runs a marker through the pipeline's steps -- supports, prepare with a signed record, the host's insert, attach --
/// and checks it the way the pipeline relies on it: the outcome is honest (a mark it says held is there when read
/// back), the marks name the record, and nothing it writes carries personal data. The content holds a name, an
/// e-mail address, a phone number and the user's request (<see cref="PersonalSamples"/>), so a marker that copies
/// content into its mark fails. Works with any test framework: <see cref="AssertAsync"/> throws on a problem.
/// </summary>
public static class MarkerTestKit
{
    /// <summary>What must never show up in a mark: a user's name, address, phone number, and what they asked for.</summary>
    public static IReadOnlyList<string> PersonalSamples { get; } = new[]
    {
        "Erika Mustermann", "erika.mustermann@example.com", "+49 30 1234567", "Write to Erika about the overdue invoice",
    };

    /// <summary>A text that carries the samples, as model-written content often does.</summary>
    public static string SampleText { get; } =
        "Dear Erika Mustermann, thank you for your message to erika.mustermann@example.com. " +
        "We will call you at +49 30 1234567 about the invoice. (Asked: Write to Erika about the overdue invoice)";

    /// <summary>Runs the marker and throws <see cref="MarkerTestException"/> on a problem.</summary>
    public static async Task<MarkerTestResult> AssertAsync(IContentMarker marker, MarkableContent content,
        Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert, Func<PlacedContent, CancellationToken, Task<string?>>? readRecordId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(marker, content, insert, readRecordId, cancellationToken).ConfigureAwait(false);
        return result.Passed ? result : throw new MarkerTestException(marker.Name, result.Problems);
    }

    /// <summary>Runs the marker and reports what it found, without throwing.</summary>
    /// <param name="marker">The marker under test.</param>
    /// <param name="content">What it marks (<see cref="SampleText"/> carries personal data to look for); its key-value and
    /// header sinks are recorded.</param>
    /// <param name="insert">The host: inserts the prepared content and says what landed.</param>
    /// <param name="readRecordId">For a host the reader does not know (a document part, a bookmark): reads the record id
    /// back from what landed; null leaves the check to the marker's own read-back.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public static async Task<MarkerTestResult> RunAsync(IContentMarker marker, MarkableContent content,
        Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert, Func<PlacedContent, CancellationToken, Task<string?>>? readRecordId = null,
        CancellationToken cancellationToken = default)
    {
        if (marker is null || content is null || insert is null)
        {
            throw new ArgumentNullException(marker is null ? nameof(marker) : content is null ? nameof(content) : nameof(insert));
        }
        var problems = new List<string>();
        var written = new List<string>();
        var keyValue = content.Sink.KeyValue is { } kv ? new RecordingSink(kv) : null;
        var headers = content.Sink.Headers is { } h ? new RecordingSink(h) : null;
        var sink = content.Sink with { KeyValue = keyValue, Headers = headers };
        var given = content with { Sink = sink, Texts = content.Texts ?? UI.UiStrings.Current };
        var record = new TestProvenanceBackend().Sign(new RecordRequest
        {
            ContentSha256 = given.Text is { } t ? ContentHash.TextSha256(t) : ContentHash.BytesSha256(given.Bytes ?? Array.Empty<byte>()),
            MediaType = given.MediaType, EditKind = given.EditKind ?? EditKind.Generated, ReceiptId = given.ReceiptId,
        });
        var id = record.Record.Id;
        if (!marker.Supports(sink, given.MediaType))
        {
            problems.Add($"it does not support {given.MediaType} in a '{sink.Kind}' sink that carries [{string.Join(", ", sink.Carries)}]");
        }
        var prepared = await marker.PrepareAsync(given, record, cancellationToken).ConfigureAwait(false) ?? given;
        written.Add(Added(prepared.Text, given.Text));
        var placed = await insert(prepared, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(prepared);
        var outcome = await marker.AttachAsync(placed, record, cancellationToken).ConfigureAwait(false);
        if (outcome.Layer != marker.Layer)
        {
            problems.Add($"the outcome's layer is {outcome.Layer}, the marker's {marker.Layer}");
        }
        var back = await placed.ReadBackAsync(cancellationToken).ConfigureAwait(false);
        written.Add(Added(back, given.Text));
        written.AddRange(keyValue?.Writes ?? new List<string>());
        written.AddRange(headers?.Writes ?? new List<string>());
        var found = new List<FoundMark>();
        if (back is not null)
        {
            found.AddRange(ProvenanceReader.Read(back, prepared.MediaType));
        }
        if (keyValue is not null && keyValue.Values.TryGetValue(KeyValueMarker.RecordKey, out var kept) && ProvenanceJson.IsRecordId(kept))
        {
            found.Add(new FoundMark(kept, KeyValueMarker.MarkerName));
        }
        if (headers is not null && ProvenanceReader.ReadHeader(headers.Values.TryGetValue(HttpHeaderMarker.Header, out var header) ? header : null) is { } inHeader)
        {
            found.Add(inHeader);
        }
        if (sink.FilePath is { } file && File.Exists(SidecarMarker.PathFor(file)))
        {
            written.Add(File.ReadAllText(SidecarMarker.PathFor(file)));
            if (ProvenanceReader.ReadSidecar(file) is { } inSidecar)
            {
                found.Add(inSidecar);
            }
        }
        if (readRecordId is not null && await readRecordId(placed, cancellationToken).ConfigureAwait(false) is { } own)
        {
            found.Add(new FoundMark(own, marker.Name));
        }
        if (found.Any(f => f.RecordId != id))
        {
            problems.Add("a mark names another record: " + string.Join(", ", found.Where(f => f.RecordId != id).Select(f => f.RecordId).Distinct()));
        }
        // A metadata mark names the record; a label or a watermark need not.
        if (marker.Layer == MarkLayer.Metadata && outcome.Verified == true && !found.Any(f => f.RecordId == id)
            && (back is not null || keyValue is not null || headers is not null || readRecordId is not null))
        {
            problems.Add("it says the mark held, and reading back finds no mark of the record");
        }
        if (!outcome.Applied && outcome.Reason is { } reason && !ProvenanceReasons.All.Contains(reason))
        {
            problems.Add($"'{reason}' is not a reason code of the contract");
        }
        foreach (var sample in PersonalSamples)
        {
            if (written.Any(w => w.IndexOf(sample, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                problems.Add($"its marks carry personal data or the request ('{sample}')");
            }
        }
        if (written.Any(w => PersonalData.Find(w.Replace(id, "")) is not null))
        {
            problems.Add("its marks carry something that looks like personal data");
        }
        return new MarkerTestResult
        {
            Record = record, Prepared = prepared, Placed = placed, Outcome = outcome, Found = found,
            Written = written.Where(w => w.Length > 0).ToList(), Problems = problems,
        };
    }

    /// <summary>What <paramref name="after"/> holds beyond <paramref name="original"/>.</summary>
    private static string Added(string? after, string? original)
    {
        if (string.IsNullOrEmpty(after))
        {
            return "";
        }
        if (string.IsNullOrEmpty(original))
        {
            return after!;
        }
        if (after!.Contains(original!))
        {
            return after.Replace(original!, " ");
        }
        var lines = new HashSet<string>(original!.Split('\n').Select(l => l.TrimEnd('\r')));
        return string.Join("\n", after.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !lines.Contains(l)));
    }

    /// <summary>A sink that keeps what was written to it, for the kit to read.</summary>
    private sealed class RecordingSink : IKeyValueSink
    {
        private readonly IKeyValueSink _inner;

        public RecordingSink(IKeyValueSink inner) => _inner = inner;

        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = new();

        public async Task WriteAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            Writes.Add($"{key}: {value}");
            await _inner.WriteAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken) => _inner.ReadAsync(key, cancellationToken);
    }
}

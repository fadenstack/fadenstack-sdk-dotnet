using System.Globalization;
using Fadenstack.Models;
using Fadenstack.Provenance.Testing;
using Fadenstack.UI;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>Each built-in marker through the test kit: honest outcomes, the record's id, no personal data.</summary>
public class MarkerTests
{
    private static readonly UiStrings English = UiStrings.For(CultureInfo.GetCultureInfo("en"));

    /// <summary>A host that keeps what it is given, and can be read back.</summary>
    private sealed class Host
    {
        public string Held { get; set; } = "";

        public Func<string, string> Keep { get; set; } = s => s;

        public SinkInfo Sink(string kind, params string[] carries) =>
            new(kind, carries) { Container = true, ReadBack = _ => Task.FromResult<string?>(Held) };

        public Task<PlacedContent?> InsertAsync(MarkableContent content, CancellationToken cancellationToken)
        {
            Held = Keep(content.Text!);
            return Task.FromResult<PlacedContent?>(new PlacedContent(content));
        }
    }

    private static MarkableContent Content(string mediaType, SinkInfo sink, string? text = null) =>
        new(text ?? MarkerTestKit.SampleText, mediaType, sink) { Texts = English };

    [Fact]
    public async Task The_html_marker_wraps_reads_back_and_carries_no_personal_data()
    {
        var host = new Host();

        var result = await MarkerTestKit.AssertAsync(new HtmlAttributeMarker(), Content("text/html", host.Sink("html.editor", Carriers.HtmlAttribute)),
            host.InsertAsync);

        Assert.Equal((true, true, "html-attribute"), (result.Outcome.Applied, result.Outcome.Verified, result.Outcome.Method));
        Assert.StartsWith($"<span data-ai-generated=\"{result.Record.Record.Id}\" title=\"AI-generated {result.Record.Record.Id}\">", host.Held);
    }

    [Fact]
    public async Task The_html_title_is_in_the_users_language()
    {
        var host = new Host();
        var content = Content("text/html", host.Sink("html.editor", Carriers.HtmlAttribute)) with { Texts = UiStrings.For(CultureInfo.GetCultureInfo("zh-Hans")) };

        var result = await MarkerTestKit.AssertAsync(new HtmlAttributeMarker(), content, host.InsertAsync);

        Assert.Contains($"title=\"AI 生成 {result.Record.Record.Id}\"", host.Held);
        Assert.Contains(new FoundMark(result.Record.Record.Id, ProvenanceReader.HtmlTitle), result.Found);
    }

    [Fact]
    public async Task Front_matter_is_merged_into_what_the_file_has_and_keeps_its_line_endings()
    {
        var host = new Host();
        var text = "---\r\ntitle: Summary\r\nai-generated: false\r\n---\r\n\r\n# Summary\r\n\r\n" + MarkerTestKit.SampleText;

        var result = await MarkerTestKit.AssertAsync(new MarkdownFrontMatterMarker(),
            Content("text/markdown", host.Sink("nextcloud.file", Carriers.FrontMatter), text), host.InsertAsync);

        var id = result.Record.Record.Id;
        Assert.StartsWith($"---\r\ntitle: Summary\r\nai-generated: true\r\nai-record-id: \"{id}\"\r\n---\r\n\r\n# Summary", host.Held);
        Assert.True(result.Outcome.Verified);
        Assert.Equal($"---\nai-generated: true\nai-record-id: \"{id}\"\n---\n\n# Plain", MarkdownFrontMatterMarker.Merge("# Plain", id));
    }

    [Fact]
    public async Task A_key_value_field_keeps_the_id_and_the_signed_record_where_it_fits()
    {
        var fields = new Dictionary<string, string>();
        IKeyValueSink Sink(int? max) => KeyValueSink.From((k, v) => { fields[k] = v; return Task.CompletedTask; },
            k => Task.FromResult(fields.TryGetValue(k, out var v) ? v : null), max);
        var host = new Host();

        var roomy = await MarkerTestKit.AssertAsync(new KeyValueMarker(),
            Content("text/plain", new SinkInfo("crm.note", Carriers.KeyValue) { KeyValue = Sink(null) }), host.InsertAsync);
        Assert.Equal("record id, signed record", roomy.Outcome.Detail);
        Assert.Contains(KeyValueMarker.DefaultSignedKey, fields.Keys);

        fields.Clear();
        var tight = await MarkerTestKit.AssertAsync(new KeyValueMarker(),
            Content("text/plain", new SinkInfo("crm.note", Carriers.KeyValue) { KeyValue = Sink(64) }), host.InsertAsync);
        Assert.Equal("record id", tight.Outcome.Detail);
        Assert.Equal(new[] { KeyValueMarker.DefaultIdKey }, fields.Keys);

        var unreadable = await MarkerTestKit.AssertAsync(new KeyValueMarker(),
            Content("text/plain", new SinkInfo("crm.note", Carriers.KeyValue) { KeyValue = KeyValueSink.From((_, _) => Task.CompletedTask) }), host.InsertAsync);
        Assert.Equal((true, false), (unreadable.Outcome.Applied, unreadable.Outcome.Verified));
    }

    [Fact]
    public async Task A_sidecar_file_holds_the_signed_record_beside_the_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"faden-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(file, MarkerTestKit.SampleText);
            var host = new Host();

            var result = await MarkerTestKit.AssertAsync(new SidecarMarker(),
                Content("text/markdown", new SinkInfo("file", Carriers.Sidecar) { FilePath = file }), host.InsertAsync);

            Assert.True(result.Outcome.Verified);
            Assert.Equal(file + ".provenance.json", SidecarMarker.PathFor(file));
            Assert.Equal(new FoundMark(result.Record.Record.Id, "sidecar"), ProvenanceReader.ReadSidecar(file));
            Assert.Contains("\"signature\":\"eyJhbGciOiJub25lIn0.", File.ReadAllText(SidecarMarker.PathFor(file)));
        }
        finally
        {
            File.Delete(file);
            File.Delete(SidecarMarker.PathFor(file));
        }
    }

    [Fact]
    public async Task An_http_response_names_the_record_in_its_header()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sink = new SinkInfo("http.response", Carriers.HttpHeader)
        {
            Headers = KeyValueSink.From((k, v) => { headers[k] = v; return Task.CompletedTask; }, k => Task.FromResult(headers.TryGetValue(k, out var v) ? v : null)),
        };

        var result = await MarkerTestKit.AssertAsync(new HttpHeaderMarker(), Content("text/html", sink), new Host().InsertAsync);

        Assert.Equal(result.Record.Record.Id, headers["X-Faden-Provenance"]);
        Assert.True(result.Outcome.Verified);
    }

    [Fact]
    public async Task The_visible_label_goes_above_the_text_and_after_front_matter()
    {
        var host = new Host();

        var plain = await MarkerTestKit.AssertAsync(new VisibleLabelMarker(), Content("text/plain", host.Sink("note")), host.InsertAsync);
        Assert.StartsWith("AI-generated\n\nDear Erika", host.Held);
        Assert.Equal(MarkLayer.Label, plain.Outcome.Layer);

        await MarkerTestKit.AssertAsync(new VisibleLabelMarker(), Content("text/markdown", host.Sink("note"), "---\nai-generated: true\n---\n\nBody"), host.InsertAsync);
        Assert.Equal("---\nai-generated: true\n---\n*AI-generated*\n\nBody", host.Held);
    }

    [Fact]
    public async Task Invisible_characters_carry_the_id_where_the_host_keeps_them()
    {
        var host = new Host();

        var result = await MarkerTestKit.AssertAsync(new UnicodeTextMarker(), Content("text/plain", host.Sink("chat")), host.InsertAsync);

        var id = result.Record.Record.Id;
        Assert.Equal(MarkerTestKit.SampleText, ContentHash.Normalize(host.Held).Substring(0, MarkerTestKit.SampleText.Length));
        Assert.Equal(new[] { id }, UnicodeTextMarker.Decode(host.Held));
        Assert.Contains(new FoundMark(id, "unicode-text"), ProvenanceReader.Read(host.Held, "text/plain"));

        host.Keep = s => new string(s.Where(c => c < '︀' && !char.IsSurrogate(c)).ToArray()); // a filter that strips them
        var stripped = await MarkerTestKit.RunAsync(new UnicodeTextMarker(), Content("text/plain", host.Sink("chat")), host.InsertAsync);
        Assert.Equal(ProvenanceReasons.StrippedByHost, stripped.Outcome.Reason);
    }

    [Fact]
    public void Built_in_markers_apply_only_where_the_sink_carries_what_they_need()
    {
        var nothing = SinkInfo.Nothing("chat.message");
        Assert.False(new HtmlAttributeMarker().Supports(nothing, "text/html"));
        Assert.False(new HtmlAttributeMarker().Supports(new SinkInfo("editor", Carriers.HtmlAttribute), "text/plain"));
        Assert.True(new HtmlAttributeMarker().Supports(new SinkInfo("editor", Carriers.HtmlAttribute), "text/html; charset=utf-8"));
        Assert.False(new KeyValueMarker().Supports(new SinkInfo("crm", Carriers.KeyValue), "text/plain")); // no field given
        Assert.False(new SidecarMarker().Supports(new SinkInfo("file", Carriers.Sidecar), "text/plain")); // no file given
        Assert.True(new ServerSignedMarker().Supports(nothing, "text/plain"));
        Assert.True(new ImageProvenanceGuard().Supports(nothing, "image/jpeg"));
        var defaults = new ProvenanceOptions().Markers.Select(m => m.Name).ToList();
        Assert.DoesNotContain("unicode-text", defaults); // opt-in only
        Assert.DoesNotContain("visible-label", defaults); // the deployer's choice
        Assert.Equal(8, defaults.Count);
    }

    /// <summary>A marker that copies the start of the content into its mark.</summary>
    private sealed class LeakyMarker : ContentMarker
    {
        public override string Name => "leaky";

        public override MarkLayer Layer => MarkLayer.Metadata;

        public override bool Supports(SinkInfo sink, string mediaType) => true;

        public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(content.WithText($"<!-- {record.Record.Id} {content.Text!.Substring(0, 30)} -->{content.Text}"));

        public override Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(MarkOutcome.Written(MarkLayer.Metadata, Name));
    }

    /// <summary>A marker that says its mark held and writes none.</summary>
    private sealed class BoastingMarker : ContentMarker
    {
        public override string Name => "boasting";

        public override MarkLayer Layer => MarkLayer.Metadata;

        public override bool Supports(SinkInfo sink, string mediaType) => true;

        public override Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(MarkOutcome.Held(MarkLayer.Metadata, Name));
    }

    [Fact]
    public async Task The_kit_fails_a_marker_that_leaks_content_or_claims_a_mark_it_did_not_write()
    {
        var host = new Host();

        var leak = await Assert.ThrowsAsync<MarkerTestException>(() =>
            MarkerTestKit.AssertAsync(new LeakyMarker(), Content("text/plain", host.Sink("note")), host.InsertAsync));
        Assert.Contains(leak.Problems, p => p.Contains("Erika Mustermann"));

        var boast = await MarkerTestKit.RunAsync(new BoastingMarker(), Content("text/html", host.Sink("note")), host.InsertAsync);
        Assert.Contains("it says the mark held, and reading back finds no mark of the record", boast.Problems);
    }

    [Fact]
    public async Task A_level_three_marker_in_the_word_pattern_is_checked_with_its_own_reader()
    {
        var document = new FakeWordDocument();
        var host = new Host();

        var result = await MarkerTestKit.AssertAsync(new WordRangeMarker(document),
            Content("text/plain", new SinkInfo("word.range", "bookmark", "custom-xml-part", "doc-property") { Container = true }), host.InsertAsync,
            (_, _) => Task.FromResult(OfficeMarks.ReadCustomXmlPart(document.Part).SingleOrDefault(r => document.Bookmarks.Contains(r.Anchor))?.RecordId));

        var id = result.Record.Record.Id;
        Assert.True(result.Outcome.Verified);
        Assert.Contains(new FoundMark(id, "word-range"), result.Found);
        Assert.Equal(OfficeMarks.AnchorName(id), Assert.Single(document.Bookmarks));
        Assert.Equal($"true {id}", document.Properties[OfficeMarks.DocumentProperty]);
    }

    /// <summary>What a Word document keeps for the marker (the object model in the add-in).</summary>
    private sealed class FakeWordDocument
    {
        public HashSet<string> Bookmarks { get; } = new();

        public string? Part { get; set; }

        public Dictionary<string, string> Properties { get; } = new();
    }

    /// <summary>
    /// The Word host marker's pattern: a hidden bookmark around the range, the signed records in the custom XML part,
    /// the document property as the flag; then read back.
    /// </summary>
    private sealed class WordRangeMarker : ContentMarker
    {
        private readonly FakeWordDocument _document;

        public WordRangeMarker(FakeWordDocument document) => _document = document;

        public override string Name => "word-range";

        public override MarkLayer Layer => MarkLayer.Metadata;

        public override bool Supports(SinkInfo sink, string mediaType) => sink.Kind == "word.range";

        public override Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
        {
            var anchor = OfficeMarks.AnchorName(record.Record.Id);
            _document.Bookmarks.Add(anchor); // Word: document.Bookmarks.Add(anchor, range)
            var records = OfficeMarks.ReadCustomXmlPart(_document.Part).Append(OfficeMarks.ToOfficeRecord(record, anchor)).ToList();
            _document.Part = OfficeMarks.CustomXmlPart(records);
            _document.Properties[OfficeMarks.DocumentProperty] = OfficeMarks.DocumentPropertyValue(records.Select(r => r.RecordId));
            var held = _document.Bookmarks.Contains(anchor) && OfficeMarks.ReadCustomXmlPart(_document.Part).Any(r => r.RecordId == record.Record.Id);
            return Task.FromResult(held ? MarkOutcome.Held(MarkLayer.Metadata, Name, "bookmark, part, property")
                : MarkOutcome.Missing(MarkLayer.Metadata, Name, ProvenanceReasons.StrippedByHost));
        }
    }

    [Fact]
    public void Office_marks_have_one_format_for_every_marker_and_reader()
    {
        const string id = "5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1";
        Assert.Equal("_faden_ai_5a0e2b5c7f3e4a519a406a1c", OfficeMarks.AnchorName(id));
        Assert.True(OfficeMarks.IsAnchor(OfficeMarks.AnchorName(id)));
        Assert.True(OfficeMarks.IsAnchor(OfficeMarks.AnchorName("rec-1"))); // a short id is hashed to 24 hex digits
        Assert.False(OfficeMarks.IsAnchor("_faden_ai_5a0e-2b5c"));
        Assert.True(OfficeMarks.AnchorName(id).Length <= 40 && OfficeMarks.AnchorName(id).All(c => char.IsLetterOrDigit(c) || c == '_'));

        var many = Enumerable.Range(1, 20).Select(n => $"5a0e2b5c-7f3e-4a51-9a40-{n:D12}").ToList();
        var flag = OfficeMarks.DocumentPropertyValue(many);
        Assert.True(flag.Length <= OfficeMarks.DocumentPropertyMaxLength);
        Assert.StartsWith($"true {many[19]} {many[18]}", flag); // the newest first

        var part = OfficeMarks.CustomXmlPart(new[] { new OfficeRecord(id, OfficeMarks.AnchorName(id), "eyJ.e30.sig", "MIIC") });
        Assert.Contains(id, part); // the server's detector finds ids in a part of this namespace
        Assert.Equal(new[] { new OfficeRecord(id, OfficeMarks.AnchorName(id), "eyJ.e30.sig", "MIIC") }, OfficeMarks.ReadCustomXmlPart(part));
        Assert.Empty(OfficeMarks.ReadCustomXmlPart("<provenance><record id=\"" + id + "\"/></provenance>")); // another namespace
        Assert.Empty(OfficeMarks.ReadCustomXmlPart("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>"));
        Assert.Empty(OfficeMarks.ReadCustomXmlPart("<provenance"));
    }

    private static MarkOutcome Watermark(Chat.ProvenanceReceiptContent? receipt, bool isShort = false) =>
        WatermarkStatus.Of(new MarkableContent("text", "text/plain", SinkInfo.Nothing("chat")) { Receipt = receipt, IsShort = isShort });

    private static Chat.ProvenanceReceiptContent Signed(WatermarkApplied applied, string? reason = null, string? vendor = null) => new(new SignedReceipt
    {
        Receipt = new GenerationReceipt
        {
            Id = "rc-1", Watermark = new ReceiptWatermark { Applied = applied, Algorithm = "dual_key_gumbel", Reason = reason, Vendor = vendor },
        },
    });

    [Fact]
    public void The_watermark_layer_follows_the_receipt()
    {
        Assert.Equal(("vllm-dual_key_gumbel", true), (Watermark(Signed(WatermarkApplied.Model)).Method, Watermark(Signed(WatermarkApplied.Model)).Applied));
        Assert.Equal("upstream-anthropic", Watermark(Signed(WatermarkApplied.Upstream, vendor: "anthropic")).Method);
        Assert.Equal(ProvenanceReasons.NoWatermarkRemoteModel, Watermark(Signed(WatermarkApplied.None, ProvenanceReasons.NoWatermarkRemoteModel)).Reason);
        Assert.Equal(ProvenanceReasons.ShortText, Watermark(Signed(WatermarkApplied.Model), isShort: true).Reason);
        Assert.Equal(ProvenanceReasons.ServerUnavailable, Watermark(new Chat.ProvenanceReceiptContent(null, reason: ProvenanceReasons.ServerUnavailable)).Reason);
        Assert.Equal((false, (string?)null, "no receipt"), (Watermark(null).Applied, Watermark(null).Reason, Watermark(null).Detail));
    }
}

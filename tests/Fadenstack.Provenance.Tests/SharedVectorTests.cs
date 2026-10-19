using System.Globalization;
using System.Net;
using System.Text.Json;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>
/// The marks byte for byte, with fixed inputs: the TypeScript SDK's tests (<c>vectors.test.ts</c>) hold the same
/// strings, so both SDKs write one format.
/// </summary>
public class SharedVectorTests
{
    private const string Id = "5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1";

    [Fact]
    public void Invisible_text_is_FEFF_then_FDNP_01_and_the_ids_bytes_as_variation_selectors()
    {
        const string expected = "﻿\U000E0136\U000E0134\U000E013E\U000E0140︁\U000E0125\U000E0151\U000E0120\U000E0155\U000E0122\U000E0152"
            + "\U000E0125\U000E0153\U000E011D\U000E0127\U000E0156\U000E0123\U000E0155\U000E011D\U000E0124\U000E0151\U000E0125\U000E0121\U000E011D"
            + "\U000E0129\U000E0151\U000E0124\U000E0120\U000E011D\U000E0126\U000E0151\U000E0121\U000E0153\U000E0125\U000E0125\U000E0155\U000E0121"
            + "\U000E0154\U000E0123\U000E0156\U000E0121";
        Assert.Equal(expected, UnicodeTextMarker.Encode(Id));
        Assert.Equal(42, CodePoints(expected));
        // An emoji's own variation selector just before the mark does not run into it.
        Assert.Equal(new[] { Id }, UnicodeTextMarker.Decode("Thanks ❤️" + expected));
    }

    private static int CodePoints(string text) => text.Count(c => !char.IsLowSurrogate(c));

    [Fact]
    public void Html_is_one_element_with_data_ai_generated_and_the_title_the_label_and_the_id_and_one_paragraph_is_marked_inside_it()
    {
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var german = UiStrings.For(CultureInfo.GetCultureInfo("de"));
        var en = HtmlAttributeMarker.Title(english, Id);
        Assert.Equal($"<span data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\">Hello</span>", HtmlAttributeMarker.Wrap("Hello", Id, en));
        Assert.Equal($"<p><span data-ai-generated=\"{Id}\" title=\"KI-generiert {Id}\">Hallo</span></p>",
            HtmlAttributeMarker.Wrap("<p>Hallo</p>", Id, HtmlAttributeMarker.Title(german, Id)));
        Assert.Equal($"\n<p class=\"lead\"><span data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\">Hi <b>there</b></span></p>\n",
            HtmlAttributeMarker.Wrap("\n<P class=\"lead\">Hi <b>there</b></P>\n", Id, en));
        Assert.Equal($"<div data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\"><p>One.</p><p>Two.</p></div>",
            HtmlAttributeMarker.Wrap("<p>One.</p><p>Two.</p>", Id, en));
        Assert.Equal($"<div data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\"><p>One.</p><ul><li>Two</li></ul></div>",
            HtmlAttributeMarker.Wrap("<p>One.</p><ul><li>Two</li></ul>", Id, en));
        Assert.Equal($"<div data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\"><pre>x</pre></div>", HtmlAttributeMarker.Wrap("<pre>x</pre>", Id, en));
    }

    /// <summary>One case of <c>reader-text-vectors.json</c>, which the TypeScript SDK's <c>vectors.test.ts</c> reads too.</summary>
    public sealed record ReaderTextVector(string Name, string MediaType, string Input, string? Text, string? Sha256);

    public static IEnumerable<object[]> ReaderTextVectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "reader-text-vectors.json"))).RootElement
            .EnumerateArray()
            .Select(v => new object[]
            {
                v.GetProperty("name").GetString()!,
                new ReaderTextVector(v.GetProperty("name").GetString()!, v.GetProperty("media_type").GetString()!, v.GetProperty("input").GetString()!,
                    v.GetProperty("text").GetString(), v.GetProperty("sha256").GetString()),
            });

    [Fact]
    public void The_reader_text_vectors_are_there() => Assert.True(ReaderTextVectors().Count() >= 15);

    [Theory]
    [MemberData(nameof(ReaderTextVectors))]
    public void The_text_a_reader_sees_is_the_typescript_sdks(string name, ReaderTextVector vector)
    {
        var text = ReaderText.AppliesTo(vector.MediaType) ? ContentHash.Normalize(ReaderText.Of(vector.Input, vector.MediaType)) : null;
        Assert.True(vector.Text == (text == "" ? null : text), $"{name}: '{text}'");
        Assert.Equal(vector.Sha256, ReaderText.Sha256(vector.Input, vector.MediaType));
    }

    /// <summary>One case of <c>edit-kind-vectors.json</c>, which the TypeScript SDK's <c>vectors.test.ts</c> reads too.</summary>
    public sealed record EditKindVector(string Name, string MediaType, string Original, string Revised, string Kind, string Reason);

    public static IEnumerable<object[]> EditKindVectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "edit-kind-vectors.json"))).RootElement
            .EnumerateArray()
            .Select(v => new object[]
            {
                v.GetProperty("name").GetString()!,
                new EditKindVector(v.GetProperty("name").GetString()!, v.GetProperty("media_type").GetString()!, v.GetProperty("original").GetString()!,
                    v.GetProperty("revised").GetString()!, v.GetProperty("kind").GetString()!, v.GetProperty("reason").GetString()!),
            });

    [Fact]
    public void The_edit_kind_vectors_are_there() => Assert.True(EditKindVectors().Count() >= 13);

    [Theory]
    [MemberData(nameof(EditKindVectors))]
    public void The_edit_kind_and_its_reason_are_the_typescript_sdks(string name, EditKindVector vector)
    {
        var kind = vector.Kind == "corrected" ? EditKind.Corrected : EditKind.Rewritten;
        var decided = new SimilarityEditClassifier().Classify(new EditInput(vector.Original, vector.Revised, EditKind.Rewritten, vector.MediaType));
        Assert.True(new EditDecision(kind, vector.Reason) == decided, $"{name}: {decided}");
        Assert.Equal(kind, SimilarityEditClassifier.Suggest(vector.Original, vector.Revised, vector.MediaType));
    }

    /// <summary>
    /// One later write into marked HTML of <c>mark-split-vectors.json</c>, which the TypeScript SDK's <c>vectors.test.ts</c>
    /// reads too: <c>html</c> with <c>|</c> at the place, <c>split</c> with <c>|</c> where the write goes, <c>record</c> what
    /// R1 was placed from.
    /// </summary>
    public sealed record MarkSplitVector(string Name, string Html, string Split, string Record);

    /// <summary>One record's text in HTML of <c>mark-split-vectors.json</c>: normalised as it is hashed, null for none.</summary>
    public sealed record RecordTextVector(string Name, string Html, string RecordId, string? Text);

    private const string R2 = "0b6f9a3e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";

    private static JsonElement MarkSplitFile() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "mark-split-vectors.json"))).RootElement;

    public static IEnumerable<object[]> MarkSplitVectors() =>
        MarkSplitFile().GetProperty("split").EnumerateArray().Select(v => new object[]
        {
            v.GetProperty("name").GetString()!,
            new MarkSplitVector(v.GetProperty("name").GetString()!, v.GetProperty("html").GetString()!, v.GetProperty("split").GetString()!,
                v.GetProperty("record").GetString()!),
        });

    public static IEnumerable<object[]> RecordTextVectors() =>
        MarkSplitFile().GetProperty("record_text").EnumerateArray().Select(v => new object[]
        {
            v.GetProperty("name").GetString()!,
            new RecordTextVector(v.GetProperty("name").GetString()!, v.GetProperty("html").GetString()!, v.GetProperty("record_id").GetString()!,
                v.GetProperty("text").GetString()),
        });

    [Fact]
    public void The_mark_split_vectors_are_there()
    {
        Assert.True(MarkSplitVectors().Count() >= 11);
        Assert.True(RecordTextVectors().Count() >= 7);
    }

    [Theory]
    [MemberData(nameof(MarkSplitVectors))]
    public void A_later_write_splits_the_mark_it_lands_in_as_the_typescript_sdk_does(string name, MarkSplitVector vector)
    {
        var html = vector.Html.Replace("|", "");
        var (split, at) = HtmlAttributeMarker.SplitMarksAt(html, vector.Html.IndexOf('|'));
        Assert.True(vector.Split == split.Insert(at, "|"), $"{name}: {split.Insert(at, "|")}");
        // R1's text is still its record's -- before the later write, and after it, with R2's own beside it.
        var recorded = ReaderText.Sha256(vector.Record, "text/html");
        Assert.Equal(recorded, ProvenanceReader.RecordTextSha256(html, Id));
        Assert.Equal(recorded, ProvenanceReader.RecordTextSha256(split, Id));
        var later = HtmlAttributeMarker.Wrap("We are sorry for the trouble.", R2, HtmlAttributeMarker.Title(UiStrings.For(CultureInfo.GetCultureInfo("en")), R2));
        var written = split.Insert(at, later);
        Assert.Equal(recorded, ProvenanceReader.RecordTextSha256(written, Id));
        Assert.Equal(ContentHash.TextSha256("We are sorry for the trouble."), ProvenanceReader.RecordTextSha256(written, R2));
    }

    [Fact]
    public void A_place_inside_a_tag_a_comment_or_a_character_reference_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HtmlAttributeMarker.SplitMarksAt("<p>a</p>", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtmlAttributeMarker.SplitMarksAt("a<!-- b -->c", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtmlAttributeMarker.SplitMarksAt("a&amp;b", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtmlAttributeMarker.SplitMarksAt("ab", 3));
        Assert.Equal(("a&amp;b", 6), HtmlAttributeMarker.SplitMarksAt("a&amp;b", 6));
    }

    [Theory]
    [MemberData(nameof(RecordTextVectors))]
    public void A_records_text_is_read_from_all_its_elements_as_the_typescript_sdk_reads_it(string name, RecordTextVector vector)
    {
        var text = ContentHash.Normalize(ProvenanceReader.RecordText(vector.Html, vector.RecordId));
        Assert.True(vector.Text == (text.Length == 0 ? null : text), $"{name}: '{text}'");
        Assert.Equal(vector.Text is null ? null : ContentHash.TextSha256(vector.Text), ProvenanceReader.RecordTextSha256(vector.Html, vector.RecordId));
    }

    [Fact]
    public void The_named_entities_are_html_4s_as_dotnet_decodes_them()
    {
        var names = ReaderText.EntityTable.Split(' ').Where((_, i) => i % 2 == 0).ToList();
        Assert.Equal(253, names.Count);
        Assert.All(names, name => Assert.Equal(WebUtility.HtmlDecode($"&{name};"), ReaderText.DecodeEntities($"&{name};")));
    }

    [Fact]
    public void Front_matter_has_the_two_keys_the_id_quoted_and_a_blank_line_before_a_body_without_front_matter()
    {
        Assert.Equal($"---\nai-generated: true\nai-record-id: \"{Id}\"\n---\n\n# Title\n", MarkdownFrontMatterMarker.Merge("# Title\n", Id));
        Assert.Equal($"---\ntitle: T\nai-generated: true\nai-record-id: \"{Id}\"\n---\nBody", MarkdownFrontMatterMarker.Merge("---\ntitle: T\n---\nBody", Id));
    }

    [Fact]
    public void Office_has_the_anchor_the_custom_xml_part_and_the_document_property()
    {
        var anchor = OfficeMarks.AnchorName(Id);
        Assert.Equal("_faden_ai_5a0e2b5c7f3e4a519a406a1c", anchor);
        var part = OfficeMarks.CustomXmlPart(new[] { new OfficeRecord(Id, anchor, "eyJhbGciOiJFUzI1NiJ9.e30.c2ln", "MIIBAQ==") });
        Assert.Equal("<provenance xmlns=\"https://fadenstack.com/ns/provenance/1.0\">"
            + $"<record id=\"{Id}\" anchor=\"_faden_ai_5a0e2b5c7f3e4a519a406a1c\" timestamp=\"MIIBAQ==\">eyJhbGciOiJFUzI1NiJ9.e30.c2ln</record></provenance>", part);
        Assert.Equal("<provenance xmlns=\"https://fadenstack.com/ns/provenance/1.0\" />", OfficeMarks.CustomXmlPart(Array.Empty<OfficeRecord>()));
        const string other = "0b6f9a3e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";
        Assert.Equal($"true {other} {Id}", OfficeMarks.DocumentPropertyValue(new[] { Id, other }));
    }

    // -- office-anchor-vectors.json, which the TypeScript SDK's vectors.test.ts reads too ------------------------

    private static JsonElement OfficeAnchorFile() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "office-anchor-vectors.json"))).RootElement;

    private static List<OfficeAnchorSpan> Anchors(JsonElement spans) =>
        spans.EnumerateArray().Select(a => new OfficeAnchorSpan(a.GetProperty("name").GetString()!, a.GetProperty("start").GetInt32(), a.GetProperty("end").GetInt32())).ToList();

    private static string? TextOf(string text, IReadOnlyList<OfficeSpan> spans) =>
        spans.Count == 0 ? null : string.Concat(spans.Select(s => text.Substring(s.Start, s.End - s.Start)));

    public static IEnumerable<object[]> AnchorNameVectors() => OfficeAnchorFile().GetProperty("names").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetProperty("anchor").GetString()!, v.GetProperty("base").GetString()! });

    public static IEnumerable<object[]> AnchorPartVectors() => OfficeAnchorFile().GetProperty("parts").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetProperty("anchor").GetString()!, v.GetProperty("part").GetInt32(), v.GetProperty("result").GetString()! });

    public static IEnumerable<object[]> AnchorSplitVectors() => OfficeAnchorFile().GetProperty("split").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetRawText() });

    public static IEnumerable<object[]> RecordSpanVectors() => OfficeAnchorFile().GetProperty("record_spans").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetRawText() });

    [Fact]
    public void The_office_anchor_vectors_are_there()
    {
        Assert.True(AnchorNameVectors().Count() >= 20);
        Assert.True(AnchorPartVectors().Count() >= 6);
        Assert.True(AnchorSplitVectors().Count() >= 14);
        Assert.True(RecordSpanVectors().Count() >= 10);
    }

    [Theory]
    [MemberData(nameof(AnchorNameVectors))]
    public void An_anchor_and_its_parts_are_named_as_the_typescript_sdk_names_them(string name, string? anchor, string? anchorBase)
    {
        Assert.True(anchorBase == OfficeMarks.AnchorBase(anchor), $"{name}: {OfficeMarks.AnchorBase(anchor)}");
        Assert.Equal(anchorBase is not null, OfficeMarks.IsAnchor(anchor));
        Assert.True(anchorBase is null || anchor!.Length <= OfficeMarks.AnchorMaxLength);
    }

    [Theory]
    [MemberData(nameof(AnchorPartVectors))]
    public void A_parts_name_is_the_typescript_sdks(string name, string anchor, int part, string? result)
    {
        if (result is null)
        {
            Assert.ThrowsAny<ArgumentException>(() => OfficeMarks.AnchorPart(anchor, part));
        }
        else
        {
            Assert.True(result == OfficeMarks.AnchorPart(anchor, part), name);
        }
    }

    [Theory]
    [MemberData(nameof(AnchorSplitVectors))]
    public void A_later_write_splits_the_anchors_it_lands_in_as_the_typescript_sdk_does(string name, string json)
    {
        var v = JsonDocument.Parse(json).RootElement;
        var text = v.GetProperty("text").GetString()!;
        var anchors = Anchors(v.GetProperty("anchors"));
        var written = v.GetProperty("written").EnumerateArray().Select(s => new OfficeSpan(s.GetProperty("start").GetInt32(), s.GetProperty("end").GetInt32())).ToList();
        var own = v.GetProperty("own").GetString();
        var taken = v.GetProperty("taken").EnumerateArray().Select(t => t.GetString()!).ToList();

        var split = OfficeMarks.SplitAnchors(anchors, written, own, taken);
        var now = anchors.ToDictionary(a => a.Name);
        foreach (var removed in split.Removed)
        {
            now.Remove(removed);
        }
        foreach (var set in split.Set)
        {
            now[set.Name] = set;
        }
        var after = Anchors(v.GetProperty("after")).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        Assert.True(after.SequenceEqual(now.Values.OrderBy(a => a.Name, StringComparer.Ordinal)), $"{name}: {string.Join(", ", now.Values)}");
        foreach (var record in v.GetProperty("records").EnumerateObject())
        {
            Assert.Equal(record.Value.GetString(), TextOf(text, OfficeMarks.RecordSpans(now.Values, record.Name)));
        }
        // Done again, nothing changes: what it left holds nothing written of another record's.
        var again = OfficeMarks.SplitAnchors(now.Values, written, own, taken);
        Assert.Equal((0, 0), (again.Set.Count, again.Removed.Count));
    }

    [Theory]
    [MemberData(nameof(RecordSpanVectors))]
    public void A_records_text_is_read_from_its_anchors_as_the_typescript_sdk_reads_it(string name, string json)
    {
        var v = JsonDocument.Parse(json).RootElement;
        var text = TextOf(v.GetProperty("text").GetString()!, OfficeMarks.RecordSpans(Anchors(v.GetProperty("anchors")), v.GetProperty("record").GetString()!));
        Assert.True(v.GetProperty("record_text").GetString() == text, $"{name}: '{text}'");
    }

    [Fact]
    public void Every_part_number_taken_leaves_the_text_after_the_write_without_an_anchor()
    {
        var r1 = OfficeMarks.AnchorName(Id);
        var r2 = OfficeMarks.AnchorName(R2);
        var taken = Enumerable.Range(2, 98).Select(n => OfficeMarks.AnchorPart(r1, n)).ToList();
        var split = OfficeMarks.SplitAnchors(new[] { new OfficeAnchorSpan(r1, 0, 20), new OfficeAnchorSpan(r2, 5, 10) }, new[] { new OfficeSpan(5, 10) }, r2, taken);
        Assert.Equal(new[] { new OfficeAnchorSpan(r1, 0, 5) }, split.Set);
        Assert.Empty(split.Removed);
    }

    [Fact]
    public void A_part_maps_to_its_record_in_the_custom_xml_part()
    {
        var records = OfficeMarks.ReadCustomXmlPart(OfficeMarks.CustomXmlPart(new[]
        {
            new OfficeRecord(Id, OfficeMarks.AnchorName(Id), "eyJ.e30.a"),
            new OfficeRecord(R2, "", "eyJ.e30.b"), // no anchor written: the id's
        }));
        Assert.Equal(Id, OfficeMarks.RecordOfAnchor(records, OfficeMarks.AnchorPart(OfficeMarks.AnchorName(Id), 7))?.RecordId);
        Assert.Equal(Id, OfficeMarks.RecordOfAnchor(records, "_faden_ai_" + OfficeMarks.AnchorName(Id).Substring(10).ToUpperInvariant())?.RecordId);
        Assert.Equal(R2, OfficeMarks.RecordOfAnchor(records, OfficeMarks.AnchorName(R2) + "_2")?.RecordId);
        Assert.Null(OfficeMarks.RecordOfAnchor(records, "_faden_ai_7d1e8f203a4b4c5d9e6fa0b1"));
        Assert.Null(OfficeMarks.RecordOfAnchor(records, "_Toc1"));
    }

    // -- office-shape-vectors.json, which the TypeScript SDK's vectors.test.ts reads too -------------------------

    private static JsonElement OfficeShapeFile() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "office-shape-vectors.json"))).RootElement;

    public static IEnumerable<object[]> ShapeReadVectors() => OfficeShapeFile().GetProperty("read").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetRawText() });

    public static IEnumerable<object[]> ShapeWriteVectors() => OfficeShapeFile().GetProperty("after_write").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetRawText() });

    [Fact]
    public void The_office_shape_vectors_are_there()
    {
        Assert.Equal("FADEN_AI_RECORDS", OfficeMarks.ShapeRecordsTag);
        Assert.True(ShapeReadVectors().Count() >= 8);
        Assert.True(ShapeWriteVectors().Count() >= 8);
    }

    [Theory]
    [MemberData(nameof(ShapeReadVectors))]
    public void The_records_a_shape_holds_are_read_as_the_typescript_sdk_reads_them(string name, string json)
    {
        var v = JsonDocument.Parse(json).RootElement;
        var ids = v.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!).ToList();
        Assert.True(ids.SequenceEqual(OfficeMarks.ShapeRecords(v.GetProperty("records").GetString(), v.GetProperty("record").GetString())), name);
    }

    [Theory]
    [MemberData(nameof(ShapeWriteVectors))]
    public void A_shapes_list_after_a_write_is_the_typescript_sdks(string name, string json)
    {
        var v = JsonDocument.Parse(json).RootElement;
        var recordId = v.GetProperty("record_id").GetString()!;
        var value = OfficeMarks.ShapeRecordsAfterWrite(v.GetProperty("records").GetString(), v.GetProperty("record").GetString(), recordId,
            v.GetProperty("partial").GetBoolean());
        Assert.True(v.GetProperty("value").GetString() == value, $"{name}: {value}");
        // Read back with FADEN_AI_RECORD set to the record: the list as written, the record last.
        Assert.Equal(value.Split(' '), OfficeMarks.ShapeRecords(value, recordId));
    }

    [Fact]
    public void A_shapes_list_needs_a_record_id() => Assert.Throws<ArgumentException>(() => OfficeMarks.ShapeRecordsAfterWrite(null, null, "rec-1", true));

    [Fact]
    public void What_needs_no_mark_and_how_tokens_are_counted()
    {
        foreach (var label in new[] { "Approved", "Q3 revenue", "Due in March", "已批准", "" })
        {
            Assert.True(Labels.IsLabel(label), label);
        }
        foreach (var text in new[] { "Approved.", "One two three four", new string('a', 41), "这是一个很长的中文句子内容", "Ok; next" })
        {
            Assert.False(Labels.IsLabel(text), text);
        }
        Assert.True(MediaTypes.IsCode("application/json") && MediaTypes.IsCode("text/x-python") && MediaTypes.IsCode("application/ld+json"));
        Assert.False(MediaTypes.IsCode("image/svg+xml; x=1") || MediaTypes.IsCode("text/csv") || MediaTypes.IsCode("text/x-markdown")
            || MediaTypes.IsCode("application/xhtml+xml"));
        Assert.Equal(3, TokenEstimate.Of("abcdefghi"));
        Assert.Equal(5, TokenEstimate.Of("人工智能 abc")); // four CJK characters and four others
    }

    public static TheoryData<string?, string> FailureNames => new()
    {
        { "RangeError", "RangeError" },
        { "InvalidOperationException", "InvalidOperationException" },
        { "InsertFailedException`1", "InsertFailedException_1" },
        { "Host error: field gone!", "Host_error:_field_gone_" },
        { "Fehler.beim-Einfügen", "Fehler.beim-Einf_gen" },
        { "📎Error", "__Error" },
        { new string('A', 70), new string('A', 64) },
        { "", "Error" },
        { null, "Error" },
    };

    [Theory]
    [MemberData(nameof(FailureNames))]
    public void A_failed_placements_code_is_the_errors_name_with_an_underscore_for_each_other_utf16_unit_64_at_most_Error_for_none(
        string? name, string code)
    {
        Assert.Equal(code, PlacementReport.FailureCode(name));
        Assert.Matches(PlacementReport.FailurePattern, code);
    }
}

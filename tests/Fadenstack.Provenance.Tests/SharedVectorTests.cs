using System.Globalization;
using System.Net;
using System.Text.Json;
using Fadenstack.Models;
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

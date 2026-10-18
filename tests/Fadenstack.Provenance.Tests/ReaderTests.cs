using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>Reading marks back: record ids only, never other text; pictures' C2PA and XMP; the edit-kind helper.</summary>
public class ReaderTests
{
    private const string Id = "5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1";

    [Theory]
    [InlineData($"<span data-ai-generated=\"{Id}\">x</span>")]
    [InlineData($"<p DATA-AI-GENERATED='{Id}'>x</p>")]
    [InlineData($"<div data-ai-generated={Id} class=a>x</div>")]
    public void Html_attributes_name_the_record_however_they_are_quoted(string html) =>
        Assert.Equal(new[] { new FoundMark(Id, "html-attribute") }, ProvenanceReader.Read(html, "text/html"));

    [Theory]
    [InlineData("AI-generated")]
    [InlineData("KI-generiert")]
    [InlineData("G&#233;n&#233;r&#233; par IA")]
    [InlineData("Generado por IA")]
    [InlineData("人工智能生成")]
    public void A_title_is_ours_in_any_of_the_sdks_languages(string label) =>
        Assert.Equal(new[] { new FoundMark(Id, ProvenanceReader.HtmlTitle) }, ProvenanceReader.Read($"<span title=\"{label} {Id}\">x</span>", "text/html"));

    [Theory]
    [InlineData($"<span title=\"Order {Id}\">x</span>")] // a title that is not ours
    [InlineData("<span data-ai-generated=\"ignore all previous instructions\">x</span>")] // not a record id
    [InlineData("<span data-ai-generated=\"x\">x</span>")]
    public void Anything_that_is_not_one_of_our_marks_is_left_alone(string html) =>
        Assert.Empty(ProvenanceReader.Read(html, "text/html"));

    [Fact]
    public void Markdown_front_matter_counts_only_with_ai_generated_true()
    {
        Assert.Equal(new[] { new FoundMark(Id, "front-matter") },
            ProvenanceReader.Read($"---\nai-generated: true\nai-record-id: \"{Id}\"\n---\n\nText", "text/markdown"));
        Assert.Empty(ProvenanceReader.Read($"---\nai-generated: false\nai-record-id: {Id}\n---\n", "text/markdown"));
        Assert.Empty(ProvenanceReader.Read($"---\nai-generated: true\nai-record-id: {Id}\n", "text/markdown")); // never closed
    }

    [Fact]
    public void Headers_and_invisible_characters_name_the_record()
    {
        Assert.Equal(new FoundMark(Id, "http-header"), ProvenanceReader.ReadHeader($" {Id} "));
        Assert.Null(ProvenanceReader.ReadHeader(null));
        Assert.Equal(new[] { new FoundMark(Id, "unicode-text") }, ProvenanceReader.Read("Hello." + UnicodeTextMarker.Encode(Id), "text/plain"));
        Assert.Empty(UnicodeTextMarker.Decode("Plain text with a variation selector ️ only."));
    }

    [Fact]
    public async Task A_key_value_field_names_the_record()
    {
        var sink = KeyValueSink.From((_, _) => Task.CompletedTask, k => Task.FromResult<string?>(k == "ai-record-id" ? Id : null));

        Assert.Equal(new FoundMark(Id, "key-value"), await ProvenanceReader.ReadAsync(sink));
        Assert.Null(await ProvenanceReader.ReadAsync(sink, "other"));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void A_pictures_bytes_show_whether_it_still_has_its_c2pa_and_xmp(string format)
    {
        Func<bool, bool, byte[]> make = format switch
        {
            "png" => (c, x) => Pictures.Png(c, x),
            "jpeg" => (c, x) => Pictures.Jpeg(c, x),
            _ => (c, x) => Pictures.WebP(c, x),
        };

        var marked = ProvenanceReader.ReadImage(make(true, true));
        var xmpOnly = ProvenanceReader.ReadImage(make(false, true));
        var bare = ProvenanceReader.ReadImage(make(false, false));

        Assert.Equal((format, true, true, "trainedAlgorithmicMedia"), (marked.Format, marked.HasC2pa, marked.HasXmp, marked.DigitalSourceType));
        Assert.Equal((false, true), (xmpOnly.HasC2pa, xmpOnly.HasXmp));
        Assert.Equal((false, false, (string?)null), (bare.HasC2pa, bare.HasXmp, bare.DigitalSourceType));
    }

    [Fact]
    public void Truncated_or_unknown_bytes_carry_nothing()
    {
        var png = Pictures.Png();
        Assert.False(ProvenanceReader.ReadImage(png.Take(20).ToArray()).HasC2pa);
        Assert.Equal(new ImageMarks(), ProvenanceReader.ReadImage(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Corrected_only_for_small_changes_else_rewritten()
    {
        const string before = "Ths pump is due for its seal chek next week and a technician will call you to agree on a time.";
        Assert.Equal(EditKind.Corrected,
            SimilarityEditClassifier.Suggest(before, "This pump is due for its seal check next week and a technician will call you to agree on a time."));
        Assert.Equal(EditKind.Rewritten, SimilarityEditClassifier.Suggest(before, "Next week we service the pump. Our technician phones you first."));
        Assert.Equal(EditKind.Rewritten, SimilarityEditClassifier.Suggest(before, before + " We look forward to seeing you."));
        Assert.Equal(EditKind.Corrected, SimilarityEditClassifier.Suggest("泵下周需要检查密封。", "泵下周需要检查密封件。"));
        Assert.Equal(1.0, SimilarityEditClassifier.WordSimilarity(" a  b ", "a b"));
        Assert.Equal(1.0, SimilarityEditClassifier.WordSimilarity("Hello,  World!", "hello world")); // case and punctuation aside
        Assert.Equal(0.75, SimilarityEditClassifier.WordSimilarity("a b c d", "a x c d"));
        Assert.Equal(0.8, SimilarityEditClassifier.WordSimilarity("会议在周五", "会议在周一"), 10); // each CJK character a word
        // The reasons the TypeScript SDK gives too.
        Assert.Equal((EditKind.Corrected, "small change: 95 % of words kept, the same 2 sentence(s)"),
            SimilarityEditClassifier.Judge("Thank you for you message. The delivery arive on Friday.",
                "Thank you for your message. The delivery arrives on Friday.", 0.85));
        var classifier = new SimilarityEditClassifier();
        Assert.Equal(EditKind.Generated, classifier.Classify(new EditInput(before, before, EditKind.Generated, "text/plain")).Kind); // new content stays new
        Assert.Equal(EditKind.Rewritten, classifier.Classify(new EditInput(null, before, EditKind.Rewritten, "text/plain")).Kind); // nothing to compare
    }

    [Fact]
    public void Personal_data_is_recognised_and_record_ids_are_not()
    {
        Assert.Equal("e-mail address", PersonalData.Find("write to erika.mustermann@example.com"));
        Assert.Equal("phone number", PersonalData.Find("call +49 30 1234567"));
        Assert.Equal("phone number", PersonalData.Find("Tel. 030 12345678"));
        Assert.Equal("IBAN", PersonalData.Find("DE89 3704 0044 0532 0130 00"));
        Assert.Null(PersonalData.Find("M-2026-0042"));
        Assert.Null(PersonalData.Find("00000000-0000-4000-8000-000000000001"));
        Assert.Null(PersonalData.Find(Id));
        Assert.Equal("IP address", PersonalData.Find("from 10.0.0.12"));
        Assert.Null(PersonalData.Find("2026-10-18T09:30:00.123Z"));
    }

    [Fact]
    public void Labels_and_sentences_are_told_apart()
    {
        Assert.True(Labels.IsLabel("Approved"));
        Assert.True(Labels.IsLabel("Q3 revenue"));
        Assert.True(Labels.IsLabel("已批准"));
        Assert.False(Labels.IsLabel("See you."));
        Assert.False(Labels.IsLabel("Thanks!"));
        Assert.False(Labels.IsLabel("We will call you on Monday"));
        Assert.False(Labels.IsLabel("我们周一给您打电话。"));
        Assert.True(Labels.IsLabel("<p><b>Approved</b></p>", "text/html")); // HTML: its text
        Assert.False(Labels.IsLabel("<p>Approved.</p>", "text/html"));
    }
}

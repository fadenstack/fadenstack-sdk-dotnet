using System.Collections.Specialized;
using System.Diagnostics;
using Faden.UI.Markdown;
using Xunit;

namespace Faden.UI.Tests;

public class MarkdownTests
{
    private const string Answer = """
        ## Page 12

        The **motor circuit** for *conveyor M3*: `K12`, see [the manual](https://docs.example/m3) or https://eplan.example/p12.
        Uses read_page and snake_case names.

        1. Check the contactor
        2. Check the overload relay
           - F12 trips at 4 A
           - reset by hand
        3. Done

        > Keep the cabinet closed
        > while testing.

        | Device | Rating | Page |
        |:-------|:------:|-----:|
        | K12    | 4 kW   | 12   |
        | F12 \| relay | 4 A | 12 |

        ```csharp
        var page = await ReadPageAsync(12);

        Console.WriteLine(page);
        ```

        ---
        ~~old~~ ***both***
        """;

    [Fact]
    public void Blocks_and_inlines_come_out_as_a_model_means_them()
    {
        var blocks = MarkdownParser.Parse(Answer);
        Assert.Collection(blocks,
            b => Assert.Equal(2, Assert.IsType<HeadingBlock>(b).Level),
            b =>
            {
                var runs = Assert.IsType<ParagraphBlock>(b).Inlines;
                Assert.Contains(runs, r => r.Text == "motor circuit" && r.Style == InlineStyle.Bold);
                Assert.Contains(runs, r => r.Text == "conveyor M3" && r.Style == InlineStyle.Italic);
                Assert.Contains(runs, r => r.Text == "K12" && r.Style == InlineStyle.Code);
                Assert.Contains(runs, r => r.Text == "the manual" && r.Url == "https://docs.example/m3");
                Assert.Contains(runs, r => r.Url == "https://eplan.example/p12" && r.Text == "https://eplan.example/p12");
                Assert.Contains(runs, r => r.Text.Contains("read_page and snake_case names") && r.Style == InlineStyle.None);
            },
            b =>
            {
                var list = Assert.IsType<ListBlock>(b);
                Assert.True(list.Ordered);
                Assert.Equal(new[] { (0, "1."), (0, "2."), (1, "•"), (1, "•"), (0, "3.") }, list.Items.Select(i => (i.Level, i.Marker)));
            },
            b => Assert.Equal("Keep the cabinet closed\nwhile testing.", Assert.IsType<QuoteBlock>(b).Inlines.Single().Text),
            b =>
            {
                var table = Assert.IsType<TableBlock>(b);
                Assert.Equal(new[] { ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right }, table.Alignments);
                Assert.Equal(2, table.Rows.Count);
                Assert.Equal("F12 | relay", table.Rows[1].Cells[0].Single().Text);
            },
            b =>
            {
                var code = Assert.IsType<CodeBlock>(b);
                Assert.Equal(("csharp", true), (code.Language, code.IsComplete));
                Assert.Equal("var page = await ReadPageAsync(12);\n\nConsole.WriteLine(page);", code.Code);
            },
            b => Assert.IsType<RuleBlock>(b),
            b =>
            {
                var runs = Assert.IsType<ParagraphBlock>(b).Inlines;
                Assert.Contains(runs, r => r.Text == "old" && r.Style == InlineStyle.Strike);
                Assert.Contains(runs, r => r.Text == "both" && r.Style == (InlineStyle.Bold | InlineStyle.Italic));
            });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public void Streamed_in_pieces_it_ends_as_if_parsed_at_once_and_only_the_last_block_changes(int chunk)
    {
        var document = new MarkdownDocument();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        document.Blocks.CollectionChanged += (_, e) => changes.Add(e);
        for (var i = 0; i < Answer.Length; i += chunk)
        {
            var count = document.Blocks.Count;
            document.Append(Answer.Substring(i, Math.Min(chunk, Answer.Length - i)));
            Assert.All(changes, c => Assert.True(
                c.Action == NotifyCollectionChangedAction.Add && c.NewStartingIndex >= count - 1
                || c.Action == NotifyCollectionChangedAction.Replace && c.NewStartingIndex == count - 1,
                $"{c.Action} at {c.NewStartingIndex} of {count}"));
            changes.Clear();
        }
        Assert.Equal(Describe(MarkdownParser.Parse(Answer)), Describe(document.Blocks));
        Assert.Equal(Answer, document.Text);
    }

    [Fact]
    public void An_unclosed_fence_is_one_growing_code_block()
    {
        var document = new MarkdownDocument("Here:\n\n```python\nprint(1)\n");
        var code = Assert.IsType<CodeBlock>(document.Blocks.Last());
        Assert.Equal(("print(1)", false), (code.Code, code.IsComplete));
        document.Append("print(2)\n```\nAfter.");
        Assert.True(Assert.IsType<CodeBlock>(document.Blocks[1]).IsComplete);
        Assert.IsType<ParagraphBlock>(document.Blocks[2]);
    }

    [Fact]
    public void A_long_answer_streams_at_the_cost_of_its_last_block()
    {
        var paragraph = string.Concat(Enumerable.Repeat("Word **bold** and `code` here. ", 12)) + "\n\n";
        var text = string.Concat(Enumerable.Repeat(paragraph, 600)); // ~250 KB, 600 blocks
        var document = new MarkdownDocument();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < text.Length; i += 40)
        {
            document.Append(text.Substring(i, Math.Min(40, text.Length - i)));
        }
        watch.Stop();
        Assert.Equal(600, document.Blocks.Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms for {text.Length / 40} updates");
    }

    private static string Describe(IEnumerable<MarkdownBlock> blocks) => string.Join("\n", blocks.Select(b => b switch
    {
        ParagraphBlock p => "P " + string.Concat(p.Inlines.Select(r => $"[{r.Style}:{r.Text}]")),
        HeadingBlock h => $"H{h.Level} " + string.Concat(h.Inlines.Select(r => r.Text)),
        CodeBlock c => $"C {c.Language} {c.IsComplete} {c.Code}",
        ListBlock l => "L " + string.Join("|", l.Items.Select(i => $"{i.Level}{i.Marker}{string.Concat(i.Inlines.Select(r => r.Text))}")),
        QuoteBlock q => "Q " + string.Concat(q.Inlines.Select(r => r.Text)),
        TableBlock t => $"T {t.ColumnCount}x{t.Rows.Count}",
        _ => b.GetType().Name,
    }));
}

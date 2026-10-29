using System.Collections.Specialized;
using System.Diagnostics;
using Fadenstack.UI.Markdown;
using Xunit;

namespace Fadenstack.UI.Tests;

public class MarkdownTests
{
    private const string Answer = """
        ## Page 12

        The **sales report** for *region M3*: `K12`, see [the manual](https://docs.example/m3) or https://docs.example/p12.
        Uses read_page and snake_case names.

        1. Check the table
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
                Assert.Contains(runs, r => r.Text == "sales report" && r.Style == InlineStyle.Bold);
                Assert.Contains(runs, r => r.Text == "region M3" && r.Style == InlineStyle.Italic);
                Assert.Contains(runs, r => r.Text == "K12" && r.Style == InlineStyle.Code);
                Assert.Contains(runs, r => r.Text == "the manual" && r.Url == "https://docs.example/m3");
                Assert.Contains(runs, r => r.Url == "https://docs.example/p12" && r.Text == "https://docs.example/p12");
                Assert.Contains(runs, r => r.Text.Contains("read_page and snake_case names") && r.Style == InlineStyle.None);
            },
            b =>
            {
                var list = Assert.IsType<ListBlock>(b);
                Assert.True(list.Ordered);
                Assert.Equal(new[] { (0, "1."), (0, "2."), (1, "•"), (1, "•"), (0, "3.") }, list.Items.Select(i => (i.Level, i.Marker)));
            },
            b => Assert.Equal("Keep the cabinet closed\nwhile testing.", Assert.IsType<ParagraphBlock>(Assert.IsType<QuoteBlock>(b).Blocks.Single()).Inlines.Single().Text),
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

    private const string Table = "| A | B |\n|---|---|\n| 1 | 2 |";

    private static T Only<T>(IEnumerable<MarkdownBlock> blocks) where T : MarkdownBlock => Assert.IsType<T>(Assert.Single(blocks));

    private static string PlainText(MarkdownBlock block) => Assert.Single(Assert.IsType<ParagraphBlock>(block).Inlines).Text;

    [Fact]
    public void A_quote_holds_a_table()
    {
        var quote = Only<QuoteBlock>(MarkdownParser.Parse("> Note:\n>\n> " + Table.Replace("\n", "\n> ")));
        Assert.Collection(quote.Blocks, b => Assert.Equal("Note:", PlainText(b)), b =>
        {
            var table = Assert.IsType<TableBlock>(b);
            Assert.Equal((2, 1), (table.ColumnCount, table.Rows.Count));
        });
    }

    [Fact]
    public void A_quote_holds_a_list_with_a_nested_list()
    {
        var quote = Only<QuoteBlock>(MarkdownParser.Parse("> - one\n>   - inner\n> - two"));
        var list = Only<ListBlock>(quote.Blocks);
        Assert.Equal(new[] { (0, "one"), (1, "inner"), (0, "two") }, list.Items.Select(i => (i.Level, PlainText(Assert.Single(i.Blocks)))));
    }

    [Fact]
    public void A_list_item_holds_a_table_and_a_code_block()
    {
        var text = "1. Check the values\n\n   " + Table.Replace("\n", "\n   ") + "\n\n   ```csharp\n   var x = 1;\n\n   var y = 2;\n   ```\n2. Done";
        var list = Only<ListBlock>(MarkdownParser.Parse(text));
        Assert.Equal(2, list.Items.Count);
        Assert.Collection(list.Items[0].Blocks,
            b => Assert.Equal("Check the values", PlainText(b)),
            b => Assert.Equal(2, Assert.IsType<TableBlock>(b).ColumnCount),
            b => Assert.Equal(("csharp", "var x = 1;\n\nvar y = 2;"), (Assert.IsType<CodeBlock>(b).Language, ((CodeBlock)b).Code)));
        Assert.Equal("Done", PlainText(Assert.Single(list.Items[1].Blocks)));
    }

    [Fact]
    public void Nesting_parses_down_to_the_cap_and_deeper_is_plain_text()
    {
        Assert.Equal(3, MarkdownParser.MaxNestingDepth);
        // quote > list > quote: three containers, still parsed.
        var outer = Only<QuoteBlock>(MarkdownParser.Parse("> - item\n>   > inner"));
        var item = Assert.Single(Only<ListBlock>(outer.Blocks).Items);
        Assert.Equal("item", PlainText(item.Blocks[0]));
        var inner = Assert.IsType<QuoteBlock>(item.Blocks[1]);
        Assert.Equal("inner", PlainText(Assert.Single(inner.Blocks)));

        // A fourth level is the raw text of the third container's content: no list, no emphasis.
        var deep = Assert.IsType<QuoteBlock>(Only<ListBlock>(Only<QuoteBlock>(MarkdownParser.Parse("> - item\n>   > inner\n>   > - **four**")).Blocks).Items.Single().Blocks[1]);
        Assert.Equal("inner\n- **four**", PlainText(Assert.Single(deep.Blocks)));
    }

    [Fact]
    public void A_runaway_nesting_stays_cheap_and_bounded()
    {
        var blocks = MarkdownParser.Parse(string.Concat(Enumerable.Repeat("> ", 500)) + "text");
        MarkdownBlock block = Assert.Single(blocks);
        var levels = 0;
        while (block is QuoteBlock q)
        {
            levels++;
            block = Assert.Single(q.Blocks);
        }
        Assert.Equal(3, levels);
        Assert.IsType<ParagraphBlock>(block);
    }

    [Fact]
    public void A_markdown_fence_is_a_preview_of_its_content()
    {
        var preview = Only<PreviewBlock>(MarkdownParser.Parse("```markdown\n# Title\n\n- a\n- b\n\n" + Table + "\n```"));
        Assert.Equal(("markdown", true), (preview.Source.Language, preview.Source.IsComplete));
        Assert.Equal(new[] { "HeadingBlock", "ListBlock", "TableBlock" }, preview.Blocks.Select(b => b.GetType().Name));
        Assert.IsType<PreviewBlock>(Assert.Single(MarkdownParser.Parse("```md\ntext\n```")));
        Assert.IsType<CodeBlock>(Assert.Single(MarkdownParser.Parse("```csharp\ntext\n```")));
    }

    [Fact]
    public void A_preview_counts_as_a_level_of_nesting()
    {
        // quote > preview > quote: the third container's content is plain.
        var quote = Only<QuoteBlock>(MarkdownParser.Parse("> ```md\n> > - x\n> ```"));
        var preview = Only<PreviewBlock>(quote.Blocks);
        var inner = Only<QuoteBlock>(preview.Blocks);
        Assert.Equal("- x", PlainText(Assert.Single(inner.Blocks)));
    }

    [Fact]
    public void A_mermaid_fence_is_a_code_block_marked_as_a_diagram()
    {
        var code = Only<CodeBlock>(MarkdownParser.Parse("```mermaid\ngraph TD; A-->B;\n```"));
        Assert.True(code.IsDiagram);
        Assert.Equal("graph TD; A-->B;", code.Code);
        Assert.False(Only<CodeBlock>(MarkdownParser.Parse("```csharp\nx\n```")).IsDiagram);
    }

    [Fact]
    public void The_texts_of_the_preview_switch_and_the_diagram_header_are_in_every_language()
    {
        foreach (var language in UiStrings.BuiltInLanguages)
        {
            var strings = UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language));
            Assert.All(new[] { strings.MarkdownPreview, strings.MarkdownSource, strings.DiagramMermaid }, t => Assert.False(string.IsNullOrWhiteSpace(t)));
            Assert.Contains("Mermaid", strings.DiagramMermaid);
        }
        Assert.NotEqual(UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("en")).MarkdownPreview,
            UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("de")).MarkdownPreview);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_nested_answer_streams_by_replacing_only_its_last_block(int chunk)
    {
        var text = "Intro.\n\n> - one\n>   " + "| A | B |\n>   |---|---|\n>   | 1 | 2 |\n\nMiddle.\n\n1. Step\n\n   ```md\n   # Title\n   ```\n\nEnd.";
        var document = new MarkdownDocument();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        document.Blocks.CollectionChanged += (_, e) => changes.Add(e);
        for (var i = 0; i < text.Length; i += chunk)
        {
            var count = document.Blocks.Count;
            document.Append(text.Substring(i, Math.Min(chunk, text.Length - i)));
            Assert.All(changes, c => Assert.True(
                c.Action == NotifyCollectionChangedAction.Add && c.NewStartingIndex >= count - 1
                || c.Action == NotifyCollectionChangedAction.Replace && c.NewStartingIndex == count - 1,
                $"{c.Action} at {c.NewStartingIndex} of {count}"));
            changes.Clear();
        }
        Assert.Equal(Describe(MarkdownParser.Parse(text)), Describe(document.Blocks));
    }

    private static string Describe(IEnumerable<MarkdownBlock> blocks) => string.Join("\n", blocks.Select(b => b switch
    {
        ParagraphBlock p => "P " + string.Concat(p.Inlines.Select(r => $"[{r.Style}:{r.Text}]")),
        HeadingBlock h => $"H{h.Level} " + string.Concat(h.Inlines.Select(r => r.Text)),
        CodeBlock c => $"C {c.Language} {c.IsComplete} {c.Code}",
        ListBlock l => "L " + string.Join("|", l.Items.Select(i => $"{i.Level}{i.Marker}({Describe(i.Blocks)})")),
        QuoteBlock q => "Q (" + Describe(q.Blocks) + ")",
        PreviewBlock v => "V (" + Describe(v.Blocks) + ")",
        TableBlock t => $"T {t.ColumnCount}x{t.Rows.Count}",
        _ => b.GetType().Name,
    }));
}

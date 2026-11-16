using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.UI.Markdown;

/// <summary>
/// Markdown blocks as HTML for another program -- the clipboard, a page's rich-text editor, a document -- never for this
/// UI, which renders the blocks itself. Built from the parsed blocks, so it holds only what they hold: text is escaped,
/// links are http(s) or mailto, images are their alt text (never loaded), and there are no attributes but <c>href</c>, a
/// list's <c>start</c> and a cell's alignment. The same text gives the same HTML as the TypeScript SDK's
/// <c>markdownToHtml</c> (both are tested against the same cases, <c>vectors/markdown-html.json</c>). TS: <c>markdownToHtml</c>,
/// <c>toHtml</c>, <c>tableToTsv</c>, <c>tablesOf</c> in <c>@fadenstack/ui</c>.
/// </summary>
public static class MarkdownSerializer
{
    private static readonly Regex SafeLink = new(@"^(https?:|mailto:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Box = new(@"^\[([ xX])\][ \t]+", RegexOptions.CultureInvariant);

    /// <summary>Markdown as HTML (see the class).</summary>
    public static string MarkdownToHtml(string source) => ToHtml(MarkdownParser.Parse(source ?? ""));

    /// <summary>The blocks as HTML.</summary>
    public static string ToHtml(IEnumerable<MarkdownBlock> blocks)
    {
        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            Write(html, block);
        }
        return html.ToString();
    }

    /// <summary>
    /// A table as tab-separated values, the header first: pasted into a spreadsheet, each cell lands in its own cell. Tabs and
    /// line breaks inside a cell become spaces.
    /// </summary>
    public static string TableToTsv(TableBlock table) =>
        string.Join("\n", new[] { table.Header }.Concat(table.Rows).Select(row => string.Join("\t", row.Cells.Select(CellText))));

    /// <summary>A cell's text as the copy button takes it: no marks, tabs and line breaks as spaces, trimmed.</summary>
    public static string CellText(IReadOnlyList<InlineRun> cell) =>
        Regex.Replace(string.Concat(cell.Select(r => r.Text)), @"[\t\r\n]+", " ").Trim();

    /// <summary>The tables among blocks, in order, also those inside quotes, lists and previews.</summary>
    public static IReadOnlyList<TableBlock> TablesIn(IEnumerable<MarkdownBlock> blocks)
    {
        var found = new List<TableBlock>();
        void Visit(IEnumerable<MarkdownBlock> list)
        {
            foreach (var block in list)
            {
                switch (block)
                {
                    case TableBlock table:
                        found.Add(table);
                        break;
                    case QuoteBlock quote:
                        Visit(quote.Blocks);
                        break;
                    case PreviewBlock preview:
                        Visit(preview.Blocks);
                        break;
                    case ListBlock list2:
                        foreach (var item in list2.Items)
                        {
                            Visit(item.Blocks);
                        }
                        break;
                }
            }
        }
        Visit(blocks);
        return found;
    }

    private static void Write(StringBuilder html, MarkdownBlock block)
    {
        switch (block)
        {
            case ParagraphBlock p:
                html.Append("<p>").Append(Inlines(p.Inlines)).Append("</p>");
                break;
            case HeadingBlock h:
                var level = Math.Min(6, Math.Max(1, h.Level));
                html.Append("<h").Append(level).Append('>').Append(Inlines(h.Inlines)).Append("</h").Append(level).Append('>');
                break;
            case CodeBlock code:
                html.Append("<pre><code>").Append(Escape(code.Code)).Append("</code></pre>");
                break;
            case PreviewBlock preview:
                html.Append("<pre><code>").Append(Escape(preview.Source.Code)).Append("</code></pre>");
                break;
            case ListBlock list:
                var next = 0;
                while (next < list.Items.Count)
                {
                    WriteList(html, list.Items, ref next);
                }
                break;
            case QuoteBlock quote:
                html.Append("<blockquote>").Append(ToHtml(quote.Blocks)).Append("</blockquote>");
                break;
            case TableBlock table:
                WriteTable(html, table);
                break;
            case RuleBlock:
                html.Append("<hr>");
                break;
        }
    }

    /// <summary>
    /// One list of items at the level of <c>items[i]</c>, with the deeper items after each as the list inside it: a list item
    /// that holds a list has its text as a paragraph, as the TypeScript SDK's blocks have it.
    /// </summary>
    private static void WriteList(StringBuilder html, IReadOnlyList<ListItem> items, ref int i)
    {
        var level = items[i].Level;
        var marker = items[i].Marker;
        var ordered = marker.Length > 0 && char.IsDigit(marker[0]);
        var start = ordered && int.TryParse(marker.TrimEnd('.', ')'), out var n) ? n : 1;
        var tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag).Append(ordered && start != 1 ? $" start=\"{start}\"" : "").Append('>');
        while (i < items.Count && items[i].Level >= level)
        {
            var item = items[i++];
            var inner = new StringBuilder();
            while (i < items.Count && items[i].Level > item.Level)
            {
                WriteList(inner, items, ref i);
            }
            html.Append("<li>").Append(Item(item.Blocks, inner.ToString())).Append("</li>");
        }
        html.Append("</").Append(tag).Append('>');
    }

    private static string Item(IReadOnlyList<MarkdownBlock> blocks, string nested)
    {
        var box = "";
        if (blocks.Count > 0 && blocks[0] is ParagraphBlock first && first.Inlines.Count > 0 && Box.Match(first.Inlines[0].Text) is { Success: true } task
            && first.Inlines[0].Style == InlineStyle.None)
        {
            box = task.Groups[1].Value == " " ? "☐ " : "☑ ";
            var runs = new List<InlineRun> { first.Inlines[0] with { Text = first.Inlines[0].Text.Substring(task.Length) } };
            runs.AddRange(first.Inlines.Skip(1));
            blocks = new MarkdownBlock[] { new ParagraphBlock(runs.Where(r => r.Text.Length > 0).ToList()) }.Concat(blocks.Skip(1)).ToList();
        }
        // A paragraph alone in an item is its text (as editors write a tight list).
        var body = blocks.Count == 1 && blocks[0] is ParagraphBlock only && nested.Length == 0 ? Inlines(only.Inlines) : ToHtml(blocks);
        return box + body + nested;
    }

    private static void WriteTable(StringBuilder html, TableBlock table)
    {
        string Align(int column) => column < table.Alignments.Count && table.Alignments[column] switch
        {
            ColumnAlignment.Left => "left",
            ColumnAlignment.Center => "center",
            ColumnAlignment.Right => "right",
            _ => "",
        } is { Length: > 0 } a ? $" style=\"text-align:{a}\"" : "";
        html.Append("<table><thead><tr>");
        for (var c = 0; c < table.Header.Cells.Count; c++)
        {
            html.Append("<th").Append(Align(c)).Append('>').Append(Inlines(table.Header.Cells[c])).Append("</th>");
        }
        html.Append("</tr></thead><tbody>");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            for (var c = 0; c < row.Cells.Count; c++)
            {
                html.Append("<td").Append(Align(c)).Append('>').Append(Inlines(row.Cells[c])).Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
    }

    /// <summary>
    /// The runs as HTML. Elements open in the order link, bold, italic, strike, and stay open across the runs that share them
    /// (<c>**a *b* c**</c> is one <c>strong</c>); code is always innermost. A link that is not http(s) or mailto is its text.
    /// </summary>
    private static string Inlines(IReadOnlyList<InlineRun> runs)
    {
        var html = new StringBuilder();
        var open = new List<(string Tag, string? Url)>();
        foreach (var run in runs)
        {
            var want = new List<(string Tag, string? Url)>();
            if (run.Style.HasFlag(InlineStyle.Link) && run.Url is { } url && SafeLink.IsMatch(url.Trim()))
            {
                want.Add(("a", url.Trim()));
            }
            if (run.Style.HasFlag(InlineStyle.Bold))
            {
                want.Add(("strong", null));
            }
            if (run.Style.HasFlag(InlineStyle.Italic))
            {
                want.Add(("em", null));
            }
            if (run.Style.HasFlag(InlineStyle.Strike))
            {
                want.Add(("del", null));
            }
            var common = 0;
            while (common < open.Count && common < want.Count && open[common] == want[common])
            {
                common++;
            }
            for (var k = open.Count - 1; k >= common; k--)
            {
                html.Append("</").Append(open[k].Tag).Append('>');
            }
            open.RemoveRange(common, open.Count - common);
            for (var k = common; k < want.Count; k++)
            {
                html.Append('<').Append(want[k].Tag).Append(want[k].Url is { } href ? $" href=\"{Escape(href)}\"" : "").Append('>');
                open.Add(want[k]);
            }
            html.Append(run.Style.HasFlag(InlineStyle.Code) ? "<code>" + Escape(run.Text) + "</code>" : Escape(run.Text));
        }
        for (var k = open.Count - 1; k >= 0; k--)
        {
            html.Append("</").Append(open[k].Tag).Append('>');
        }
        return html.ToString();
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return escaped.ToString();
    }
}

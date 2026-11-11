using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.UI.Markdown;

/// <summary>
/// The Markdown models write: GitHub-flavoured blocks (headings, paragraphs, fenced code, lists, quotes,
/// tables, rules) and inlines (bold, italic, code, strike, links). Never throws: anything it does not
/// understand is text. Blocks come back with their offsets, so a streaming document re-parses only its tail.
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})[ \t]*([^\s`]*)");
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?[ \t#]*$");
    private static readonly Regex Rule = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$");
    private static readonly Regex Item = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$");
    private static readonly Regex Quote = new(@"^ {0,3}>[ ]?(.*)$");
    private static readonly Regex TableSeparator = new(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$");

    /// <summary>
    /// How many containers (a quote, a list item, a Markdown preview) may sit inside each other: quote > list > quote is
    /// the deepest that still parses. The content of a container at this depth is one plain-text block, the raw lines,
    /// not parsed again. Model output nests a level or two; a deeper tree is rare, costs a control per level to show,
    /// and a hostile or runaway answer (a thousand ">") must not make the parser or the skin pay for it, so the cap
    /// keeps both bounded and the text stays readable.
    /// </summary>
    public const int MaxNestingDepth = 3;

    /// <summary>The blocks of <paramref name="text"/>.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string text) => ParseWithOffsets(text).Select(b => b.Block).ToList();

    /// <summary>The blocks and where each starts in <paramref name="text"/>.</summary>
    public static List<(MarkdownBlock Block, int Offset)> ParseWithOffsets(string text) => ParseLines(Lines(text), 0);

    /// <summary>The blocks of some lines; <paramref name="depth"/> is how many containers they are inside.</summary>
    private static List<(MarkdownBlock Block, int Offset)> ParseLines(List<(string Line, int Offset)> lines, int depth)
    {
        if (depth >= MaxNestingDepth)
        {
            return PlainText(lines);
        }
        var blocks = new List<(MarkdownBlock, int)>();
        var i = 0;
        while (i < lines.Count)
        {
            var (line, offset) = lines[i];
            if (IsBlank(line))
            {
                i++;
                continue;
            }
            MarkdownBlock block;
            if (Fence.Match(line) is { Success: true } fence)
            {
                var code = ReadFence(lines, ref i, fence);
                block = IsMarkdown(code.Language)
                    ? new PreviewBlock(code, Children(code.Code.Split('\n'), depth + 1))
                    : code;
            }
            else if (Heading.Match(line) is { Success: true } heading)
            {
                block = new HeadingBlock(heading.Groups[1].Length, Inlines(heading.Groups[2].Value));
                i++;
            }
            else if (Rule.IsMatch(line))
            {
                block = new RuleBlock();
                i++;
            }
            else if (Quote.IsMatch(line))
            {
                var inner = new List<string>();
                while (i < lines.Count && Quote.Match(lines[i].Line) is { Success: true } q)
                {
                    inner.Add(q.Groups[1].Value);
                    i++;
                }
                block = new QuoteBlock(Children(inner, depth + 1));
            }
            else if (Item.IsMatch(line))
            {
                block = ReadList(lines, ref i, depth);
            }
            else if (i + 1 < lines.Count && line.IndexOf('|') >= 0 && TableSeparator.IsMatch(lines[i + 1].Line)
                && lines[i + 1].Line.IndexOf('-') >= 0)
            {
                block = ReadTable(lines, ref i);
            }
            else
            {
                var para = new StringBuilder();
                while (i < lines.Count && !IsBlank(lines[i].Line) && (para.Length == 0 || !StartsBlock(lines, i)))
                {
                    AppendLine(para, lines[i].Line.Trim());
                    i++;
                }
                block = new ParagraphBlock(Inlines(para.ToString()));
            }
            blocks.Add((block, offset));
        }
        return blocks;
    }

    /// <summary>The content of a container, parsed one level deeper.</summary>
    private static IReadOnlyList<MarkdownBlock> Children(IEnumerable<string> content, int depth)
    {
        var lines = new List<(string, int)>();
        foreach (var line in content)
        {
            lines.Add((line, 0));
        }
        return ParseLines(lines, depth).Select(b => b.Block).ToList();
    }

    /// <summary>The lines as one block of text, unparsed (the content of a container at <see cref="MaxNestingDepth"/>).</summary>
    private static List<(MarkdownBlock Block, int Offset)> PlainText(List<(string Line, int Offset)> lines)
    {
        var first = 0;
        var last = lines.Count - 1;
        while (first <= last && IsBlank(lines[first].Line))
        {
            first++;
        }
        while (last >= first && IsBlank(lines[last].Line))
        {
            last--;
        }
        if (first > last)
        {
            return new List<(MarkdownBlock, int)>();
        }
        var text = new StringBuilder();
        for (var k = first; k <= last; k++)
        {
            AppendLine(text, lines[k].Line.TrimEnd());
        }
        return new List<(MarkdownBlock, int)> { (new ParagraphBlock(new[] { new InlineRun(text.ToString()) }), lines[first].Offset) };
    }

    private static bool IsMarkdown(string? language) =>
        string.Equals(language, "markdown", StringComparison.OrdinalIgnoreCase) || string.Equals(language, "md", StringComparison.OrdinalIgnoreCase);

    private static CodeBlock ReadFence(List<(string Line, int Offset)> lines, ref int i, Match open)
    {
        var marker = open.Groups[1].Value;
        var language = open.Groups[2].Value;
        var code = new StringBuilder();
        var firstLine = true;
        i++;
        while (i < lines.Count)
        {
            var line = lines[i].Line;
            var trimmed = line.TrimStart(' ');
            if (line.Length - trimmed.Length <= 3 && trimmed.StartsWith(marker, StringComparison.Ordinal)
                && trimmed.TrimEnd().Trim(marker[0]).Length == 0)
            {
                i++;
                return new CodeBlock(language.Length == 0 ? null : language, code.ToString(), true);
            }
            if (!firstLine)
            {
                code.Append('\n');
            }
            code.Append(line);
            firstLine = false;
            i++;
        }
        return new CodeBlock(language.Length == 0 ? null : language, code.ToString(), false);
    }

    /// <summary>One list item while reading: its marker and the lines of its content, indent removed.</summary>
    private sealed class ItemLines
    {
        public ItemLines(int indent, string marker, string first)
        {
            Indent = indent;
            Marker = marker;
            Lines.Add(first);
            Track(first);
        }

        public int Indent { get; }

        public string Marker { get; }

        public List<string> Lines { get; } = new();

        /// <summary>Inside an open fenced block of the item: its blank lines and indentation belong to the code.</summary>
        public bool InFence { get; private set; }

        private string _fence = "";

        public void Add(string line)
        {
            Lines.Add(line);
            Track(line);
        }

        private void Track(string line)
        {
            if (Fence.Match(line) is not { Success: true } f)
            {
                return;
            }
            var marker = f.Groups[1].Value;
            if (!InFence)
            {
                InFence = true;
                _fence = marker;
            }
            else if (marker[0] == _fence[0] && marker.Length >= _fence.Length && line.Trim().Trim(marker[0]).Length == 0)
            {
                InFence = false;
            }
        }
    }

    private static ListBlock ReadList(List<(string Line, int Offset)> lines, ref int i, int depth)
    {
        var items = new List<ItemLines>();
        var ordered = Item.Match(lines[i].Line).Groups[2].Value is var first && char.IsDigit(first[0]);
        while (i < lines.Count)
        {
            var line = lines[i].Line;
            var current = items.Count > 0 ? items[items.Count - 1] : null;
            if (IsBlank(line))
            {
                var next = i + 1;
                while (next < lines.Count && IsBlank(lines[next].Line))
                {
                    next++;
                }
                if (next < lines.Count && current is not null)
                {
                    var after = lines[next].Line;
                    if (current.InFence ? Indent(Leading(after)) > current.Indent : !Item.IsMatch(after) && Indent(Leading(after)) > current.Indent)
                    {
                        // Blank lines inside an item (between its paragraphs, in its code) belong to it.
                        for (var k = i; k < next; k++)
                        {
                            current.Add("");
                        }
                        i = next;
                        continue;
                    }
                    if (Item.IsMatch(after))
                    {
                        i = next; // a loose list goes on
                        continue;
                    }
                }
                break;
            }
            var indent = Indent(Leading(line));
            if (current is not null && current.InFence)
            {
                if (indent <= current.Indent)
                {
                    break;
                }
                current.Add(Strip(line, current.Indent + current.Marker.Length + 1));
                i++;
                continue;
            }
            if (Item.Match(line) is { Success: true } item)
            {
                var marker = item.Groups[2].Value;
                if (current is not null && Indent(item.Groups[1].Value) == 0 && char.IsDigit(marker[0]) != ordered)
                {
                    break; // a different kind of list starts
                }
                items.Add(new ItemLines(Indent(item.Groups[1].Value), marker, item.Groups[3].Value.TrimEnd()));
                i++;
                continue;
            }
            if (current is not null && indent > current.Indent)
            {
                current.Add(Strip(line, current.Indent + current.Marker.Length + 1)); // indented past the marker: the item's content
                i++;
                continue;
            }
            if (current is not null && !StartsBlock(lines, i))
            {
                current.Add(line.Trim()); // a lazy continuation of the item's text
                i++;
                continue;
            }
            break;
        }
        // Nesting follows indentation, relative to the items seen before.
        var levels = new List<int>();
        var result = new List<ListItem>();
        foreach (var item in items)
        {
            while (levels.Count > 0 && levels[levels.Count - 1] > item.Indent)
            {
                levels.RemoveAt(levels.Count - 1);
            }
            if (levels.Count == 0 || levels[levels.Count - 1] < item.Indent)
            {
                levels.Add(item.Indent);
            }
            var shown = char.IsDigit(item.Marker[0]) ? item.Marker.TrimEnd(')', '.') + "." : "•";
            result.Add(new ListItem(levels.Count - 1, shown, Children(item.Lines, depth + 1)));
        }
        return new ListBlock(ordered, result);
    }

    private static string Leading(string line)
    {
        var k = 0;
        while (k < line.Length && (line[k] == ' ' || line[k] == '\t'))
        {
            k++;
        }
        return line.Substring(0, k);
    }

    /// <summary>The line without up to <paramref name="columns"/> columns of leading whitespace.</summary>
    private static string Strip(string line, int columns)
    {
        var width = 0;
        var k = 0;
        while (k < line.Length && width < columns && (line[k] == ' ' || line[k] == '\t'))
        {
            width += line[k] == '\t' ? 4 : 1;
            k++;
        }
        return line.Substring(k);
    }

    private static TableBlock ReadTable(List<(string Line, int Offset)> lines, ref int i)
    {
        var header = Cells(lines[i].Line);
        var alignments = Cells(lines[i + 1].Line).Select(c =>
        {
            var t = c.Trim();
            return t.StartsWith(":", StringComparison.Ordinal) && t.EndsWith(":", StringComparison.Ordinal) ? ColumnAlignment.Center
                : t.EndsWith(":", StringComparison.Ordinal) ? ColumnAlignment.Right : ColumnAlignment.Left;
        }).ToList();
        i += 2;
        var rows = new List<TableRow>();
        while (i < lines.Count && !IsBlank(lines[i].Line) && lines[i].Line.IndexOf('|') >= 0)
        {
            var cells = Cells(lines[i].Line);
            rows.Add(Row(cells, header.Count));
            i++;
        }
        while (alignments.Count < header.Count)
        {
            alignments.Add(ColumnAlignment.Left);
        }
        return new TableBlock(Row(header, header.Count), rows, alignments.Take(header.Count).ToList());
    }

    private static TableRow Row(List<string> cells, int count)
    {
        var parsed = new List<IReadOnlyList<InlineRun>>();
        for (var c = 0; c < count; c++)
        {
            parsed.Add(Inlines(c < cells.Count ? cells[c].Trim() : ""));
        }
        return new TableRow(parsed);
    }

    private static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal))
        {
            t = t.Substring(1);
        }
        if (t.EndsWith("|", StringComparison.Ordinal) && !t.EndsWith("\\|", StringComparison.Ordinal))
        {
            t = t.Substring(0, t.Length - 1);
        }
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var k = 0; k < t.Length; k++)
        {
            if (t[k] == '\\' && k + 1 < t.Length && t[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (t[k] == '|')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(t[k]);
            }
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private static bool StartsBlock(List<(string Line, int Offset)> lines, int i)
    {
        var line = lines[i].Line;
        return Fence.IsMatch(line) || Heading.IsMatch(line) || Rule.IsMatch(line) || Quote.IsMatch(line) || Item.IsMatch(line)
            || (i + 1 < lines.Count && line.IndexOf('|') >= 0 && TableSeparator.IsMatch(lines[i + 1].Line)
                && lines[i + 1].Line.IndexOf('-') >= 0);
    }

    private static int Indent(string whitespace) => whitespace.Sum(c => c == '\t' ? 4 : 1);

    private static bool IsBlank(string line)
    {
        foreach (var c in line)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }
        return true;
    }

    private static void AppendLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n'); // a model's single line break is meant as one
        }
        builder.Append(line);
    }

    private static List<(string Line, int Offset)> Lines(string text)
    {
        var lines = new List<(string, int)>();
        var start = 0;
        for (var k = 0; k <= text.Length; k++)
        {
            if (k == text.Length || text[k] == '\n')
            {
                var end = k > start && text[k - 1] == '\r' ? k - 1 : k;
                if (k < text.Length || start < text.Length)
                {
                    lines.Add((text.Substring(start, end - start), start));
                }
                start = k + 1;
            }
        }
        return lines;
    }

    // -- inlines ---------------------------------------------------------------------------------

    /// <summary>The runs of one block's text.</summary>
    public static IReadOnlyList<InlineRun> Inlines(string text)
    {
        var runs = new List<InlineRun>();
        ParseInlines(text, 0, text.Length, InlineStyle.None, runs);
        return Merge(runs);
    }

    private static void ParseInlines(string s, int start, int end, InlineStyle style, List<InlineRun> runs)
    {
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length > 0)
            {
                runs.Add(new InlineRun(plain.ToString(), style));
                plain.Clear();
            }
        }
        var i = start;
        while (i < end)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < end && (char.IsPunctuation(s[i + 1]) || char.IsSymbol(s[i + 1])))
            {
                plain.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = Run(s, i, end, '`');
                var close = FindRun(s, i + ticks, end, '`', ticks);
                if (close >= 0)
                {
                    FlushPlain();
                    var code = s.Substring(i + ticks, close - i - ticks);
                    if (code.Length > 1 && code[0] == ' ' && code[code.Length - 1] == ' ')
                    {
                        code = code.Substring(1, code.Length - 2);
                    }
                    runs.Add(new InlineRun(code, style | InlineStyle.Code));
                    i = close + ticks;
                    continue;
                }
                plain.Append(s, i, ticks);
                i += ticks;
                continue;
            }
            if ((c == '*' || c == '_' || c == '~') && CanOpen(s, i, end, c))
            {
                var count = Run(s, i, end, c);
                var width = c == '~' ? (count >= 2 ? 2 : 0) : count >= 2 ? 2 : 1;
                if (width > 0)
                {
                    var close = FindClose(s, i + width, end, c, width);
                    if (close > i + width)
                    {
                        FlushPlain();
                        var added = c == '~' ? InlineStyle.Strike : width == 2 ? InlineStyle.Bold : InlineStyle.Italic;
                        ParseInlines(s, i + width, close, style | added, runs);
                        i = close + width;
                        continue;
                    }
                }
                plain.Append(s, i, count);
                i += count;
                continue;
            }
            if (c == '[')
            {
                var closeText = FindBracket(s, i + 1, end);
                if (closeText > 0 && closeText + 1 < end && s[closeText + 1] == '(')
                {
                    var closeUrl = s.IndexOf(')', closeText + 2);
                    if (closeUrl > 0 && closeUrl < end)
                    {
                        FlushPlain();
                        var url = s.Substring(closeText + 2, closeUrl - closeText - 2).Trim();
                        var space = url.IndexOf(' ');
                        if (space > 0)
                        {
                            url = url.Substring(0, space); // drop a title
                        }
                        runs.Add(new InlineRun(s.Substring(i + 1, closeText - i - 1), style | InlineStyle.Link, url));
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }
            if ((c == 'h' || c == 'H') && (i == start || !char.IsLetterOrDigit(s[i - 1])) && StartsUrl(s, i, end))
            {
                var stop = i;
                while (stop < end && !char.IsWhiteSpace(s[stop]) && s[stop] != '<' && s[stop] != '>')
                {
                    stop++;
                }
                while (stop > i && ".,;:!?)'\"".IndexOf(s[stop - 1]) >= 0)
                {
                    stop--;
                }
                FlushPlain();
                var url = s.Substring(i, stop - i);
                runs.Add(new InlineRun(url, style | InlineStyle.Link, url));
                i = stop;
                continue;
            }
            plain.Append(c);
            i++;
        }
        FlushPlain();
    }

    private static bool StartsUrl(string s, int i, int end) =>
        (end - i > 8 && string.Compare(s, i, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0)
        || (end - i > 7 && string.Compare(s, i, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0);

    /// <summary>A delimiter opens only before text, and <c>_</c> only at a word's start (snake_case stays).</summary>
    private static bool CanOpen(string s, int i, int end, char c)
    {
        var after = i + Run(s, i, end, c);
        if (after >= end || char.IsWhiteSpace(s[after]))
        {
            return false;
        }
        return c != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
    }

    private static int FindClose(string s, int from, int end, char c, int width)
    {
        for (var k = from; k < end; k++)
        {
            if (s[k] == '`')
            {
                var ticks = Run(s, k, end, '`');
                var close = FindRun(s, k + ticks, end, '`', ticks);
                if (close >= 0)
                {
                    k = close + ticks - 1;
                    continue;
                }
            }
            if (s[k] != c)
            {
                continue;
            }
            var count = Run(s, k, end, c);
            if (count >= width && !char.IsWhiteSpace(s[k - 1])
                && (c != '_' || k + count >= end || !char.IsLetterOrDigit(s[k + count])))
            {
                // "***x***": the inner delimiters sit next to the text, so close with the run's end.
                return k + count - width;
            }
            k += count - 1;
        }
        return -1;
    }

    private static int FindBracket(string s, int from, int end)
    {
        var depth = 0;
        for (var k = from; k < end; k++)
        {
            if (s[k] == '[')
            {
                depth++;
            }
            else if (s[k] == ']')
            {
                if (depth == 0)
                {
                    return k;
                }
                depth--;
            }
        }
        return -1;
    }

    private static int Run(string s, int i, int end, char c)
    {
        var k = i;
        while (k < end && s[k] == c)
        {
            k++;
        }
        return k - i;
    }

    private static int FindRun(string s, int from, int end, char c, int length)
    {
        for (var k = from; k < end; k++)
        {
            if (s[k] == c)
            {
                var run = Run(s, k, end, c);
                if (run == length)
                {
                    return k;
                }
                k += run - 1;
            }
        }
        return -1;
    }

    private static List<InlineRun> Merge(List<InlineRun> runs)
    {
        var merged = new List<InlineRun>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }
            if (merged.Count > 0 && merged[merged.Count - 1] is var last && last.Style == run.Style && last.Url == run.Url
                && (run.Style & InlineStyle.Link) == 0)
            {
                merged[merged.Count - 1] = last with { Text = last.Text + run.Text };
                continue;
            }
            merged.Add(run);
        }
        return merged;
    }
}

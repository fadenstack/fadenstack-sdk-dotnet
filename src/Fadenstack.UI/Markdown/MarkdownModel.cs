namespace Fadenstack.UI.Markdown;

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
    Link = 16,
}

/// <summary>A run of text in one style; <see cref="Url"/> for a link.</summary>
public sealed record InlineRun(string Text, InlineStyle Style = InlineStyle.None, string? Url = null);

/// <summary>
/// One block of an answer. Blocks are immutable: while an answer streams only the last block is replaced, so
/// a skin re-renders one block per update however long the answer is.
/// </summary>
public abstract record MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

/// <summary>A fenced code block; <see cref="IsComplete"/> is false while its closing fence has not arrived.</summary>
public sealed record CodeBlock(string? Language, string Code, bool IsComplete) : MarkdownBlock;

/// <summary>A list item; <see cref="Level"/> is its nesting depth, <see cref="Marker"/> "•" or "3.".</summary>
public sealed record ListItem(int Level, string Marker, IReadOnlyList<InlineRun> Inlines);

public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItem> Items) : MarkdownBlock;

public sealed record QuoteBlock(IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

public enum ColumnAlignment
{
    Left,
    Center,
    Right,
}

public sealed record TableRow(IReadOnlyList<IReadOnlyList<InlineRun>> Cells);

public sealed record TableBlock(TableRow Header, IReadOnlyList<TableRow> Rows, IReadOnlyList<ColumnAlignment> Alignments) : MarkdownBlock
{
    public int ColumnCount => Header.Cells.Count;
}

public sealed record RuleBlock : MarkdownBlock;

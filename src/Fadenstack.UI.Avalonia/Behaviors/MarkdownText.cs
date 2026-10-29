using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Fadenstack.UI.Markdown;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Renders a block's <see cref="InlineRun"/>s into a <see cref="TextBlock"/>: <c>b:MarkdownText.Inlines="{Binding Inlines}"</c>.
/// A text block with runs, not a document, so a long chat stays light. Code and links take their look from the
/// <c>code</c> and <c>link</c> classes (Chat.axaml), so they follow the theme.
/// </summary>
public static class MarkdownText
{
    public static readonly AttachedProperty<IReadOnlyList<InlineRun>?> InlinesProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<InlineRun>?>("Inlines", typeof(MarkdownText));

    static MarkdownText() => InlinesProperty.Changed.AddClassHandler<TextBlock>((text, e) => Render(text, e.NewValue as IReadOnlyList<InlineRun>));

    public static IReadOnlyList<InlineRun>? GetInlines(TextBlock element) => element.GetValue(InlinesProperty);

    public static void SetInlines(TextBlock element, IReadOnlyList<InlineRun>? value) => element.SetValue(InlinesProperty, value);

    /// <summary>Opens a link; replace it to route links through the host (e.g. its own browser).</summary>
    public static Action<Uri> OpenLink { get; set; } = uri =>
    {
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    };

    /// <summary>The link under a point of the text block, if any.</summary>
    internal static Uri? LinkAt(TextBlock text, Point point)
    {
        if (GetInlines(text) is not { } runs)
        {
            return null;
        }
        var layout = text.TextLayout;
        var hit = layout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return null;
        }
        var start = 0;
        foreach (var run in runs)
        {
            start += run.Text.Length;
            if (hit.TextPosition < start)
            {
                return (run.Style & InlineStyle.Link) != 0 && Uri.TryCreate(run.Url, UriKind.Absolute, out var uri) ? uri : null;
            }
        }
        return null;
    }

    private static void Render(TextBlock text, IReadOnlyList<InlineRun>? runs)
    {
        text.PointerMoved -= OnPointerMoved;
        text.PointerReleased -= OnPointerReleased;
        var inlines = text.Inlines ??= new InlineCollection();
        inlines.Clear();
        if (runs is null)
        {
            return;
        }
        foreach (var source in runs)
        {
            inlines.Add(Build(source));
        }
        if (runs.Any(r => (r.Style & InlineStyle.Link) != 0))
        {
            text.PointerMoved += OnPointerMoved;
            text.PointerReleased += OnPointerReleased;
        }
    }

    private static Run Build(InlineRun source)
    {
        var run = new Run(source.Text);
        if ((source.Style & InlineStyle.Bold) != 0)
        {
            run.FontWeight = FontWeight.SemiBold;
        }
        if ((source.Style & InlineStyle.Italic) != 0)
        {
            run.FontStyle = FontStyle.Italic;
        }
        if ((source.Style & InlineStyle.Strike) != 0)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }
        if ((source.Style & InlineStyle.Code) != 0)
        {
            run.Classes.Add("code");
        }
        if ((source.Style & InlineStyle.Link) != 0)
        {
            run.Classes.Add("link");
        }
        return run;
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var text = (TextBlock)sender!;
        text.Cursor = LinkAt(text, e.GetPosition(text)) is null ? null : new Cursor(StandardCursorType.Hand);
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var text = (TextBlock)sender!;
        // A drag that selected text is not a click.
        if (e.InitialPressMouseButton != MouseButton.Left || text is SelectableTextBlock { SelectionStart: var a, SelectionEnd: var b } && a != b)
        {
            return;
        }
        if (LinkAt(text, e.GetPosition(text)) is { } uri)
        {
            OpenLink(uri);
            e.Handled = true;
        }
    }
}

/// <summary>Lays out a <see cref="TableBlock"/> as a grid: <c>b:MarkdownTable.Table="{Binding}"</c> on a ContentControl.</summary>
public static class MarkdownTable
{
    public static readonly AttachedProperty<TableBlock?> TableProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, TableBlock?>("Table", typeof(MarkdownTable));

    static MarkdownTable() => TableProperty.Changed.AddClassHandler<ContentControl>((host, e) => host.Content = e.NewValue is TableBlock table ? Build(table) : null);

    public static TableBlock? GetTable(ContentControl element) => element.GetValue(TableProperty);

    public static void SetTable(ContentControl element, TableBlock? value) => element.SetValue(TableProperty, value);

    private static ScrollViewer Build(TableBlock table)
    {
        var grid = new Grid();
        for (var c = 0; c < table.ColumnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        var rows = new[] { table.Header }.Concat(table.Rows).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < table.ColumnCount; c++)
            {
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                    TextAlignment = table.Alignments[c] switch
                    {
                        ColumnAlignment.Center => TextAlignment.Center,
                        ColumnAlignment.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    },
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                };
                MarkdownText.SetInlines(text, c < rows[r].Cells.Count ? rows[r].Cells[c] : Array.Empty<InlineRun>());
                var cell = new Border { Child = text };
                cell.Classes.Add(r == 0 ? "table-head" : "table-cell");
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }
}

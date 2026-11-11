using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Fadenstack.UI.Markdown;

namespace Fadenstack.UI.Wpf.Behaviors;

/// <summary>
/// Renders a block's <see cref="InlineRun"/>s into a <see cref="TextBlock"/>: <c>w:MarkdownText.Inlines="{Binding Inlines}"</c>.
/// A TextBlock, not a FlowDocument, so a long chat stays light.
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty InlinesProperty = DependencyProperty.RegisterAttached(
        "Inlines", typeof(IReadOnlyList<InlineRun>), typeof(MarkdownText), new PropertyMetadata(null, OnInlinesChanged));

    public static IReadOnlyList<InlineRun>? GetInlines(DependencyObject element) => (IReadOnlyList<InlineRun>?)element.GetValue(InlinesProperty);

    public static void SetInlines(DependencyObject element, IReadOnlyList<InlineRun>? value) => element.SetValue(InlinesProperty, value);

    /// <summary>Opens a link; replace it to route links through the host (e.g. its own browser).</summary>
    public static Action<Uri> OpenLink { get; set; } = uri =>
    {
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    };

    private static void OnInlinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }
        text.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<InlineRun> runs)
        {
            return;
        }
        foreach (var run in runs)
        {
            text.Inlines.Add(Build(run));
        }
    }

    private static Inline Build(InlineRun source)
    {
        var run = new Run(source.Text);
        if ((source.Style & InlineStyle.Bold) != 0)
        {
            run.FontWeight = FontWeights.SemiBold;
        }
        if ((source.Style & InlineStyle.Italic) != 0)
        {
            run.FontStyle = FontStyles.Italic;
        }
        if ((source.Style & InlineStyle.Strike) != 0)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }
        if ((source.Style & InlineStyle.Code) != 0)
        {
            run.SetResourceReference(TextElement.FontFamilyProperty, "Fadenstack.CodeFont");
            run.SetResourceReference(TextElement.BackgroundProperty, "Fadenstack.CodeInlineBackground");
        }
        if ((source.Style & InlineStyle.Link) != 0 && Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
        {
            var link = new Hyperlink(run) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
            link.SetResourceReference(TextElement.ForegroundProperty, "Fadenstack.Link");
            // The keyboard-focus ring in the theme's focus colour (the system's black dashes vanish on dark colours).
            link.SetResourceReference(FrameworkContentElement.FocusVisualStyleProperty, "Fadenstack.LinkFocusVisual");
            link.RequestNavigate += (_, e) =>
            {
                OpenLink(e.Uri);
                e.Handled = true;
            };
            return link;
        }
        return run;
    }
}

/// <summary>
/// The command that copies a code block, set once on the answer's block list and inherited by every block inside it, so a
/// code block in a quote or a list item finds it too: <c>w:MarkdownContext.CopyCommand="{Binding CopyCodeCommand}"</c>.
/// </summary>
public static class MarkdownContext
{
    public static readonly DependencyProperty CopyCommandProperty = DependencyProperty.RegisterAttached(
        "CopyCommand", typeof(System.Windows.Input.ICommand), typeof(MarkdownContext),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static System.Windows.Input.ICommand? GetCopyCommand(DependencyObject element) =>
        (System.Windows.Input.ICommand?)element.GetValue(CopyCommandProperty);

    public static void SetCopyCommand(DependencyObject element, System.Windows.Input.ICommand? value) => element.SetValue(CopyCommandProperty, value);
}

/// <summary>Lays out a <see cref="TableBlock"/> as a grid: <c>w:MarkdownTable.Table="{Binding}"</c> on a ContentControl.</summary>
public static class MarkdownTable
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.RegisterAttached(
        "Table", typeof(TableBlock), typeof(MarkdownTable), new PropertyMetadata(null, OnTableChanged));

    public static TableBlock? GetTable(DependencyObject element) => (TableBlock?)element.GetValue(TableProperty);

    public static void SetTable(DependencyObject element, TableBlock? value) => element.SetValue(TableProperty, value);

    private static void OnTableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl host)
        {
            return;
        }
        if (e.NewValue is not TableBlock table)
        {
            host.Content = null;
            return;
        }
        var grid = new Grid();
        for (var c = 0; c < table.ColumnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        var rows = new[] { table.Header }.Concat(table.Rows).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                MarkdownText.SetInlines(text, c < rows[r].Cells.Count ? rows[r].Cells[c] : Array.Empty<InlineRun>());
                var cell = new Border
                {
                    Child = text,
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "Fadenstack.Border");
                if (r == 0)
                {
                    cell.SetResourceReference(Border.BackgroundProperty, "Fadenstack.SurfaceAlt");
                }
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        host.Content = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}

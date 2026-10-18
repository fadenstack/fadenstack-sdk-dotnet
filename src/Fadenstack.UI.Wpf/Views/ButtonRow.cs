using System.Windows;
using System.Windows.Controls;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// A card's buttons (an approval's Allow, Allow for this chat, Allow always, Deny), in order, never one alone on a
/// row of its own (a WrapPanel left "Deny" there). In one row when they fit. Otherwise in as few rows as hold them,
/// each with as many buttons as the others (four go two by two): in equal columns when the widest fits a column, else
/// as wide as their texts. When no such rows fit, one under the other, as wide as the panel. Collapsed children take
/// no place.
/// </summary>
public class ButtonRow : Panel
{
    /// <summary>The space between two buttons in a row.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ButtonRow), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between two rows.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ButtonRow), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // The buttons in each row, and whether the rows are equal columns (else each button is as wide as its text).
    private List<int> _rows = new();
    private bool _columns;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private List<UIElement> Shown() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var shown = Shown();
        if (shown.Count == 0)
        {
            _rows = new List<int>();
            return default;
        }
        (_rows, _columns) = Layout(shown, availableSize.Width);
        var width = 0.0;
        var height = 0.0;
        var start = 0;
        foreach (var count in _rows)
        {
            var row = shown.GetRange(start, count);
            if (_columns)
            {
                foreach (var child in row)
                {
                    child.Measure(new Size(ColumnWidth(availableSize.Width, _rows[0]), double.PositiveInfinity));
                }
                width = availableSize.Width;
            }
            else
            {
                width = Math.Max(width, RowWidth(row));
            }
            height += row.Max(c => c.DesiredSize.Height);
            start += count;
        }
        return new Size(width, height + RowSpacing * (_rows.Count - 1));
    }

    /// <summary>The rows (buttons in each) and whether they are equal columns, for the buttons' own widths.</summary>
    private (List<int> Rows, bool Columns) Layout(List<UIElement> shown, double available)
    {
        var n = shown.Count;
        if (double.IsInfinity(available) || RowWidth(shown) <= available)
        {
            return (new List<int> { n }, false);
        }
        var widest = shown.Max(c => c.DesiredSize.Width);
        for (var rows = 2; rows <= n; rows++)
        {
            var counts = Enumerable.Range(0, rows).Select(r => n / rows + (r < n % rows ? 1 : 0)).ToList();
            if (rows < n && counts.Any(c => c == 1))
            {
                continue;
            }
            if (widest <= ColumnWidth(available, counts[0]))
            {
                return (counts, true);
            }
            if (rows < n && Fits(shown, counts, available))
            {
                return (counts, false);
            }
        }
        return (Enumerable.Repeat(1, n).ToList(), true);
    }

    private bool Fits(List<UIElement> shown, List<int> counts, double available)
    {
        var start = 0;
        foreach (var count in counts)
        {
            if (RowWidth(shown.GetRange(start, count)) > available)
            {
                return false;
            }
            start += count;
        }
        return true;
    }

    private double RowWidth(List<UIElement> row) => row.Sum(c => c.DesiredSize.Width) + Spacing * (row.Count - 1);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        var y = 0.0;
        var start = 0;
        foreach (var count in _rows)
        {
            if (start + count > shown.Count)
            {
                break;
            }
            var row = shown.GetRange(start, count);
            var tallest = row.Max(c => c.DesiredSize.Height);
            var x = 0.0;
            foreach (var child in row)
            {
                var width = _columns ? ColumnWidth(finalSize.Width, _rows[0]) : child.DesiredSize.Width;
                child.Arrange(new Rect(x, y, width, tallest));
                x += width + Spacing;
            }
            y += tallest + RowSpacing;
            start += count;
        }
        return finalSize;
    }

    private double ColumnWidth(double available, int columns) => Math.Max(0, (available - Spacing * (columns - 1)) / columns);
}

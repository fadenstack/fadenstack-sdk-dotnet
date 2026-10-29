using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The virtualizing panel of the chat's turns. It remembers the height of every turn it has measured and estimates the
/// others by their average, so the extent is the sum of what is known and the position of a turn does not depend on which
/// turns happen to be realized (the stock <see cref="VirtualizingStackPanel"/> places its realized block from an estimate
/// that moves whenever the average does, which, with a last turn taller than the window, re-realizes the turns around it on
/// every streamed word). It scrolls logically, so it owns the offset: when the heights above the first visible turn change,
/// or turns are inserted above it (an earlier page), the offset moves by the same amount and the reader's place stays.
/// </summary>
public sealed class TurnStackPanel : VirtualizingPanel, ILogicalScrollable
{
    /// <summary>How much of the window, above and below it, is kept realized (1 is one window each way).</summary>
    public static readonly StyledProperty<double> CacheLengthProperty =
        AvaloniaProperty.Register<TurnStackPanel, double>(nameof(CacheLength), 0.5, validate: v => v is >= 0 and <= 2);

    /// <summary>Space after the last turn, inside the extent.</summary>
    public static readonly StyledProperty<double> EndPaddingProperty =
        AvaloniaProperty.Register<TurnStackPanel, double>(nameof(EndPadding));

    /// <summary>Height assumed for turns before any is measured.</summary>
    private const double FirstEstimate = 160;

    /// <summary>Pixels one wheel notch scrolls.</summary>
    private const double LineHeight = 50;

    private readonly List<double> _sizes = new();
    private readonly List<Control> _realized = new();
    private readonly List<Control> _next = new();
    private double _knownSum;
    private int _knownCount;
    private int _first;
    private double _width = double.NaN;
    private double _offset;
    private Size _extent;
    private Size _viewport;
    private bool _inLayout;
    private EventHandler? _scrollInvalidated;

    public double CacheLength
    {
        get => GetValue(CacheLengthProperty);
        set => SetValue(CacheLengthProperty, value);
    }

    public double EndPadding
    {
        get => GetValue(EndPaddingProperty);
        set => SetValue(EndPaddingProperty, value);
    }

    /// <summary>The index of the first realized turn, or -1.</summary>
    public int FirstRealizedIndex => _realized.Count == 0 ? -1 : _first;

    /// <summary>The index of the last realized turn, or -1.</summary>
    public int LastRealizedIndex => _realized.Count == 0 ? -1 : _first + _realized.Count - 1;

    private double Estimate => _knownCount > 0 ? _knownSum / _knownCount : FirstEstimate;

    // -- scrolling ---------------------------------------------------------------------------------

    bool ILogicalScrollable.CanHorizontallyScroll { get; set; }

    bool ILogicalScrollable.CanVerticallyScroll { get; set; }

    bool IScrollable.CanHorizontallyScroll => false;

    bool IScrollable.CanVerticallyScroll => true;

    bool ILogicalScrollable.IsLogicalScrollEnabled => true;

    Size ILogicalScrollable.ScrollSize => new(LineHeight, LineHeight);

    Size ILogicalScrollable.PageScrollSize => _viewport;

    Size IScrollable.Extent => _extent;

    Size IScrollable.Viewport => _viewport;

    Vector IScrollable.Offset
    {
        get => new(0, _offset);
        set
        {
            if (Math.Abs(value.Y - _offset) > 0.01)
            {
                _offset = Math.Max(0, value.Y);
                InvalidateMeasure();
            }
        }
    }

    event EventHandler? ILogicalScrollable.ScrollInvalidated
    {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    void ILogicalScrollable.RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);

    Control? ILogicalScrollable.GetControlInDirection(NavigationDirection direction, Control? from) => null;

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;

    /// <summary>Scrolls the least that shows the part of a turn (as the stock scroll presenter does).</summary>
    bool ILogicalScrollable.BringIntoView(Control target, Rect targetRect)
    {
        if (_inLayout || target == this || !this.IsVisualAncestorOf(target) || target.TransformToVisual(this) is not { } map)
        {
            return false;
        }
        var rect = targetRect.TransformToAABB(map);
        var height = _viewport.Height;
        var above = rect.Top < 0 && rect.Bottom < height;
        var below = rect.Bottom > height && rect.Top > 0;
        var tooTall = rect.Height > height;
        var delta = (above && !tooTall) || (below && tooTall) ? rect.Top : above || below ? rect.Bottom - height : 0;
        if (Math.Abs(delta) < 0.5)
        {
            return false;
        }
        _offset = Math.Max(0, _offset + delta);
        InvalidateMeasure();
        return true;
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || index < 0 || index >= Items.Count)
        {
            return null;
        }
        var top = Top(index, Estimate);
        var size = SizeOf(index, Estimate);
        var height = _viewport.Height;
        _offset = size >= height || top < _offset ? top : top + size > _offset + height ? top + size - height : _offset;
        InvalidateMeasure();
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    // -- containers --------------------------------------------------------------------------------

    protected override Control? ContainerFromIndex(int index) =>
        index >= _first && index < _first + _realized.Count ? _realized[index - _first] : null;

    protected override int IndexFromContainer(Control container)
    {
        var at = _realized.IndexOf(container);
        return at < 0 ? -1 : _first + at;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized;

    // Containers are not pooled: a pool per recycle key (as the stock panel keeps) measured no faster at 1,000 turns
    // (streaming, wheel, scrolling through everything) and held about 7 MB more.
    private Control Realize(int index)
    {
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        var container = generator.NeedsContainer(item, index, out var key) ? generator.CreateContainer(item, index, key) : (Control)item!;
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private void Recycle(Control container)
    {
        ItemContainerGenerator!.ClearItemContainer(container);
        RemoveInternalChild(container);
    }

    private void RecycleAll()
    {
        foreach (var container in _realized)
        {
            Recycle(container);
        }
        _realized.Clear();
    }

    /// <summary>Recycles the realized turns before <paramref name="index"/>.</summary>
    private void RecycleBefore(int index)
    {
        var drop = Math.Min(_realized.Count, Math.Max(0, index - _first));
        for (var i = 0; i < drop; i++)
        {
            Recycle(_realized[i]);
        }
        _realized.RemoveRange(0, drop);
        _first += drop;
    }

    // -- the items ---------------------------------------------------------------------------------

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        var estimate = Estimate;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Inserted(e.NewStartingIndex, e.NewItems!.Count, estimate);
                break;
            case NotifyCollectionChangedAction.Remove:
                Removed(e.OldStartingIndex, e.OldItems!.Count, estimate);
                break;
            default:
                RecycleAll();
                ResetSizes(items.Count);
                break;
        }
        InvalidateMeasure();
    }

    /// <summary>Turns inserted above the first visible one push it down: the offset follows, so the reader keeps their place.</summary>
    private void Inserted(int at, int count, double estimate)
    {
        var before = _sizes.Count;
        var anchor = before == 0 ? 0 : AnchorIndex(estimate);
        _sizes.InsertRange(at, Enumerable.Repeat(double.NaN, count));
        if (at <= anchor && before > 0)
        {
            _offset += count * estimate;
        }
        ShiftRealized(at, count);
    }

    private void Removed(int at, int count, double estimate)
    {
        var anchor = AnchorIndex(estimate);
        var removed = 0.0;
        for (var i = at; i < at + count && i < _sizes.Count; i++)
        {
            removed += SizeOf(i, estimate);
        }
        if (at + count <= anchor)
        {
            _offset = Math.Max(0, _offset - removed);
        }
        _sizes.RemoveRange(at, Math.Min(count, _sizes.Count - at));
        RecountKnown();
        ShiftRealized(at, -count);
    }

    /// <summary>Realized turns after an insert (positive) or removal (negative) at <paramref name="at"/>.</summary>
    private void ShiftRealized(int at, int count)
    {
        if (_realized.Count == 0)
        {
            return;
        }
        var last = _first + _realized.Count - 1;
        if (at > last)
        {
            return;
        }
        if (at + Math.Max(0, -count) <= _first && (count < 0 || at <= _first))
        {
            // Only turns above the realized ones changed: they keep their containers, and their indexes move.
            var generator = ItemContainerGenerator!;
            for (var i = 0; i < _realized.Count; i++)
            {
                generator.ItemContainerIndexChanged(_realized[i], _first + i, _first + i + count);
            }
            _first += count;
            return;
        }
        RecycleAll();
    }

    private void ResetSizes(int count)
    {
        _sizes.Clear();
        _sizes.AddRange(Enumerable.Repeat(double.NaN, count));
        _knownSum = 0;
        _knownCount = 0;
    }

    private void RecountKnown()
    {
        _knownSum = 0;
        _knownCount = 0;
        foreach (var size in _sizes.Where(s => !double.IsNaN(s)))
        {
            _knownSum += size;
            _knownCount++;
        }
    }

    private void Remember(int index, double size)
    {
        var old = _sizes[index];
        if (double.IsNaN(old))
        {
            _knownCount++;
            _knownSum += size;
        }
        else
        {
            _knownSum += size - old;
        }
        _sizes[index] = size;
    }

    private double SizeOf(int index, double estimate) => double.IsNaN(_sizes[index]) ? estimate : _sizes[index];

    private double Top(int index, double estimate)
    {
        var top = 0.0;
        for (var i = 0; i < index; i++)
        {
            top += SizeOf(i, estimate);
        }
        return top;
    }

    /// <summary>The first turn whose bottom is below the top of the window.</summary>
    private int AnchorIndex(double estimate)
    {
        var top = 0.0;
        for (var i = 0; i < _sizes.Count; i++)
        {
            top += SizeOf(i, estimate);
            if (top > _offset)
            {
                return i;
            }
        }
        return Math.Max(0, _sizes.Count - 1);
    }

    // -- layout ------------------------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Items.Count;
        if (_sizes.Count != count)
        {
            RecycleAll();
            ResetSizes(count);
        }
        if (count == 0 || ItemContainerGenerator is null)
        {
            RecycleAll();
            Publish(default, default, 0);
            return default;
        }
        if (!double.IsNaN(_width) && Math.Abs(_width - availableSize.Width) > 0.5)
        {
            // Every remembered height was for another width.
            ResetSizes(count);
        }
        _width = availableSize.Width;

        _inLayout = true;
        try
        {
            return MeasureTurns(availableSize, count);
        }
        finally
        {
            _inLayout = false;
        }
    }

    private Size MeasureTurns(Size availableSize, int count)
    {
        // Asked with no height (before the scroll viewer has one) the panel shows its first turn only: realizing a turn
        // costs a template, and the real window follows in the next pass.
        var windowHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var estimate = Estimate;
        var offset = Math.Clamp(_offset, 0, Math.Max(0, Total(count, estimate) - windowHeight));
        var cache = CacheLength * windowHeight;
        var start = offset - cache;
        var end = offset + windowHeight + cache;

        // The first turn to realize, and how many unmeasured turns lie above it.
        var first = 0;
        var top = 0.0;
        var unknownAbove = 0;
        while (first < count - 1 && top + SizeOf(first, estimate) <= start)
        {
            top += SizeOf(first, estimate);
            unknownAbove += double.IsNaN(_sizes[first]) ? 1 : 0;
            first++;
        }
        RecycleBefore(first);

        // Realize and measure downwards. "Before" is what the heights were, "after" what they are now; the turn at the
        // top of the window (the anchor) must stay where it is on screen.
        var next = _next;
        next.Clear();
        var before = top;
        var after = top;
        var anchor = -1;
        var anchorShift = 0.0;
        var childSize = new Size(availableSize.Width, double.PositiveInfinity);
        var i = first;
        for (; i < count && (after < end || next.Count == 0); i++)
        {
            var old = SizeOf(i, estimate);
            if (anchor < 0 && before + old > offset)
            {
                anchor = i;
            }
            var container = i >= _first && i - _first < _realized.Count ? _realized[i - _first] : Realize(i);
            container.Measure(childSize);
            Remember(i, container.DesiredSize.Height);
            next.Add(container);
            if (anchor < 0)
            {
                anchorShift += container.DesiredSize.Height - old;
            }
            before += old;
            after += container.DesiredSize.Height;
        }
        for (var k = 0; k < _realized.Count; k++)
        {
            var index = _first + k;
            if (index >= i || index < first)
            {
                Recycle(_realized[k]);
            }
        }
        _realized.Clear();
        _realized.AddRange(next);
        _first = first;

        var newEstimate = Estimate;
        var newOffset = offset + anchorShift + unknownAbove * (newEstimate - estimate);
        var total = Total(count, newEstimate);
        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, total - windowHeight));

        // The window moved with the anchor; if what is realized no longer covers it, measure again.
        var realizedTop = Top(first, newEstimate);
        var realizedBottom = realizedTop + _realized.Sum(c => c.DesiredSize.Height);
        if ((first > 0 && realizedTop > newOffset - cache + 0.5) || (i < count && realizedBottom < newOffset + windowHeight + cache - 0.5))
        {
            InvalidateMeasure();
        }

        var width = double.IsInfinity(availableSize.Width) ? _realized.Max(c => c.DesiredSize.Width) : availableSize.Width;
        Publish(new Size(width, total), new Size(width, windowHeight), newOffset);
        return new Size(width, windowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_realized.Count > 0)
        {
            var top = Top(_first, Estimate) - _offset;
            foreach (var container in _realized)
            {
                var height = container.DesiredSize.Height;
                container.Arrange(new Rect(0, top, finalSize.Width, height));
                top += height;
            }
        }
        return finalSize;
    }

    private double Total(int count, double estimate) => _knownSum + (count - _knownCount) * estimate + EndPadding;

    /// <summary>Tells the scroll viewer when the extent, the window or the offset changed.</summary>
    private void Publish(Size extent, Size viewport, double offset)
    {
        var changed = extent != _extent || viewport != _viewport || Math.Abs(offset - _offset) > 0.01;
        _extent = extent;
        _viewport = viewport;
        _offset = offset;
        if (changed)
        {
            _scrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }
}

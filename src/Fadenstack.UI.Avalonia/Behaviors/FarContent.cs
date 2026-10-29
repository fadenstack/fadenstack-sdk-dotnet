using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Lets turns far from the viewport go of their heavy content: <c>b:FarContent.Distance="100"</c> on the scroll viewer of
/// the turns' items control. A turn more than that many positions from the realized range drops its parsed Markdown
/// blocks (<see cref="TurnViewModel.Unload"/>; its text stays) a moment after scrolling settles; it loads them again,
/// before it is drawn, when its container is prepared. Decoded pictures need no such step: <see cref="PictureSource"/>
/// holds one only while its image shows it. Together the memory of a scrolled-through session stays flat.
/// </summary>
public static class FarContent
{
    /// <summary>How many positions beyond the realized range a turn keeps its content; 0 turns the behaviour off.</summary>
    public static readonly AttachedProperty<int> DistanceProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, int>("Distance", typeof(FarContent));

    private static readonly AttachedProperty<Watch?> WatchProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Watch?>("Watch", typeof(FarContent));

    static FarContent() => DistanceProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
    {
        viewer.GetValue(WatchProperty)?.Dispose();
        viewer.SetValue(WatchProperty, e.NewValue is > 0 ? new Watch(viewer) : null);
    });

    public static int GetDistance(ScrollViewer element) => element.GetValue(DistanceProperty);

    public static void SetDistance(ScrollViewer element, int value) => element.SetValue(DistanceProperty, value);

    /// <summary>Unloads the turns outside the window now (the behaviour does this a moment after scrolling stops).</summary>
    public static void Sweep(ScrollViewer viewer) => viewer.GetValue(WatchProperty)?.Sweep();

    private sealed class Watch : IDisposable
    {
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

        private readonly ScrollViewer _viewer;
        private ItemsControl? _items;
        private bool _sweepScheduled;

        public Watch(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.AttachedToVisualTree += OnAttached;
            viewer.DetachedFromVisualTree += OnDetached;
            if (viewer.IsAttachedToVisualTree())
            {
                Hook();
            }
        }

        public void Dispose()
        {
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.AttachedToVisualTree -= OnAttached;
            _viewer.DetachedFromVisualTree -= OnDetached;
            Unhook();
        }

        /// <summary>Unloads every turn that is not within the distance of the realized range.</summary>
        public void Sweep()
        {
            _sweepScheduled = false;
            if (_items is not { ItemsPanelRoot: VirtualizingStackPanel { FirstRealizedIndex: var first and >= 0, LastRealizedIndex: var last } } items)
            {
                return;
            }
            var distance = GetDistance(_viewer);
            var index = 0;
            foreach (var item in items.Items)
            {
                if (item is TurnViewModel turn && (index < first - distance || index > last + distance))
                {
                    turn.Unload();
                }
                index++;
            }
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Hook();

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Unhook();

        private void Hook()
        {
            Unhook();
            _items = _viewer.TemplatedParent as ItemsControl;
            if (_items is not null)
            {
                _items.ContainerPrepared += OnPrepared;
            }
        }

        private void Unhook()
        {
            if (_items is not null)
            {
                _items.ContainerPrepared -= OnPrepared;
                _items = null;
            }
        }

        /// <summary>A turn about to be drawn gets its content back first.</summary>
        private void OnPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (_items?.Items[e.Index] is TurnViewModel turn)
            {
                turn.Load();
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!_sweepScheduled)
            {
                _sweepScheduled = true;
                DispatcherTimer.RunOnce(Sweep, Settle, DispatcherPriority.Background);
            }
        }
    }
}

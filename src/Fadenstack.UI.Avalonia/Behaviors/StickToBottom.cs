using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Keeps a scroll viewer at the bottom while new content arrives, as long as the reader is there; scrolling up to read
/// stops it, scrolling back down resumes it (as Copilot's chat does). <c>b:StickToBottom.IsEnabled="True"</c> on the
/// viewer; <see cref="IsFollowingProperty"/> tells a "jump to newest" button when to show.
/// Avalonia's own scroll anchoring anchors to the top item only, so the end needs this.
/// <para>
/// The reader's input decides, not the offset: a layout pass moves the offset too (a recycled turn is measured, the extent
/// is re-estimated), and reading that as "the reader scrolled" would let go of the end in the middle of a stream. So the
/// wheel, the keys, a press on the scroll bar and touch mark the next scroll changes as the reader's (they set following
/// to "at the bottom"); every other change while following scrolls to the end again. A host that scrolls by code calls
/// <see cref="Release"/> first.
/// </para>
/// </summary>
public static class StickToBottom
{
    /// <summary>How far from the end still counts as "at the bottom", in pixels.</summary>
    private const double Slack = 8;

    /// <summary>How long after the reader's input a scroll change is still theirs, in milliseconds.</summary>
    private const long ReaderWindow = 300;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(StickToBottom));

    /// <summary>True while the viewer follows the end: at the start, after a send, and whenever the reader is at the bottom.</summary>
    public static readonly AttachedProperty<bool> IsFollowingProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsFollowing", typeof(StickToBottom), true);

    private static readonly AttachedProperty<Reader?> ReaderProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Reader?>("Reader", typeof(StickToBottom));

    static StickToBottom() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
    {
        viewer.GetValue(ReaderProperty)?.Dispose();
        viewer.SetValue(ReaderProperty, e.NewValue is true ? new Reader(viewer) : null);
    });

    /// <summary>The "jump to newest" button's command; its parameter is the scroll viewer.</summary>
    public static ICommand ScrollToNewestCommand { get; } = new RelayCommand<ScrollViewer>(viewer =>
    {
        if (viewer is not null)
        {
            ScrollToNewest(viewer);
        }
    });

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsFollowing(ScrollViewer element) => element.GetValue(IsFollowingProperty);

    /// <summary>Scrolls to the end and follows it from now on (the "jump to newest" button).</summary>
    public static void ScrollToNewest(ScrollViewer viewer)
    {
        viewer.SetValue(IsFollowingProperty, true);
        viewer.ScrollToEnd();
    }

    /// <summary>Stops following the end; the host calls this before it scrolls by code to somewhere else.</summary>
    public static void Release(ScrollViewer viewer) => viewer.SetValue(IsFollowingProperty, false);

    /// <summary>The reader's input on one viewer, and the scroll changes it explains.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private long _inputAt = -ReaderWindow;
        private bool _dragging;

        public Reader(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.ScrollChanged += OnScrollChanged;
            // Tunnel, before the viewer acts on them, and also when something below handled them.
            viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        public void Dispose()
        {
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnInput);
            _viewer.RemoveHandler(InputElement.KeyDownEvent, OnKey);
            _viewer.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            _viewer.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            _viewer.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        }

        private bool AtBottom => _viewer.Offset.Y + _viewer.Viewport.Height >= _viewer.Extent.Height - Slack;

        private bool Active => _dragging || Environment.TickCount64 - _inputAt < ReaderWindow;

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (Active)
            {
                _viewer.SetValue(IsFollowingProperty, AtBottom);
            }
            else if (GetIsFollowing(_viewer) && !AtBottom)
            {
                _viewer.ScrollToEnd();
            }
        }

        private void OnInput(object? sender, RoutedEventArgs e) => _inputAt = Environment.TickCount64;

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                _inputAt = Environment.TickCount64;
            }
        }

        /// <summary>A press on the scroll bar (its thumb or track) or a touch drag scrolls; a press on the content does not.</summary>
        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch || (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null))
            {
                _dragging = true;
            }
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e) => EndDrag();

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

        private void EndDrag()
        {
            if (_dragging)
            {
                _dragging = false;
                _inputAt = Environment.TickCount64;
            }
        }
    }
}

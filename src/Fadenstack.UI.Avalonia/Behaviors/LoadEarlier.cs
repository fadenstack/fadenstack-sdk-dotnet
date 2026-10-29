using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Loads older turns before the reader reaches them: <c>b:LoadEarlier.Command="{Binding LoadEarlierCommand}"</c> on the
/// scroll viewer of an items control. The command runs when the viewport comes within one screen of the top, so the
/// offset is still above 0 (where Avalonia's scroll anchoring is off) and the reader never waits at the top.
/// Turns inserted above the visible one push it down, and Avalonia does not move the offset to follow it (anchoring keeps
/// no turn in place across an insert). So the turn at the top of the viewport is remembered by where it is in the content,
/// and when the page has arrived the offset is moved by how far that turn moved: the reader sees the same part of the
/// conversation, even if they kept scrolling while the page was built.
/// </summary>
public static class LoadEarlier
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, ICommand?>("Command", typeof(LoadEarlier));

    private static readonly AttachedProperty<Watch?> WatchProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Watch?>("Watch", typeof(LoadEarlier));

    static LoadEarlier() => CommandProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
    {
        viewer.GetValue(WatchProperty)?.Dispose();
        viewer.SetValue(WatchProperty, e.NewValue is null ? null : new Watch(viewer));
    });

    public static ICommand? GetCommand(ScrollViewer element) => element.GetValue(CommandProperty);

    public static void SetCommand(ScrollViewer element, ICommand? value) => element.SetValue(CommandProperty, value);

    /// <summary>The turn at the top of the viewport and where its top edge is in the content (not on the screen).</summary>
    private sealed record Anchor(object Item, double ContentTop);

    private sealed class Watch : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private bool _loading;

        public Watch(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.ScrollChanged += OnScrollChanged;
        }

        public void Dispose() => _viewer.ScrollChanged -= OnScrollChanged;

        private ItemsControl? Items => _viewer.TemplatedParent as ItemsControl;

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => TryLoad();

        private void TryLoad()
        {
            var command = GetCommand(_viewer);
            if (_loading || command is null || _viewer.Viewport.Height <= 0 || _viewer.Offset.Y >= _viewer.Viewport.Height || !command.CanExecute(null))
            {
                return;
            }
            _ = RunAsync(command);
        }

        private async Task RunAsync(ICommand command)
        {
            _loading = true;
            var anchor = Capture();
            try
            {
                if (command is IAsyncRelayCommand async)
                {
                    await async.ExecuteAsync(null);
                }
                else
                {
                    command.Execute(null);
                }
                Restore(anchor);
            }
            catch (Exception)
            {
                // The command reports its own failures; the reader keeps what is shown.
            }
            finally
            {
                _loading = false;
            }
            // The reader may be at the top again (a flick passed the whole screen before the page arrived), and no scroll event follows.
            TryLoad();
        }

        private Anchor? Capture()
        {
            if (Items is not { ItemsPanelRoot: { } panel } items)
            {
                return null;
            }
            Anchor? best = null;
            var bestTop = double.MaxValue;
            foreach (var child in panel.Children)
            {
                if (!child.IsVisible || items.ItemFromContainer(child) is not { } item || child.TranslatePoint(default, _viewer) is not { } at
                    || at.Y + child.Bounds.Height <= 0 || at.Y >= bestTop)
                {
                    continue;
                }
                bestTop = at.Y;
                best = new Anchor(item, _viewer.Offset.Y + at.Y);
            }
            return best;
        }

        private void Restore(Anchor? anchor)
        {
            // A reader at the bottom follows the end instead; nothing to keep in place.
            if (anchor is null || Items is not { } items || StickToBottom.GetIsFollowing(_viewer))
            {
                return;
            }
            var index = items.Items.IndexOf(anchor.Item);
            if (index < 0)
            {
                return;
            }
            // The turn is now a page further down and not drawn: let the panel take in the new turns, bring the turn into view,
            // see where it landed in the content, and put the offset back where the reader was, plus the distance it moved.
            var offset = _viewer.Offset.Y;
            _viewer.UpdateLayout();
            items.ScrollIntoView(index);
            _viewer.UpdateLayout();
            if (items.ContainerFromIndex(index)?.TranslatePoint(default, _viewer) is { } now)
            {
                var moved = _viewer.Offset.Y + now.Y - anchor.ContentTop;
                _viewer.Offset = new Vector(_viewer.Offset.X, offset + moved);
            }
        }
    }
}

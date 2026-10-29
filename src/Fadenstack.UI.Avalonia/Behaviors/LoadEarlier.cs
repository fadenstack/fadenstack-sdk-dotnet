using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Loads older turns before the reader reaches them: <c>b:LoadEarlier.Command="{Binding LoadEarlierCommand}"</c> on the
/// scroll viewer of an items control. The command runs when the viewport comes within one screen of the top, so the
/// offset is still above 0 and the reader never waits at the top. Keeping the reader's place while the page is inserted
/// above is the panel's job (<see cref="Views.TurnStackPanel"/> moves the offset by the height it inserts).
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
            }
            catch (Exception)
            {
                // The command reports its own failures; the reader keeps what is shown.
            }
            finally
            {
                _loading = false;
            }
            // The reader may be at the top again (a flick passed the whole screen before the page arrived), and no scroll event
            // follows. Ask once the page is laid out: until then the viewer still has the offset from before the page.
            Dispatcher.UIThread.Post(TryLoad, DispatcherPriority.Background);
        }

    }
}

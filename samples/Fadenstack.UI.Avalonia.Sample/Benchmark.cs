using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Views;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
/// The long-session harness (17 section 4.1): opens a seeded session in a real window and runs the scenarios on it,
/// recording per frame (a) the work: from the start of the frame until the UI thread is idle again at Background
/// priority (input, layout, render and the render batcher's flush are all above or at it), and (b) the interval
/// between two frames.
/// </summary>
internal sealed class Benchmark
{
    private readonly TopLevel _window;
    private readonly ChatView _view;
    private readonly HarnessChat _harness;
    private readonly StringBuilder _report = new();

    public Benchmark(TopLevel window, ChatView view, HarnessChat harness)
    {
        _window = window;
        _view = view;
        _harness = harness;
    }

    public string Report => _report.ToString();

    private ScrollViewer Scroller => _view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");

    /// <summary>Runs every scenario and returns the numbers as a Markdown table.</summary>
    public async Task<IReadOnlyList<Result>> RunAsync(int turns, string? only = null)
    {
        var results = new List<Result>();
        var chat = _harness.Chat;

        Log("open");
        var openPrepared = 0;
        var openList = _view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Turns");
        void CountOpen(object? sender, ContainerPreparedEventArgs e) => openPrepared++;
        openList.ContainerPrepared += CountOpen;
        var opened = Stopwatch.StartNew();
        await chat.OpenSessionAsync(HarnessChat.SessionId);
        var read = opened.Elapsed.TotalMilliseconds;
        await Frame();
        var first = opened.Elapsed.TotalMilliseconds;
        await Frame();
        opened.Stop();
        openList.ContainerPrepared -= CountOpen;
        Log($"open: view model {read:F0} ms, first frame {first:F0} ms, second frame {opened.Elapsed.TotalMilliseconds:F0} ms");
        results.Add(new Result(turns, "open", opened.Elapsed.TotalMilliseconds, Stats.Empty,
            $"{chat.Turns.Count} turns shown, {openPrepared} containers prepared", HeapMb()));

        await Settle();
        var viewer = Scroller;
        if (only == "stream")
        {
            await StreamAsync(turns, results);
            return results;
        }

        Log("opened");
        // Jump to the end from a few screens up.
        StickToBottom.Release(viewer);
        viewer.Offset = new Vector(0, Math.Max(0, viewer.Offset.Y - 5 * viewer.Viewport.Height));
        await Frame();
        var jump = Stopwatch.StartNew();
        StickToBottom.ScrollToNewest(viewer);
        await Frame();
        await Frame();
        var landed = viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;
        results.Add(new Result(turns, "jump to end", jump.Elapsed.TotalMilliseconds, Stats.Empty, landed ? "landed at the end" : "MISSED the end", HeapMb()));

        // Scroll up through everything and back down, twice: the first pass pays for compiling the code (cold), the
        // second is the steady state a reader sees (warm; left out for very long sessions, where one pass takes minutes).
        // 1,500 px a frame is a fast flick of the wheel.
        foreach (var pass in turns <= 2000 ? new[] { "cold", "warm" } : new[] { "cold" })
        {
            Log($"scroll up ({pass})");
            StickToBottom.Release(viewer); // the harness scrolls by code
            var up = Record();
            var scrolling = Stopwatch.StartNew();
            for (var frame = 0; frame < 60000 && (viewer.Offset.Y > 0 || chat.HasEarlierTurns); frame++)
            {
                await Frame();
                viewer.Offset = new Vector(0, Math.Max(0, viewer.Offset.Y - 1500));
            }
            await Frame();
            results.Add(new Result(turns, $"scroll up through everything ({pass})", scrolling.Elapsed.TotalMilliseconds, up.Stop(),
                $"{chat.Turns.Count} of {turns} turns loaded, {(chat.HasEarlierTurns ? "NOT at the first turn" : "at the first turn")}", HeapMb()));

            Log($"scroll down ({pass})");
            var down = Record();
            scrolling.Restart();
            for (var frame = 0; frame < 60000; frame++)
            {
                await Frame();
                viewer.Offset = new Vector(0, viewer.Offset.Y + 1500);
                if (viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 1)
                {
                    break;
                }
            }
            await Frame();
            await Task.Delay(600); // the far-content sweep runs a moment after scrolling stops
            results.Add(new Result(turns, $"scroll back down ({pass})", scrolling.Elapsed.TotalMilliseconds, down.Stop(), "", HeapMb()));
        }

        // The wheel: 120 px a frame, 600 frames up and 600 down. A steady load, the one to hold against the 16.7 ms of a frame.
        foreach (var direction in new[] { -1, 1 })
        {
            StickToBottom.Release(viewer);
            if (direction < 0)
            {
                viewer.ScrollToEnd();
                await Settle();
            }
            var wheel = Record();
            var wheeling = Stopwatch.StartNew();
            for (var frame = 0; frame < 600; frame++)
            {
                await Frame();
                viewer.Offset = new Vector(0, Math.Max(0, viewer.Offset.Y + direction * 120));
            }
            results.Add(new Result(turns, direction < 0 ? "wheel up, 120 px a frame" : "wheel down, 120 px a frame", wheeling.Elapsed.TotalMilliseconds, wheel.Stop(), "", HeapMb()));
        }
        await StreamAsync(turns, results);
        return results;
    }

    /// <summary>Streams a long answer into a new last turn while the reader follows the end.</summary>
    private async Task StreamAsync(int turns, List<Result> results)
    {
        var chat = _harness.Chat;
        var viewer = Scroller;
        Log("stream");
        // Stream a long answer into the last turn, following the end.
        StickToBottom.ScrollToNewest(viewer);
        await Settle();
        var stream = Record();
        var streaming = Stopwatch.StartNew();
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var paused = GC.GetTotalPauseDuration();
        using var allocations = AllocationProfile.StartIfRequested();
        var prepared = 0;
        var turnsControl = _view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Turns");
        // A stream grows only the last turn: any other container prepared meanwhile is the panel re-realizing turns for nothing.
        void CountPrepared(object? sender, ContainerPreparedEventArgs e) => prepared++;
        turnsControl.ContainerPrepared += CountPrepared;
        var scrollChanges = 0;
        void CountScrolls(object? sender, ScrollChangedEventArgs e) => scrollChanges++;
        viewer.ScrollChanged += CountScrolls;
        var sending = chat.SendAsync(HarnessGateway.StreamLong + " answer");
        while (!sending.IsCompleted)
        {
            await Task.Delay(50);
        }
        await sending;
        await Frame();
        await Frame();
        var streamStats = stream.Stop();
        var atEnd = viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;
        turnsControl.ContainerPrepared -= CountPrepared;
        viewer.ScrollChanged -= CountScrolls;
        var gc = $"{prepared} containers prepared, {scrollChanges} scroll changes, GC{GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]} " +
            $"({(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:F0} ms paused)";
        if (allocations is not null)
        {
            Console.Error.WriteLine("allocations while streaming:\n" + allocations.Top(25));
        }
        results.Add(new Result(turns, "stream a long answer", streaming.Elapsed.TotalMilliseconds, streamStats,
            $"{(atEnd ? "stayed at the end" : "LOST the end")}, {gc}", HeapMb()));
    }

    public static string Table(IReadOnlyList<Result> results)
    {
        var table = new StringBuilder();
        table.AppendLine("| turns | scenario | total ms | frames | work p50 ms | work p95 ms | work p99 ms | max ms | interval p50 | interval p95 | heap MB | note |");
        table.AppendLine("|---:|:--|---:|---:|---:|---:|---:|---:|---:|---:|---:|:--|");
        foreach (var r in results)
        {
            var s = r.Frames;
            table.AppendLine($"| {r.Turns} | {r.Scenario} | {r.TotalMs:F0} | {s.Count} | {s.WorkP50:F1} | {s.WorkP95:F1} | {s.WorkP99:F1} | {s.WorkMax:F1} | {s.IntervalP50:F1} | {s.IntervalP95:F1} | {r.HeapMb:F0} | {r.Note} |");
        }
        return table.ToString();
    }

    private static void Log(string stage)
    {
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {stage}");
        Console.Error.Flush();
    }

    private async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            await Frame();
        }
    }

    private Task Frame()
    {
        var done = new TaskCompletionSource();
        _window.RequestAnimationFrame(_ => done.TrySetResult());
        return done.Task;
    }

    private Recorder Record() => new(_window);

    private static double HeapMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true) / (1024.0 * 1024.0);
    }

    internal sealed record Result(int Turns, string Scenario, double TotalMs, Stats Frames, string Note, double HeapMb);

    internal sealed record Stats(int Count, double WorkP50, double WorkP95, double WorkP99, double WorkMax, double IntervalP50, double IntervalP95)
    {
        public static Stats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Records every frame between its creation and <see cref="Stop"/>.</summary>
    private sealed class Recorder
    {
        private readonly TopLevel _window;
        private readonly List<double> _work = new();
        private readonly List<double> _interval = new();
        private long _last = -1;
        private bool _running = true;

        public Recorder(TopLevel window)
        {
            _window = window;
            _window.RequestAnimationFrame(OnFrame);
        }

        public Stats Stop()
        {
            _running = false;
            return new Stats(_work.Count, Percentile(_work, 0.5), Percentile(_work, 0.95), Percentile(_work, 0.99), _work.Count == 0 ? 0 : _work.Max(),
                Percentile(_interval, 0.5), Percentile(_interval, 0.95));
        }

        private void OnFrame(TimeSpan time)
        {
            if (!_running)
            {
                return;
            }
            var start = Stopwatch.GetTimestamp();
            if (_last >= 0)
            {
                _interval.Add(Milliseconds(start - _last));
            }
            _last = start;
            // Everything above Background (input, layout, render) runs before this does, and so does the flush posted earlier.
            Dispatcher.UIThread.Post(() =>
            {
                if (_running)
                {
                    _work.Add(Milliseconds(Stopwatch.GetTimestamp() - start));
                }
            }, DispatcherPriority.Background);
            _window.RequestAnimationFrame(OnFrame);
        }

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1))];
        }
    }
}

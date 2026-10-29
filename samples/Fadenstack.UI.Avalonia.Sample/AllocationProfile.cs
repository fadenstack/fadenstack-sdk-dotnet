using System.Diagnostics.Tracing;
using System.Text;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
/// Which types the process allocates while it is alive, from the runtime's own allocation-tick events (one per ~100 KB).
/// The harness prints the top of it after the streaming scenario when <c>FADEN_BENCH_ALLOC=1</c>: a stream's cost is mostly
/// allocation (and the garbage collections it causes), so this says where to look.
/// </summary>
internal sealed class AllocationProfile : EventListener
{
    private const int GcKeyword = 0x1;

    private readonly Dictionary<string, long> _ticks = new();
    private readonly object _gate = new();

    public static AllocationProfile? StartIfRequested() => Environment.GetEnvironmentVariable("FADEN_BENCH_ALLOC") == "1" ? new AllocationProfile() : null;

    public string Top(int count)
    {
        lock (_gate)
        {
            var report = new StringBuilder();
            foreach (var (type, ticks) in _ticks.OrderByDescending(t => t.Value).Take(count))
            {
                report.AppendLine($"  {ticks * 0.1:F1} MB  {type}");
            }
            return report.ToString();
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.Payload is null)
        {
            return;
        }
        var index = e.PayloadNames?.IndexOf("TypeName") ?? -1;
        if (index >= 0 && e.Payload[index] is string type)
        {
            lock (_gate)
            {
                _ticks[type] = _ticks.GetValueOrDefault(type) + 1;
            }
        }
    }
}

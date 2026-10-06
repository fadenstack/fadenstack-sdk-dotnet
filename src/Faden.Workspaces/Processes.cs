using System.Diagnostics;

namespace Faden.Workspaces;

/// <summary>A process as the framework needs it: identity, start time, session, window title.</summary>
public sealed record ProcessSnapshot(int Id, string Name, DateTime StartedUtc, int SessionId, string? WindowTitle)
{
    public long StartTicks => StartedUtc.Ticks;
}

/// <summary>The processes of this device. Replaceable in tests.</summary>
public interface IProcessSource
{
    /// <summary>The current user's session; processes of other sessions are never reported.</summary>
    int CurrentSessionId { get; }

    /// <summary>The current process.</summary>
    ProcessSnapshot Current { get; }

    /// <summary>Processes of the current session with this name (without extension, case-insensitive on Windows).</summary>
    IReadOnlyList<ProcessSnapshot> ByName(string name);

    ProcessSnapshot? ById(int id);
}

public static class ProcessSourceExtensions
{
    /// <summary>The process still runs and is the same one (not a new process with a reused id).</summary>
    public static bool IsAlive(this IProcessSource processes, int processId, long startTicks) =>
        processes.ById(processId) is { } p && p.StartTicks == startTicks;
}

/// <summary><see cref="Process"/>-based. Processes it may not inspect (another user's, elevated) are skipped.</summary>
public sealed class SystemProcessSource : IProcessSource
{
    public static SystemProcessSource Instance { get; } = new();

    private SystemProcessSource()
    {
        using var self = Process.GetCurrentProcess();
        CurrentSessionId = self.SessionId;
        Current = Snapshot(self)!;
    }

    public int CurrentSessionId { get; }

    public ProcessSnapshot Current { get; }

    public IReadOnlyList<ProcessSnapshot> ByName(string name)
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (Snapshot(process) is { } snapshot && snapshot.SessionId == CurrentSessionId)
                {
                    result.Add(snapshot);
                }
            }
        }
        return result.OrderBy(p => p.StartedUtc).ToList();
    }

    public ProcessSnapshot? ById(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return Snapshot(process) is { } snapshot && snapshot.SessionId == CurrentSessionId ? snapshot : null;
        }
        catch (ArgumentException)
        {
            return null; // not running
        }
    }

    private static ProcessSnapshot? Snapshot(Process process)
    {
        try
        {
            string? title = null;
            try
            {
                title = process.MainWindowTitle;
                title = string.IsNullOrEmpty(title) ? null : title;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
            {
            }
            return new ProcessSnapshot(process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), process.SessionId, title);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // exited meanwhile, or not ours to inspect
        }
    }
}

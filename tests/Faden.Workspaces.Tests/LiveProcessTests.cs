using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Faden.Workspaces.Tests;

/// <summary>Real processes: two instances of one program are told apart, and an agent shows on its own instance only.</summary>
public class LiveProcessTests
{
    [Fact]
    public void Two_instances_of_one_program_and_an_agent_in_one_of_them()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // PING.EXE; the logic is covered by the fake-process tests
        }
        var root = Path.Combine(Path.GetTempPath(), "faden-ws-live-" + Guid.NewGuid().ToString("N"));
        using var first = Start();
        using var second = Start();
        try
        {
            var registry = new DeviceAgentRegistry(Path.Combine(root, "agents"));
            var directory = new WorkspaceDirectory(
                [new ProcessOnlyProvider(new ApplicationInfo { Id = "ping", Name = "Ping", ProcessNames = ["PING"] })],
                registry, new DocumentLeases(Path.Combine(root, "leases")));
            var ours = directory.GetInstances("ping").Where(i => i.ProcessId == first.Id || i.ProcessId == second.Id).ToList();
            Assert.Equal(2, ours.Count);
            Assert.NotEqual(ours[0].Handle, ours[1].Handle);

            // An agent working in the second instance (its loop in this process, like a thin host's service).
            var target = ours.Single(i => i.ProcessId == second.Id);
            using var agent = registry.Register(new AgentPresence { Agent = "ping-assistant", Instance = target.Key.ToString() }, autoHeartbeat: false);

            var lines = new SystemTools(directory).FindProcesses("PING.exe").Split('\n');
            Assert.Contains(lines, l => l.StartsWith($"process {first.Id} ") && l.EndsWith("no Fadenstack agent"));
            Assert.Contains(lines, l => l.StartsWith($"process {second.Id} ") && l.Contains("agents: ping-assistant (idle)"));
            Assert.Equal("ping-assistant", Assert.Single(directory.GetInstances("ping").Single(i => i.ProcessId == second.Id).Agents).Agent);
            Assert.Empty(directory.GetInstances("ping").Single(i => i.ProcessId == first.Id).Agents);
        }
        finally
        {
            Stop(first);
            Stop(second);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process Start() => Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    })!;

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
        }
    }
}

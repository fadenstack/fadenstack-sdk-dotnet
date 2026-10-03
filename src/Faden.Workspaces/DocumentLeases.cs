using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Faden.Auth;

namespace Faden.Workspaces;

/// <summary>
/// Short write leases on workspaces, so two agents never change the same document at once -- the Word pane's
/// agent and the desktop agent, say. One file per workspace in the SDK's data folder, created atomically; a
/// lease whose holder process ended or that expired is taken over. Readers need no lease, and the user is never
/// blocked: a lease only coordinates agents.
/// </summary>
public sealed class DocumentLeases
{
    private readonly string _directory;
    private readonly IProcessSource _processes;

    /// <param name="directory">Default: <c>%LOCALAPPDATA%\Fadenstack\leases</c> (the user data folder elsewhere).</param>
    /// <param name="processes">Default: the real processes.</param>
    public DocumentLeases(string? directory = null, IProcessSource? processes = null)
    {
        _directory = directory ?? SecretProtection.DataDirectory("leases");
        Directory.CreateDirectory(_directory);
        _processes = processes ?? SystemProcessSource.Instance;
    }

    /// <summary>The lease, or null when someone else holds it (<see cref="Peek"/> says who).</summary>
    public DocumentLease? TryAcquire(WorkspaceRef workspace, string holder, TimeSpan duration)
    {
        var file = FileFor(workspace);
        var self = _processes.Current;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lease = new LeaseInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Holder = holder,
                HolderProcessId = self.Id,
                HolderStartTicks = self.StartTicks,
                ExpiresUtc = DateTime.UtcNow + duration,
            };
            try
            {
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lease, DeviceAgentRegistry.Json));
                    stream.Write(bytes, 0, bytes.Length);
                }
                return new DocumentLease(file, lease);
            }
            catch (IOException) when (File.Exists(file))
            {
                var current = Read(file);
                if (current is not null && !IsStale(current))
                {
                    return null;
                }
                DeviceAgentRegistry.TryDelete(file); // stale or unreadable: take it over
            }
        }
        return null;
    }

    /// <summary>The current, live lease on the workspace, if any.</summary>
    public LeaseInfo? Peek(WorkspaceRef workspace)
    {
        var current = Read(FileFor(workspace));
        return current is not null && !IsStale(current) ? current : null;
    }

    private bool IsStale(LeaseInfo lease) =>
        DateTime.UtcNow > lease.ExpiresUtc || !_processes.IsAlive(lease.HolderProcessId, lease.HolderStartTicks);

    private static LeaseInfo? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<LeaseInfo>(File.ReadAllText(file), DeviceAgentRegistry.Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string FileFor(WorkspaceRef workspace)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(workspace.Key));
        return Path.Combine(_directory, BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant() + ".json");
    }
}

/// <summary>A held lease; dispose to release it.</summary>
public sealed class DocumentLease : IDisposable
{
    private readonly string _file;
    private bool _released;

    internal DocumentLease(string file, LeaseInfo info)
    {
        _file = file;
        Info = info;
    }

    public LeaseInfo Info { get; private set; }

    /// <summary>Extends the lease (long turns).</summary>
    public void Renew(TimeSpan duration)
    {
        if (_released)
        {
            return;
        }
        Info = Info with { ExpiresUtc = DateTime.UtcNow + duration };
        SecretProtection.WriteOwnerOnly(_file, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Info, DeviceAgentRegistry.Json)));
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        try
        {
            // Only our own: a stale lease of ours may have been taken over.
            var current = JsonSerializer.Deserialize<LeaseInfo>(File.ReadAllText(_file), DeviceAgentRegistry.Json);
            if (current?.Id == Info.Id)
            {
                File.Delete(_file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }
}

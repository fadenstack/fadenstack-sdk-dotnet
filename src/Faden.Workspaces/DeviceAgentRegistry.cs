using System.Text;
using System.Text.Json;
using Faden.Auth;

namespace Faden.Workspaces;

/// <summary>
/// The Fadenstack agents active on this device for this user: each agent registers itself (one small file in
/// the SDK's data folder, owner-only) and keeps a heartbeat. Any process can list them -- the desktop agent, an
/// add-in, a service -- without being linked to them. An entry whose process has ended or restarted, or whose
/// heartbeat stopped, is ignored and removed.
/// </summary>
public sealed class DeviceAgentRegistry
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly IProcessSource _processes;
    private readonly TimeSpan _staleAfter;

    /// <param name="directory">Default: <c>%LOCALAPPDATA%\Fadenstack\agents</c> (the user data folder elsewhere).</param>
    /// <param name="processes">Default: the real processes.</param>
    /// <param name="staleAfter">How long an entry lives without a heartbeat (default 2 minutes).</param>
    public DeviceAgentRegistry(string? directory = null, IProcessSource? processes = null, TimeSpan? staleAfter = null)
    {
        _directory = directory ?? SecretProtection.DataDirectory("agents");
        Directory.CreateDirectory(_directory);
        _processes = processes ?? SystemProcessSource.Instance;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>How often a registration renews itself (a third of the stale time).</summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromTicks(_staleAfter.Ticks / 3);

    /// <summary>
    /// Registers an agent of this process. The host process fields are filled in; the registration renews
    /// itself until disposed.
    /// </summary>
    public AgentRegistration Register(AgentPresence presence, bool autoHeartbeat = true)
    {
        var self = _processes.Current;
        var entry = presence with { HostProcessId = self.Id, HostStartTicks = self.StartTicks, HeartbeatUtc = DateTime.UtcNow };
        var file = Path.Combine(_directory, FileName(entry));
        var registration = new AgentRegistration(file, entry, autoHeartbeat ? HeartbeatInterval : null);
        Write(file, entry);
        return registration;
    }

    /// <summary>The live agents, oldest heartbeat first.</summary>
    public IReadOnlyList<AgentPresence> List()
    {
        var result = new List<AgentPresence>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            AgentPresence? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AgentPresence>(File.ReadAllText(file), Json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue; // being rewritten, or not ours
            }
            if (entry is null)
            {
                continue;
            }
            if (!_processes.IsAlive(entry.HostProcessId, entry.HostStartTicks) || DateTime.UtcNow - entry.HeartbeatUtc > _staleAfter)
            {
                TryDelete(file);
                continue;
            }
            result.Add(entry);
        }
        return result.OrderBy(e => e.HeartbeatUtc).ToList();
    }

    internal static void Write(string file, AgentPresence entry) =>
        SecretProtection.WriteOwnerOnly(file, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json)));

    internal static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FileName(AgentPresence entry)
    {
        var name = new StringBuilder();
        foreach (var c in $"{entry.Agent}-{entry.HostProcessId}-{entry.Instance}-{entry.Window}")
        {
            name.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        return name + ".json";
    }
}

/// <summary>An agent's entry in the <see cref="DeviceAgentRegistry"/>. Dispose when the agent closes.</summary>
public sealed class AgentRegistration : IDisposable
{
    private readonly string _file;
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private AgentPresence _entry;
    private bool _disposed;

    internal AgentRegistration(string file, AgentPresence entry, TimeSpan? heartbeat)
    {
        _file = file;
        _entry = entry;
        if (heartbeat is { } interval)
        {
            _timer = new Timer(_ => Heartbeat(), null, interval, interval);
        }
    }

    public AgentPresence Current
    {
        get
        {
            lock (_gate)
            {
                return _entry;
            }
        }
    }

    /// <summary>Busy while a turn runs; the workspaces the agent is attached to.</summary>
    public void Update(AgentStatus? status = null, IReadOnlyList<string>? workspaces = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _entry = _entry with
            {
                Status = status ?? _entry.Status,
                Workspaces = workspaces ?? _entry.Workspaces,
                HeartbeatUtc = DateTime.UtcNow,
            };
            DeviceAgentRegistry.Write(_file, _entry);
        }
    }

    public void Heartbeat() => Update();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _timer?.Dispose();
        DeviceAgentRegistry.TryDelete(_file);
    }
}

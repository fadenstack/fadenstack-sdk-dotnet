using System.Text;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Storage;

namespace Fadenstack.Agents.Sessions;

/// <summary>Where an agent's sessions live: on the device. Integrators may plug their own (e.g. in the host's project).</summary>
public interface ISessionStore
{
    Task AppendAsync(string agent, string sessionId, TranscriptEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranscriptEntry>> ReadAsync(string agent, string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionInfo>> ListAsync(string agent, CancellationToken cancellationToken = default);

    Task DeleteAsync(string agent, string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>How many sessions, and how old, are kept per agent (defaults: 50, 90 days).</summary>
public sealed record SessionRetention(int MaxSessions = 50, int MaxAgeDays = 90)
{
    public static SessionRetention Default { get; } = new();

    /// <summary>The stricter of these limits and the server's (<c>local_retention</c>); a limit the server leaves out stays.</summary>
    internal SessionRetention Within(LocalRetention? server) => server is null
        ? this
        : new SessionRetention(Math.Min(MaxSessions, server.MaxSessions ?? MaxSessions), Math.Min(MaxAgeDays, server.MaxAgeDays ?? MaxAgeDays));
}

/// <summary>Sessions in memory only: gone with the process.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, List<TranscriptEntry>> _sessions = new();

    public Task AppendAsync(string agent, string sessionId, TranscriptEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(Key(agent, sessionId), out var entries))
            {
                _sessions[Key(agent, sessionId)] = entries = new List<TranscriptEntry>();
            }
            entries.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptEntry>> ReadAsync(string agent, string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sessions)
        {
            return Task.FromResult<IReadOnlyList<TranscriptEntry>>(
                _sessions.TryGetValue(Key(agent, sessionId), out var entries) ? entries.ToList() : new List<TranscriptEntry>());
        }
    }

    public Task<IReadOnlyList<SessionInfo>> ListAsync(string agent, CancellationToken cancellationToken = default)
    {
        lock (_sessions)
        {
            return Task.FromResult<IReadOnlyList<SessionInfo>>(_sessions
                .Where(p => p.Key.StartsWith(agent + "/", StringComparison.Ordinal) && p.Value.Count > 0)
                .Select(p => Info(agent, p.Key.Substring(agent.Length + 1), p.Value))
                .OrderByDescending(s => s.LastActivity).ToList());
        }
    }

    public Task DeleteAsync(string agent, string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sessions)
        {
            _sessions.Remove(Key(agent, sessionId));
        }
        return Task.CompletedTask;
    }

    private static string Key(string agent, string sessionId) => agent + "/" + sessionId;

    internal static SessionInfo Info(string agent, string id, IReadOnlyList<TranscriptEntry> entries)
    {
        var first = entries[0];
        var title = entries.FirstOrDefault(e => e.Type == EntryTypes.UserMessage) is { } u
            && u.Data.ValueKind == JsonValueKind.Object && u.Data.TryGetProperty("text", out var t)
            ? Title(t.GetString()) : null;
        return new SessionInfo(id, agent, first.Timestamp, entries[entries.Count - 1].Timestamp, title);
    }

    private static string? Title(string? text) =>
        text is null ? null : text.Length <= 60 ? text : text.Substring(0, 57).TrimEnd() + "...";
}

/// <summary>
/// One append-only JSONL file per session, per agent, in the signed-in user's own folder
/// (<c>%LOCALAPPDATA%\Fadenstack\users\&lt;user&gt;\sessions\&lt;agent&gt;\</c>; <c>...\Fadenstack\sessions\</c> when the
/// server names no user). Each line is encrypted for the current user
/// (DPAPI on Windows); elsewhere the files are readable by their owner only.
/// </summary>
public sealed class FileSessionStore : ISessionStore, IListsAgents
{
    private readonly string _root;
    private readonly ISecretProtector _protector;
    private readonly SessionRetention _retention;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileSessionStore(string? directory = null, ISecretProtector? protector = null, SessionRetention? retention = null)
    {
        _root = directory ?? FadenstackPaths.Data("sessions");
        Directory.CreateDirectory(_root);
        _protector = protector ?? SecretProtection.Default;
        _retention = retention ?? SessionRetention.Default;
    }

    public async Task AppendAsync(string agent, string sessionId, TranscriptEntry entry, CancellationToken cancellationToken = default)
    {
        var path = PathOf(agent, sessionId);
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, FadenstackJson.Options);
        var line = Convert.ToBase64String(_protector.Protect(json)) + "\n";
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var created = !File.Exists(path);
            File.AppendAllText(path, line, Encoding.ASCII);
            if (created)
            {
                SecretProtection.OwnerOnly(path, directory: false);
                Prune(agent, keep: sessionId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<TranscriptEntry>> ReadAsync(string agent, string sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathOf(agent, sessionId);
        var entries = new List<TranscriptEntry>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path, Encoding.ASCII))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                var json = _protector.Unprotect(Convert.FromBase64String(line));
                entries.Add(JsonSerializer.Deserialize<TranscriptEntry>(json, FadenstackJson.Options)!);
            }
        }
        return Task.FromResult<IReadOnlyList<TranscriptEntry>>(entries);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(string agent, CancellationToken cancellationToken = default)
    {
        var sessions = new List<SessionInfo>();
        foreach (var file in Directory.GetFiles(AgentDirectory(agent), "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var entries = await ReadAsync(agent, id, cancellationToken).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                sessions.Add(MemorySessionStore.Info(agent, id, entries));
            }
        }
        return sessions.OrderByDescending(s => s.LastActivity).ToList();
    }

    public Task DeleteAsync(string agent, string sessionId, CancellationToken cancellationToken = default)
    {
        var path = PathOf(agent, sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    IReadOnlyList<string> IListsAgents.Agents() => Directory.Exists(_root)
        ? Directory.GetDirectories(_root).Select(d => Path.GetFileName(d)).ToList()
        : new List<string>();

    /// <summary>Keeps the newest <see cref="SessionRetention.MaxSessions"/> not older than the age limit.</summary>
    private void Prune(string agent, string keep)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retention.MaxAgeDays);
        var others = Directory.GetFiles(AgentDirectory(agent), "*.jsonl")
            .Select(f => new FileInfo(f))
            .Where(f => Path.GetFileNameWithoutExtension(f.Name) != FadenstackPaths.SafeName(keep))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        for (var i = 0; i < others.Count; i++)
        {
            if (i >= _retention.MaxSessions - 1 || others[i].LastWriteTimeUtc < cutoff) // the new session is one of them
            {
                others[i].Delete();
            }
        }
    }

    private string AgentDirectory(string agent)
    {
        var path = Path.Combine(_root, FadenstackPaths.SafeName(agent));
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            SecretProtection.OwnerOnly(path, directory: true);
        }
        return path;
    }

    private string PathOf(string agent, string sessionId) => Path.Combine(AgentDirectory(agent), FadenstackPaths.SafeName(sessionId) + ".jsonl");
}

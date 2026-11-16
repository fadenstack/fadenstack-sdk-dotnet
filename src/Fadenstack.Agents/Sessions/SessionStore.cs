using System.Text;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

/// <summary>A store that can delete a person's old chats by the rules in force (the SDK's own <see cref="FileSessionStore"/>).</summary>
internal interface IPrunesSessions
{
    /// <summary>Deletes the agent's chats that are too old or too many; <paramref name="keep"/> (the open ones) are never deleted. Returns how many went.</summary>
    Task<int> PruneAsync(string agent, IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default);
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
/// (<c>%LOCALAPPDATA%\Fadenstack\users\&lt;user&gt;\sessions\&lt;agent&gt;\</c>; <c>...\Fadenstack\sessions\&lt;agent&gt;\</c>
/// when the server names no user). Each line is encrypted for the current user (DPAPI on Windows); elsewhere the files
/// are readable by their owner only.
/// </summary>
public sealed class FileSessionStore : ISessionStore, IListsAgents, IPrunesSessions
{
    private readonly string _root;
    private readonly ISecretProtector _protector;
    private readonly Func<SessionRetention?> _retention;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _log;

    /// <param name="directory">The folder; the data root's <c>sessions</c> by default.</param>
    /// <param name="protector">How each line is encrypted; <see cref="SecretProtection.Default"/> by default.</param>
    /// <param name="retention">How many chats, and how old, are kept.</param>
    /// <param name="logger">Where lines and files that cannot be read are logged (counts and kinds only); nowhere if not given.</param>
    public FileSessionStore(string? directory = null, ISecretProtector? protector = null, SessionRetention? retention = null,
        ILogger? logger = null)
        : this(directory, protector, Fixed(retention ?? SessionRetention.Default), logger)
    {
    }

    private static Func<SessionRetention?> Fixed(SessionRetention retention) => () => retention;

    /// <summary>A store whose limits are read each time they are needed: null means no automatic deletion (the person turned it off).</summary>
    internal FileSessionStore(string? directory, ISecretProtector? protector, Func<SessionRetention?> retention, ILogger? logger)
    {
        _root = directory ?? FadenstackPaths.Data("sessions");
        Directory.CreateDirectory(_root);
        _protector = protector ?? SecretProtection.Default;
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _log = logger ?? NullLogger.Instance;
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
                Prune(agent, new[] { sessionId });
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The chat's entries. A line that cannot be read (torn by a crash while it was appended, another user's, damaged) is
    /// skipped and the rest kept; a file another process holds for longer than a moment reads as empty for now. The log
    /// gives counts and kinds only.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptEntry>> ReadAsync(string agent, string sessionId, CancellationToken cancellationToken = default)
    {
        var entries = new List<TranscriptEntry>();
        byte[]? bytes;
        try
        {
            bytes = await SecretProtection.ReadKeptAsync(PathOf(agent, sessionId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ChatUnreadable(_log, e.GetType().Name);
            return entries;
        }
        if (bytes is null)
        {
            return entries;
        }
        var skipped = 0;
        string? firstKind = null;
        using var lines = new StringReader(Encoding.ASCII.GetString(bytes));
        for (var line = lines.ReadLine(); line is not null; line = lines.ReadLine())
        {
            if (line.Length == 0)
            {
                continue;
            }
            try
            {
                var json = _protector.Unprotect(Convert.FromBase64String(line));
                entries.Add(JsonSerializer.Deserialize<TranscriptEntry>(json, FadenstackJson.Options)
                    ?? throw new JsonException("The line holds no entry."));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Any failure to decode one line -- the host's protector may throw its own kind -- costs that line only.
                skipped++;
                firstKind ??= e.GetType().Name;
            }
        }
        if (skipped > 0)
        {
            Log.ChatLinesSkipped(_log, skipped, firstKind!);
        }
        return entries;
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

    /// <summary>
    /// Deletes the chats that are too old or beyond the newest <see cref="SessionRetention.MaxSessions"/>, when automatic deletion is on
    /// (at the agent's start, when the person turns it on or lowers a limit). The chats in <paramref name="keep"/> are open: they are
    /// never deleted, and they count among the chats kept.
    /// </summary>
    public async Task<int> PruneAsync(string agent, IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Prune(agent, keep);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Keeps the newest <see cref="SessionRetention.MaxSessions"/> not older than the age limit; the <paramref name="keep"/> chats always stay.</summary>
    private int Prune(string agent, IReadOnlyCollection<string> keep)
    {
        if (_retention() is not { } retention)
        {
            return 0; // off: every chat stays until the person deletes it
        }
        var cutoff = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);
        var open = new HashSet<string>(keep.Select(FadenstackPaths.SafeName), StringComparer.Ordinal);
        var files = Directory.GetFiles(AgentDirectory(agent), "*.jsonl").Select(f => new FileInfo(f)).ToList();
        var others = files.Where(f => !open.Contains(Path.GetFileNameWithoutExtension(f.Name)))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var room = Math.Max(0, retention.MaxSessions - (files.Count - others.Count)); // the open ones are among the kept
        var removed = 0;
        for (var i = 0; i < others.Count; i++)
        {
            if (i >= room || others[i].LastWriteTimeUtc < cutoff)
            {
                others[i].Delete();
                removed++;
            }
        }
        return removed;
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

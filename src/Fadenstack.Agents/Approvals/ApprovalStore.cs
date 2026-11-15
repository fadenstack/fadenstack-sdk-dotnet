using System.Security.Cryptography;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Agents;

/// <summary>A tool the user allowed always, for one agent on this device.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="ToolClass">The class it had then: the approval holds only while the tool still has it.</param>
/// <param name="ApprovedAt">When the user allowed it.</param>
public sealed record StoredApproval(string Tool, ToolClass ToolClass, DateTimeOffset ApprovedAt)
{
    /// <summary>
    /// Where the tool comes from (<c>host</c>, <c>mcp</c>, <c>skills</c>, <c>web</c>, ...): an approval holds only for a tool of the
    /// same name from the same source and server (sdk/51 F08 §4), so a new MCP server bringing a tool of that name inherits no
    /// "always allow" (ASVS 5.0 V8). Null in an approval kept before 0.3.0, which answers nothing: the person is asked again.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>The MCP server's label, for an MCP tool; null otherwise.</summary>
    public string? Server { get; init; }

    /// <summary>Whether this approval answers a call of <paramref name="tool"/> with this class, source and server.</summary>
    public bool Covers(string tool, ToolClass toolClass, string source, string? server) =>
        Tool == tool && ToolClass == toolClass && Source is not null && Source == source && Server == server;

    /// <summary>The same tool from the same place (what a new approval replaces); an approval without a source is replaced by any.</summary>
    internal bool SameTool(StoredApproval other) => Tool == other.Tool && (Source is null || (Source == other.Source && Server == other.Server));
}

/// <summary>
/// Where "Allow always" is kept, per agent, on this device. Like the session store, integrators may plug their own.
/// The agent checks a stored approval at every call: a tool that became destructive or unclassified, or that an
/// admin's rule covers, asks again.
/// </summary>
public interface IApprovalStore
{
    Task<IReadOnlyList<StoredApproval>> ListAsync(string agent, CancellationToken cancellationToken = default);

    /// <summary>Adds one, replacing the tool's earlier approval from the same source and server.</summary>
    Task AddAsync(string agent, StoredApproval approval, CancellationToken cancellationToken = default);

    /// <summary>Forgets one tool's approval, or all of the agent's when <paramref name="tool"/> is null.</summary>
    Task RemoveAsync(string agent, string? tool = null, CancellationToken cancellationToken = default);
}

/// <summary>Approvals in memory only: gone with the process.</summary>
public sealed class MemoryApprovalStore : IApprovalStore
{
    private readonly Dictionary<string, List<StoredApproval>> _approvals = new();

    public Task<IReadOnlyList<StoredApproval>> ListAsync(string agent, CancellationToken cancellationToken = default)
    {
        lock (_approvals)
        {
            return Task.FromResult<IReadOnlyList<StoredApproval>>(
                _approvals.TryGetValue(agent, out var list) ? list.ToList() : new List<StoredApproval>());
        }
    }

    public Task AddAsync(string agent, StoredApproval approval, CancellationToken cancellationToken = default)
    {
        lock (_approvals)
        {
            var kept = _approvals.TryGetValue(agent, out var list) ? list.Where(a => !a.SameTool(approval)).ToList() : new List<StoredApproval>();
            kept.Add(approval);
            _approvals[agent] = kept;
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string agent, string? tool = null, CancellationToken cancellationToken = default)
    {
        lock (_approvals)
        {
            if (tool is null)
            {
                _approvals.Remove(agent);
            }
            else if (_approvals.TryGetValue(agent, out var list))
            {
                list.RemoveAll(a => a.Tool == tool);
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// One file per agent in the signed-in user's own folder (<c>%LOCALAPPDATA%\Fadenstack\users\&lt;user&gt;\approvals\</c>;
/// <c>...\Fadenstack\approvals\</c> when the server names no user), encrypted for the current user like the sessions
/// (DPAPI on Windows); elsewhere the files are readable by their owner only.
/// <para>
/// Each approval is sealed (sdk/51 F08 §1): an HMAC-SHA256 over the agent, the tool, its class, source and server and the time,
/// under a key of this folder's own (<see cref="SealKeyName"/> in it, encrypted like the approvals). An approval whose
/// seal does not hold answers nothing and the person is asked again: one planted in the file, one copied from another agent's
/// file or another person's folder, or one kept before 0.3.0 (ASVS 5.0 V8, authorization data the device keeps; V14, data at
/// rest).
/// </para>
/// </summary>
public sealed class FileApprovalStore : IApprovalStore, IListsAgents
{
    private const string SealVersion = "fadenstack.approval.v1";

    /// <summary>The seal key's file, in the store's own folder beside the agents' files.</summary>
    public const string SealKeyName = "approvals-seal.key";

    private readonly string _root;
    private readonly string _sealKeyFile;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _log;
    private byte[]? _sealKey;

    /// <param name="directory">The folder; the data root's <c>approvals</c> by default.</param>
    /// <param name="protector">How the files are encrypted; <see cref="SecretProtection.Default"/> by default.</param>
    /// <param name="logger">Where approvals that cannot be read are logged (the kind of failure only); nowhere if not given.</param>
    public FileApprovalStore(string? directory = null, ISecretProtector? protector = null, ILogger? logger = null)
    {
        _root = directory ?? FadenstackPaths.Data("approvals");
        Directory.CreateDirectory(_root);
        _sealKeyFile = Path.Combine(_root, SealKeyName);
        _protector = protector ?? SecretProtection.Default;
        _log = logger ?? NullLogger.Instance;
    }

    public async Task<IReadOnlyList<StoredApproval>> ListAsync(string agent, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(agent, forWrite: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddAsync(string agent, StoredApproval approval, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = (await ReadAsync(agent, forWrite: true, cancellationToken).ConfigureAwait(false)).Where(a => !a.SameTool(approval)).ToList();
            kept.Add(approval);
            Write(agent, kept);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string agent, string? tool = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Write(agent, tool is null
                ? new List<StoredApproval>()
                : (await ReadAsync(agent, forWrite: true, cancellationToken).ConfigureAwait(false)).Where(a => a.Tool != tool).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    IReadOnlyList<string> IListsAgents.Agents() => Directory.Exists(_root)
        ? Directory.GetFiles(_root, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).ToList()
        : new List<string>();

    /// <summary>
    /// The agent's approvals; none when the file is another user's or damaged, or (to list them) is locked by another
    /// process for longer than a moment: the user is then asked again, and the log names the kind of failure. Before a
    /// write, a locked file stays an error, so the approvals in it are not written over.
    /// </summary>
    private async Task<List<StoredApproval>> ReadAsync(string agent, bool forWrite, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await SecretProtection.ReadKeptAsync(PathOf(agent), cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return new List<StoredApproval>();
            }
            var records = JsonSerializer.Deserialize<List<SealedApproval>>(_protector.Unprotect(bytes), FadenstackJson.Options) ?? new List<SealedApproval>();
            var key = SealKey(create: false);
            var held = records.Where(r => key is not null && r.Seal is { } seal && FixedEquals(seal, Seal(key, agent, r))).Select(r => r.ToApproval()).ToList();
            if (held.Count < records.Count)
            {
                Log.ApprovalsUnreadable(_log, "unsealed"); // planted, copied, or kept before 0.3.0: asked again
            }
            return held;
        }
        catch (Exception e) when (e is CryptographicException or JsonException
            || (!forWrite && e is IOException or UnauthorizedAccessException))
        {
            Log.ApprovalsUnreadable(_log, e.GetType().Name);
            return new List<StoredApproval>();
        }
    }

    private void Write(string agent, List<StoredApproval> approvals)
    {
        var path = PathOf(agent);
        if (approvals.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        Directory.CreateDirectory(_root); // the user's data may have been deleted (FadenstackAgent.DeleteMyDataAsync)
        var key = SealKey(create: true)!;
        var records = approvals.Select(a => SealedApproval.From(a)).Select(r => r with { Seal = Seal(key, agent, r) }).ToList();
        SecretProtection.WriteOwnerOnly(path, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(records, FadenstackJson.Options)));
    }

    /// <summary>The seal of one approval: HMAC-SHA256 over the version, the agent and every field but the seal, base64.</summary>
    private static string Seal(byte[] key, string agent, SealedApproval record)
    {
        var text = string.Join("\n", SealVersion, FadenstackPaths.SafeName(agent), record.Tool, record.ToolClass.ToWire(),
            record.Source ?? "", record.Server ?? "", record.ApprovedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        using var mac = new HMACSHA256(key);
        return Convert.ToBase64String(mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static bool FixedEquals(string a, string b)
    {
        var x = System.Text.Encoding.ASCII.GetBytes(a);
        var y = System.Text.Encoding.ASCII.GetBytes(b);
        if (x.Length != y.Length)
        {
            return false;
        }
        var diff = 0;
        for (var i = 0; i < x.Length; i++)
        {
            diff |= x[i] ^ y[i];
        }
        return diff == 0;
    }

    /// <summary>
    /// This folder's seal key: 32 random bytes, encrypted with the store's protector (owner-only where nothing encrypts), made at the
    /// first write. Null before that, or when it cannot be read: then no approval is held.
    /// </summary>
    private byte[]? SealKey(bool create)
    {
        if (_sealKey is not null)
        {
            return _sealKey;
        }
        try
        {
            if (File.Exists(_sealKeyFile))
            {
                return _sealKey = _protector.Unprotect(SecretProtection.ReadShared(_sealKeyFile)) is { Length: 32 } kept ? kept : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
        if (!create)
        {
            return null;
        }
        var key = new byte[32];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(key);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_sealKeyFile)!);
        SecretProtection.WriteOwnerOnly(_sealKeyFile, _protector.Protect(key));
        return _sealKey = key;
    }

    /// <summary>An approval as the file keeps it, with its seal.</summary>
    private sealed record SealedApproval(string Tool, ToolClass ToolClass, DateTimeOffset ApprovedAt)
    {
        public string? Source { get; init; }

        public string? Server { get; init; }

        public string? Seal { get; init; }

        public static SealedApproval From(StoredApproval a) => new(a.Tool, a.ToolClass, a.ApprovedAt) { Source = a.Source, Server = a.Server };

        public StoredApproval ToApproval() => new(Tool, ToolClass, ApprovedAt) { Source = Source, Server = Server };
    }

    private string PathOf(string agent) => Path.Combine(_root, FadenstackPaths.SafeName(agent) + ".json");
}

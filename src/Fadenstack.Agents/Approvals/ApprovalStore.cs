using System.Security.Cryptography;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Storage;
using Fadenstack.Tools;

namespace Fadenstack.Agents;

/// <summary>A tool the user allowed always, for one agent on this device.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="ToolClass">The class it had then: the approval holds only while the tool still has it.</param>
/// <param name="ApprovedAt">When the user allowed it.</param>
public sealed record StoredApproval(string Tool, ToolClass ToolClass, DateTimeOffset ApprovedAt);

/// <summary>
/// Where "Allow always" is kept, per agent, on this device. Like the session store, integrators may plug their own.
/// The agent checks a stored approval at every call: a tool that became destructive or unclassified, or that an
/// admin's rule covers, asks again.
/// </summary>
public interface IApprovalStore
{
    Task<IReadOnlyList<StoredApproval>> ListAsync(string agent, CancellationToken cancellationToken = default);

    /// <summary>Adds one, replacing the tool's earlier approval.</summary>
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
            var kept = _approvals.TryGetValue(agent, out var list) ? list.Where(a => a.Tool != approval.Tool).ToList() : new List<StoredApproval>();
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
/// </summary>
public sealed class FileApprovalStore : IApprovalStore
{
    private readonly string _root;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileApprovalStore(string? directory = null, ISecretProtector? protector = null)
    {
        _root = directory ?? FadenstackPaths.Data("approvals");
        Directory.CreateDirectory(_root);
        _protector = protector ?? SecretProtection.Default;
    }

    public async Task<IReadOnlyList<StoredApproval>> ListAsync(string agent, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Read(agent);
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
            var kept = Read(agent).Where(a => a.Tool != approval.Tool).ToList();
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
            Write(agent, tool is null ? new List<StoredApproval>() : Read(agent).Where(a => a.Tool != tool).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<StoredApproval> Read(string agent)
    {
        var path = PathOf(agent);
        if (!File.Exists(path))
        {
            return new List<StoredApproval>();
        }
        try
        {
            var json = _protector.Unprotect(File.ReadAllBytes(path));
            return JsonSerializer.Deserialize<List<StoredApproval>>(json, FadenstackJson.Options) ?? new List<StoredApproval>();
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return new List<StoredApproval>(); // another user's, or damaged: the user is asked again
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
        SecretProtection.WriteOwnerOnly(path, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(approvals, FadenstackJson.Options)));
    }

    private string PathOf(string agent) => Path.Combine(_root, FadenstackPaths.SafeName(agent) + ".json");
}

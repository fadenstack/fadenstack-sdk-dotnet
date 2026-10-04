using System.Text.Json;
using Faden.Http;
using Faden.Models;

namespace Faden.Resources;

/// <summary>The client's agent: its profile, the events the SDK reports, and approvals the gateway waits for.</summary>
public sealed class AgentResource
{
    private readonly FadenTransport _transport;
    private readonly string _agent;

    internal AgentResource(FadenTransport transport, string agent)
    {
        _transport = transport;
        _agent = agent;
    }

    private Uri Route(string rest) => _transport.Gateway($"v1/agents/{Uri.EscapeDataString(_agent)}/{rest}");

    /// <summary>The agent's current version: model, tool contract, local MCP mode, tool modes; not its instructions.</summary>
    public Task<AgentProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<AgentProfile>(Route("profile"), cancellationToken);

    /// <summary>Reports what the SDK did -- names, counts, outcomes. Up to 100 per call.</summary>
    public async Task ReportAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }
        // Serialized by their runtime type, so each carries its own fields.
        var batch = new { events = events.Select(e => (object)e).ToList() };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(batch, FadenJson.Options));
        await _transport.PostAsync<bool>(Route("events"), doc.RootElement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the server what the app brings -- its tools with their classes, its context, its greeting -- for its
    /// admins to review. The same manifest again is only counted (<see cref="ManifestReceipt.IsNew"/> false).
    /// </summary>
    public Task<ManifestReceipt> ReportManifestAsync(AppManifest manifest, CancellationToken cancellationToken = default) =>
        _transport.PutAsync<ManifestReceipt>(Route("manifest"), manifest, cancellationToken);

    /// <summary>Approves or declines a gateway tool call waiting for the user (<c>faden.approval_required</c>).</summary>
    public Task AnswerApprovalAsync(string approvalId, bool approved, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<bool>(Route($"approvals/{Uri.EscapeDataString(approvalId)}"), new { approved },
            cancellationToken);
}

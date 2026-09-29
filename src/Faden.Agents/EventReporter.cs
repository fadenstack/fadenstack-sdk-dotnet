using Faden.Models;

namespace Faden.Agents;

/// <summary>
/// Tells the server what happened on the device -- tool runs, approvals, session counts -- as names, durations
/// and outcomes, never content (06 §5). Sent after each turn; a report that fails is dropped, never the turn.
/// </summary>
internal sealed class EventReporter
{
    private const int Batch = 100; // the server takes at most 100 per request
    private readonly FadenClient _client;
    private readonly bool _enabled;
    private readonly List<AgentClientEvent> _pending = new();

    public EventReporter(FadenClient client, bool enabled)
    {
        _client = client;
        _enabled = enabled && client.Agent is not null;
    }

    /// <summary>The last report that failed, for diagnostics.</summary>
    public Exception? LastError { get; private set; }

    public void Add(AgentClientEvent clientEvent)
    {
        if (!_enabled)
        {
            return;
        }
        lock (_pending)
        {
            _pending.Add(clientEvent);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            List<AgentClientEvent> batch;
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    return;
                }
                batch = _pending.Take(Batch).ToList();
                _pending.RemoveRange(0, batch.Count);
            }
            await SendAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReportNowAsync(IReadOnlyList<AgentClientEvent> events) => _enabled ? SendAsync(events, CancellationToken.None) : Task.CompletedTask;

    private async Task SendAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Agent!.ReportAsync(events, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e;
        }
    }
}

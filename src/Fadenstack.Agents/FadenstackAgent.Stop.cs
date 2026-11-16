using Fadenstack.Models;

namespace Fadenstack.Agents;

/// <summary>What <see cref="FadenstackAgent.StopAllAsync(CancellationToken)"/> did: counts, never content. TypeScript: <c>StopAllResult</c>.</summary>
public sealed record StopAllResult
{
    /// <summary>The turns that were running and were stopped: one per chat that was busy.</summary>
    public int TurnsStopped { get; init; }

    /// <summary>
    /// The grants that ended: the server's grants of this device (time, work, plan, Bypass) and the tools the person had allowed for
    /// a chat. Those the server could not be told about (<see cref="GrantsNotTold"/>) are counted too: they ended on this device.
    /// </summary>
    public int GrantsEnded { get; init; }

    /// <summary>How many of <see cref="GrantsEnded"/> are the chats' "for this chat" approvals.</summary>
    public int ChatApprovalsEnded { get; init; }

    /// <summary>
    /// The server's grants it could not be told about (offline, or it kept refusing): they ended on this device at once and end at
    /// their time on the server.
    /// </summary>
    public int GrantsNotTold { get; init; }

    /// <summary>Whether nothing ran and nothing was granted.</summary>
    public bool NothingRan => TurnsStopped == 0 && GrantsEnded == 0;

    /// <summary>The sum of two results (several agents).</summary>
    public StopAllResult Plus(StopAllResult other) => new()
    {
        TurnsStopped = TurnsStopped + other.TurnsStopped,
        GrantsEnded = GrantsEnded + other.GrantsEnded,
        ChatApprovalsEnded = ChatApprovalsEnded + other.ChatApprovalsEnded,
        GrantsNotTold = GrantsNotTold + other.GrantsNotTold,
    };
}

public sealed partial class FadenstackAgent
{
    /// <summary>
    /// The fixed sentence for a turned-off agent: what the person reads (the chat view words it in their language), and what the
    /// server's <c>agent_disabled</c> says. TypeScript: <c>turnedOffText</c>.
    /// </summary>
    public const string TurnedOffText = "Your organisation turned this agent off.";

    private int _turnedOff;

    /// <summary>
    /// Whether the organisation has turned this agent off (core 0.7.1, the off-switch). True once the profile at start says
    /// <c>enabled: false</c>, or any agent route answers 403 <c>agent_disabled</c>. Then the running turns stop and the grants end, a new
    /// turn is refused without a request (<see cref="Governance.ChatOutcome.AgentDisabled"/>), and the agent makes no further request
    /// but its events and its manifest: nothing retries. <see cref="CheckAgainAsync"/> reads the profile again; <c>enabled: true</c>
    /// clears this. TypeScript: <c>turnedOff</c>.
    /// </summary>
    public bool TurnedOff => Volatile.Read(ref _turnedOff) == 1;

    /// <summary>Raised when <see cref="TurnedOff"/> changes, on whatever thread saw it. TypeScript: <c>onTurnedOffChange</c>.</summary>
    public event EventHandler? TurnedOffChanged;

    bool IAgentSwitch.TurnedOff => TurnedOff;

    void IAgentSwitch.MarkTurnedOff() => MarkTurnedOff();

    /// <summary>
    /// The agent is off: stops the running turns and ends the grants on this device (the server ended its own, <c>revoked_by:
    /// agent_turned_off</c>; nothing here needs it), then says so. Once; asking the server again would only be refused.
    /// </summary>
    internal void MarkTurnedOff() => SetOff(true);

    private void SetOff(bool off)
    {
        if (Interlocked.Exchange(ref _turnedOff, off ? 1 : 0) == (off ? 1 : 0))
        {
            return;
        }
        if (off)
        {
            Log.AgentTurnedOff(Logger, Slug);
            StopLocal(forOff: true); // the local part of "Stop everything": the grants end here, whether or not the server is asked
        }
        TurnedOffChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// "Try again" on a turned-off agent: reads the profile (the one route the server still answers for it). When it says
    /// <c>enabled: true</c> the agent is back on (<see cref="TurnedOff"/> false, the event raised); it says so by the return value, the
    /// new <see cref="TurnedOff"/>. A server that cannot be reached leaves it as it was. TypeScript: <c>checkAgain()</c>.
    /// </summary>
    public async Task<bool> CheckAgainAsync(CancellationToken cancellationToken = default)
    {
        if (_profile is null)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            return TurnedOff;
        }
        await RefreshProfileAsync(cancellationToken, force: true).ConfigureAwait(false);
        return TurnedOff;
    }

    // The chats this agent gave out and that still live.
    private List<ChatSession> OpenChats()
    {
        lock (_handedOut)
        {
            _handedOut.RemoveAll(r => !r.TryGetTarget(out _));
            return _handedOut.Select(r => r.TryGetTarget(out var s) ? s : null!).Where(s => s is not null).ToList();
        }
    }

    /// <summary>
    /// The part of "stop everything" that needs no server and always happens: every running turn is stopped, each chat's "for this
    /// chat" approvals end, and every grant of this device's copy ends (it covers no call from now on). Returns what the server may
    /// still be told.
    /// </summary>
    private StopPlan StopLocal(bool forOff = false)
    {
        var turns = 0;
        var chatApprovals = 0;
        foreach (var chat in OpenChats())
        {
            if (forOff ? chat.StopForOff() : chat.Stop())
            {
                turns++;
            }
            chatApprovals += chat.EndChatApprovals();
        }
        return new StopPlan(turns, chatApprovals, Grants.EndAllHere().ToList());
    }

    private sealed record StopPlan(int Turns, int ChatApprovals, List<AgentGrant> Grants);

    /// <summary>How long a read of the server's grants may take when someone presses Stop: the stop does not wait for a slow server.</summary>
    internal TimeSpan StopServerWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stops everything this agent does for the person, at once (the emergency stop; release plan 8.11). Every running turn in every
    /// open chat stops the way the chat's own Stop does (it ends as stopped, a tool call waiting for an answer is answered no); then every
    /// grant the agent holds for the person ends: the server's (for a time, for a stretch of work, plan, Bypass) and each chat's
    /// "for this chat" approvals. What the person allowed <em>always</em> stays: it is their preference, not running authority, and
    /// <see cref="ResetApprovalsAsync"/> forgets it. The part on this device always happens, also offline: a grant the server could not
    /// be told about is counted (<see cref="StopAllResult.GrantsNotTold"/>), tried once more and never blocks the stop. With the
    /// agent turned off the server ended its grants itself and is not asked. TypeScript: <c>stopAll()</c>.
    /// </summary>
    public async Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken = default)
    {
        var plan = StopLocal();
        return await TellServerAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="StopAllAsync(CancellationToken)"/> for each of <paramref name="agents"/> (an app with more than one): the stops on
    /// this device all happen first, then the servers are told, at the same time. The result is the sum. TypeScript: <c>stopAll(agents)</c>.
    /// </summary>
    public static async Task<StopAllResult> StopAllAsync(IEnumerable<FadenstackAgent> agents, CancellationToken cancellationToken = default)
    {
        if (agents is null)
        {
            throw new ArgumentNullException(nameof(agents));
        }
        var plans = agents.Where(a => a is not null).Distinct().Select(a => (Agent: a, Plan: a.StopLocal())).ToList();
        var results = await Task.WhenAll(plans.Select(p => p.Agent.TellServerAsync(p.Plan, cancellationToken))).ConfigureAwait(false);
        return results.Aggregate(new StopAllResult(), (sum, next) => sum.Plus(next));
    }

    // The server part: reads the grants the copy did not know (best effort, never long), ends each on the server, once more when it
    // fails. A server that does not answer never holds the stop up beyond StopServerWait for each try.
    private async Task<StopAllResult> TellServerAsync(StopPlan plan, CancellationToken cancellationToken)
    {
        var toTell = plan.Grants;
        var offSwitch = TurnedOff;
        if (!offSwitch && Grants.Available && Connection == AgentConnection.Governed)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(StopServerWait);
                await Grants.RefreshAsync(wait.Token).ConfigureAwait(false);
                toTell.AddRange(Grants.EndAllHere()); // the ones the copy had not known: ended here too
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException or OperationCanceledException or InvalidOperationException
                && !cancellationToken.IsCancellationRequested)
            {
                // the copy is what there is
            }
        }
        var ended = 0;
        var notTold = 0;
        foreach (var grant in toTell)
        {
            ended++;
            if (offSwitch || TurnedOff)
            {
                continue; // the server ended it with the switch (revoked_by agent_turned_off)
            }
            if (!await TryRevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false))
            {
                notTold++;
            }
        }
        if (ended > 0)
        {
            Log.StoppedEverything(Logger, Slug, plan.Turns, ended, notTold);
        }
        return new StopAllResult
        {
            TurnsStopped = plan.Turns,
            GrantsEnded = ended + plan.ChatApprovals,
            ChatApprovalsEnded = plan.ChatApprovals,
            GrantsNotTold = notTold,
        };
    }

    // Tells the server a grant ended; once more if that failed. True: told (or the server already had it ended, or the agent is off).
    private async Task<bool> TryRevokeAsync(string grantId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(StopServerWait);
                await Grants.RevokeAsync(grantId, wait.Token).ConfigureAwait(false);
                return true;
            }
            catch (FadenstackApiException e) when (e.IsAgentDisabled)
            {
                return true; // off: the server ended it; no retry for this answer
            }
            catch (FadenstackApiException e) when ((int)e.Status == 404)
            {
                return true; // it ended already
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException or OperationCanceledException or InvalidOperationException
                && !cancellationToken.IsCancellationRequested)
            {
                // unreachable, refused, or too slow: once more
            }
        }
        return false;
    }
}

using System.Net;
using Fadenstack.Models;

namespace Fadenstack.Agents;

/// <summary>What the device says about its screen: locked, unlocked, or that it cannot tell.</summary>
public enum ScreenLockState
{
    /// <summary>Nothing could be read: a grant is never paused on it, and one the lock paused is not resumed on it.</summary>
    Unknown,

    Unlocked,

    /// <summary>The screen is locked (or its screen saver runs): the person is away.</summary>
    Locked,
}

/// <summary>Why a grant ended, as <see cref="GrantLifecycle"/> says it.</summary>
public enum GrantEndReason
{
    /// <summary>Its time was up.</summary>
    Time,

    /// <summary>The person pressed Stop (here, or the server says the person revoked it).</summary>
    Stopped,

    /// <summary>It left the server's list and the server cannot say why: Settings, another app, an administrator, or its calls.</summary>
    Elsewhere,

    /// <summary>It used up its calls.</summary>
    Spent,

    /// <summary>The screen stayed locked for <see cref="GrantLifecycle.LockedFor"/>.</summary>
    Locked,

    /// <summary>It stayed paused for <see cref="GrantLifecycle.PausedLimit"/> while the app was closed.</summary>
    Away,

    /// <summary>An administrator ended it (the server's word).</summary>
    Admin,

    /// <summary>The organisation turned the feature off (the server's kill switch).</summary>
    KillSwitch,

    /// <summary>The organisation narrowed who may use it (the server's limit).</summary>
    Limit,

    /// <summary>The app ended it (<see cref="GrantLifecycle.EndAsync"/>): a setting of its own was turned off.</summary>
    Host,
}

/// <summary>A grant of this device ended, and why.</summary>
public sealed class GrantEndedEventArgs : EventArgs
{
    internal GrantEndedEventArgs(AgentGrant grant, GrantEndReason reason, DateTimeOffset endedAt, TimeSpan ran)
    {
        Grant = grant;
        Reason = reason;
        EndedAt = endedAt;
        Ran = ran;
    }

    /// <summary>The grant as the copy last held it.</summary>
    public AgentGrant Grant { get; }

    public GrantEndReason Reason { get; }

    /// <summary>When it ended by the server's clock: its time for <see cref="GrantEndReason.Time"/>, when it was paused for <see cref="GrantEndReason.Away"/>.</summary>
    public DateTimeOffset EndedAt { get; }

    /// <summary>How long it ran, from when it was made to <see cref="EndedAt"/>.</summary>
    public TimeSpan Ran { get; }

    /// <summary>How many calls ran under it.</summary>
    public int CallsUsed => Grant.CallsUsed;
}

/// <summary>A running grant is near its end: the app may offer another <see cref="GrantLifecycle.KeepGoingBy"/>.</summary>
public sealed class GrantEndingSoonEventArgs : EventArgs
{
    internal GrantEndingSoonEventArgs(AgentGrant grant, TimeSpan left)
    {
        Grant = grant;
        Left = left;
    }

    public AgentGrant Grant { get; }

    /// <summary>The time left by the server's clock.</summary>
    public TimeSpan Left { get; }
}

/// <summary>How one grant fared when the app started again (<see cref="GrantLifecycle.AppStartedAsync"/>).</summary>
public enum GrantResumeOutcome
{
    /// <summary>It was paused less than <see cref="GrantLifecycle.PausedLimit"/> and runs again.</summary>
    Resumed,

    /// <summary>It was paused too long and was ended (<see cref="GrantEndReason.Away"/>).</summary>
    EndedAway,

    /// <summary>The server ended it as it refused the resume (it is no longer allowed); <see cref="GrantEndedEventArgs"/> says why.</summary>
    Refused,

    /// <summary>The server could not check: it stays paused and is tried again at each tick.</summary>
    Unavailable,

    /// <summary>The server could not be reached: it stays paused and is tried again at each tick.</summary>
    Offline,

    /// <summary>The screen is locked: it resumes with the unlock.</summary>
    WaitsForUnlock,

    /// <summary>The server had ended it already.</summary>
    Gone,
}

/// <summary>What <see cref="GrantLifecycle.StopAsync"/> did.</summary>
/// <param name="Grants">How many grants it tried to end.</param>
/// <param name="Ended">How many it ended (one the server had ended already counts).</param>
public readonly record struct GrantStopResult(int Grants, int Ended)
{
    /// <summary>Every grant ended; otherwise those left run on at the server until their time.</summary>
    public bool Complete => Ended >= Grants;
}

/// <summary>The settings of a <see cref="GrantLifecycle"/>.</summary>
public sealed class GrantLifecycleOptions
{
    /// <summary>
    /// The grants the lifecycle looks after. By default a stretch of work and Bypass; a grant for a time of one tool is
    /// not "working alongside", and a later local auto-approve is added here.
    /// </summary>
    public Func<AgentGrant, bool>? Manages { get; init; }

    /// <summary>
    /// Of those, the grants the lock pauses and, locked for <see cref="GrantLifecycle.LockedFor"/>, ends. By default a
    /// stretch of work; Bypass is paused by the app's end only.
    /// </summary>
    public Func<AgentGrant, bool>? PausedByLock { get; init; }

    /// <summary>
    /// How often the lifecycle looks by itself (<see cref="GrantLifecycle.TickEvery"/> by default); <see cref="TimeSpan.Zero"/>:
    /// never, and the host calls <see cref="GrantLifecycle.TickAsync"/>.
    /// </summary>
    public TimeSpan? TickEvery { get; init; }
}

/// <summary>
/// The rules that keep a server-held grant (a stretch of work, Bypass) from running on unattended (sdk/46 G2; desktop
/// 37 D5, sdk/40 A3.5), for every host to share. The host feeds it what only it knows -- the app ends, the app starts
/// again, the screen locks or unlocks -- and it pauses, resumes and ends the grants of this device for the agent by
/// the server's clock (<see cref="AgentGrants.ServerNow"/>), and says when one is near its end and when one ended and why:
/// <list type="bullet">
/// <item><b>The app's end</b> (<see cref="AppEndingAsync"/>): every running grant is paused at the server. Its time runs on.</item>
/// <item><b>The next start</b> (<see cref="AppStartedAsync"/>): a grant paused less than <see cref="PausedLimit"/> resumes, with no
/// card and no second sign-in; one paused longer ends (<see cref="GrantEndReason.Away"/>). One running is left running.</item>
/// <item><b>The lock</b> (<see cref="SetScreenLockAsync"/>): a stretch of work pauses while the screen is locked and resumes with
/// the unlock; locked for <see cref="LockedFor"/>, it ends (<see cref="GrantEndReason.Locked"/>). A state the host cannot
/// read (<see cref="ScreenLockState.Unknown"/>) never pauses, and after a lock it never resumes.</item>
/// <item><b>Stop</b> (<see cref="StopAsync"/>): every grant of this device that it looks after ends, a paused one too.</item>
/// <item><b>Near the end</b> (<see cref="EndingSoon"/>): once per end time, <see cref="EndingSoon"/> before it (not for Bypass,
/// which is never extended); <see cref="KeepGoingAsync"/> adds <see cref="KeepGoingBy"/>.</item>
/// <item><b>The end</b> (<see cref="Ended"/>): once per grant, with the reason this device knows, or the server's word.</item>
/// </list>
/// The server decides every call; this only pauses, resumes and ends. It needs the host to call <see cref="TickAsync"/> unless
/// it ticks by itself. The events come on any thread.
/// </summary>
public sealed class GrantLifecycle : IDisposable
{
    /// <summary>A grant paused by the app's end ends at the next start when it was paused this long (by the server's clock).</summary>
    public static readonly TimeSpan PausedLimit = AgentGrants.ResumeWithin;

    /// <summary>A grant paused by the lock ends after this long locked.</summary>
    public static readonly TimeSpan LockedFor = TimeSpan.FromMinutes(30);

    /// <summary>How long before the end <see cref="EndingSoon"/> is raised.</summary>
    public static readonly TimeSpan EndingSoonBefore = TimeSpan.FromMinutes(2);

    /// <summary>What <see cref="KeepGoingAsync"/> adds.</summary>
    public static readonly TimeSpan KeepGoingBy = TimeSpan.FromMinutes(30);

    /// <summary>How often the lifecycle looks while a grant runs, when it ticks by itself.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(15);

    /// <summary>How often the server's list is read again while a grant runs: one the server ended leaves the copy.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="AppEndingAsync"/> waits for the pauses by default.</summary>
    public static readonly TimeSpan QuitWithin = TimeSpan.FromSeconds(3);

    private sealed class Record
    {
        public Record(AgentGrant grant, DateTimeOffset seen) => (Last, FirstSeen) = (grant, seen);

        public AgentGrant Last;
        public readonly DateTimeOffset FirstSeen;
        public GrantEndReason? Ending;
        public DateTimeOffset? EndedAt;
        public bool PausedByLock;
        public DateTimeOffset? AskedFor;
    }

    private readonly AgentGrants _grants;
    private readonly Func<AgentGrant, bool> _manages;
    private readonly Func<AgentGrant, bool> _pausedByLock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retry = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer? _timer;
    private ScreenLockState _screen = ScreenLockState.Unknown;
    private bool _locked;
    private DateTimeOffset _lockedAt;
    private DateTimeOffset _refreshedAt;
    private bool _resolving;
    private bool _disposed;

    /// <param name="grants">The agent's grants (<see cref="FadenstackAgent.Grants"/>).</param>
    /// <param name="options">Which grants it looks after, and whether it ticks by itself; null: work and Bypass, every <see cref="TickEvery"/>.</param>
    public GrantLifecycle(AgentGrants grants, GrantLifecycleOptions? options = null)
    {
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _manages = options?.Manages ?? (g => g.Kind is GrantKinds.Work or GrantKinds.Bypass);
        _pausedByLock = options?.PausedByLock ?? (g => g.Kind == GrantKinds.Work);
        _refreshedAt = _grants.ServerNow;
        _grants.Changed += OnGrantsChanged;
        Look();
        var every = options?.TickEvery ?? TickEvery;
        if (every > TimeSpan.Zero)
        {
            _timer = new Timer(_ => _ = TickQuietlyAsync(), null, every, every);
        }
    }

    /// <summary>The grants this lifecycle looks at.</summary>
    public AgentGrants Grants => _grants;

    /// <summary>A grant of this device ended (once per grant), and why.</summary>
    public event EventHandler<GrantEndedEventArgs>? Ended;

    /// <summary>A running grant that can be extended is <see cref="EndingSoonBefore"/> from its end (once per end time).</summary>
    public event EventHandler<GrantEndingSoonEventArgs>? EndingSoon;

    /// <summary>The lifecycle looked again (a tick, a pause, a resume, a change of the copy): a marker brings its time left up to date.</summary>
    public event EventHandler? Changed;

    /// <summary>The last screen state the host gave.</summary>
    public ScreenLockState Screen
    {
        get
        {
            lock (_gate)
            {
                return _screen;
            }
        }
    }

    /// <summary>Whether the screen is locked (it stays so while the host cannot read it).</summary>
    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    /// <summary>The grants it looks after that are live (active or paused), the one ending first first.</summary>
    public IReadOnlyList<AgentGrant> Running => _grants.Live.Where(_manages).ToList();

    /// <summary>How many grants wait to resume or end at the next tick: the server could not check, or could not be reached.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _retry.Count;
            }
        }
    }

    // -- what the host feeds ---------------------------------------------------------------------------------------

    /// <summary>
    /// The app ends: every running grant it looks after is paused at the server, waiting at most <paramref name="within"/>
    /// (<see cref="QuitWithin"/> by default). Its time runs on; the next start resumes it (<see cref="AppStartedAsync"/>).
    /// Offline, or past the limit, a grant runs on at the server until its time with no agent of this app acting for it.
    /// Returns how many were paused. A host that must block calls it off its UI thread and waits.
    /// </summary>
    public async Task<int> AppEndingAsync(TimeSpan? within = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return 0;
        }
        var active = Running.Where(g => g.Status == "active").Select(g => g.Id).ToList();
        if (active.Count == 0)
        {
            return 0;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(within ?? QuitWithin);
        var paused = 0;
        foreach (var id in active)
        {
            try
            {
                await _grants.PauseAsync(id, limit.Token).ConfigureAwait(false);
                paused++;
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException or OperationCanceledException)
            {
                // offline, ended meanwhile, or out of time: it runs on at the server until its time
            }
        }
        return paused;
    }

    /// <summary>
    /// The app starts again, once signed in: reads the server's list, then each grant paused for less than
    /// <see cref="PausedLimit"/> by the server's clock resumes -- no card, no second sign-in -- and each paused longer ends
    /// (<see cref="GrantEndReason.Away"/>). The server may refuse a resume (403: no longer allowed, it ended it; <see cref="Ended"/>
    /// says why) or not be able to check (503) or be out of reach: the grant stays paused and is tried again at each tick.
    /// A grant found running (the app crashed before it paused it) runs on. With the screen locked, a paused grant resumes with
    /// the unlock. Returns how each paused grant fared.
    /// </summary>
    public async Task<IReadOnlyList<(AgentGrant Grant, GrantResumeOutcome Outcome)>> AppStartedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _grants.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            // offline: the copy is what the device knows
        }
        Look();
        var results = await ResolvePausedAsync(null, cancellationToken).ConfigureAwait(false);
        Look();
        return results;
    }

    /// <summary>
    /// The screen locked or unlocked. Locked, each grant that pauses on the lock pauses and ends after <see cref="LockedFor"/>
    /// locked; unlocked, what the lock paused resumes and what it ended and could not tell the server ends now.
    /// <see cref="ScreenLockState.Unknown"/> changes nothing: it never pauses, and after a lock it never resumes either (the
    /// end after <see cref="LockedFor"/> still holds). The returned task completes when the server has been told.
    /// </summary>
    public async Task SetScreenLockAsync(ScreenLockState state, CancellationToken cancellationToken = default)
    {
        bool locked;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _screen = state;
            if (state == ScreenLockState.Unknown)
            {
                return;
            }
            locked = state == ScreenLockState.Locked;
            if (locked == _locked)
            {
                return;
            }
            _locked = locked;
            if (locked)
            {
                _lockedAt = _grants.ServerNow;
            }
        }
        if (locked)
        {
            await PauseForLockAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ResumeAfterLockAsync(cancellationToken).ConfigureAwait(false);
        }
        Look();
    }

    /// <summary>
    /// Stop: ends every grant of this device that the lifecycle looks after, a paused one too, and says
    /// <see cref="GrantEndReason.Stopped"/> without asking the server. One the server ended meanwhile counts as ended. The host
    /// stops the running turns of their conversations itself. Never throws for an unreachable server: the result says how many
    /// ended, and those left run on at the server until their time.
    /// </summary>
    public async Task<GrantStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var all = Running;
        var now = _grants.ServerNow;
        lock (_gate)
        {
            foreach (var grant in all)
            {
                var record = Track(grant);
                record.Ending ??= GrantEndReason.Stopped;
                record.EndedAt ??= now;
            }
        }
        var ended = 0;
        foreach (var grant in all)
        {
            if (await TryRevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false))
            {
                ended++;
            }
            else
            {
                lock (_gate)
                {
                    if (_records.TryGetValue(grant.Id, out var record) && record.Ending == GrantEndReason.Stopped)
                    {
                        (record.Ending, record.EndedAt) = (null, null);
                    }
                }
            }
        }
        Look();
        return new GrantStopResult(all.Count, ended);
    }

    /// <summary>
    /// Ends one grant for a reason of the app's own (<see cref="GrantEndReason.Host"/>, say a setting turned off).
    /// An unreachable server is thrown, and nothing is said as ended.
    /// </summary>
    public async Task EndAsync(string grantId, GrantEndReason reason = GrantEndReason.Host, CancellationToken cancellationToken = default)
    {
        if (grantId is null)
        {
            throw new ArgumentNullException(nameof(grantId));
        }
        lock (_gate)
        {
            if (_grants.Find(grantId) is { } grant)
            {
                var record = Track(grant);
                record.Ending ??= reason;
                record.EndedAt ??= _grants.ServerNow;
            }
        }
        try
        {
            await _grants.RevokeAsync(grantId, cancellationToken).ConfigureAwait(false);
        }
        catch (FadenstackApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // ended meanwhile: the copy drops it
            await RefreshQuietlyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_records.TryGetValue(grantId, out var record) && record.Ending == reason)
                {
                    (record.Ending, record.EndedAt) = (null, null);
                }
            }
            throw;
        }
        Look();
    }

    /// <summary>
    /// Keeps <paramref name="grantId"/> going for another <see cref="KeepGoingBy"/>, the same bounds (the notice near the end).
    /// The server cuts it at the longest grant from its start (<see cref="AgentGrant.Narrowed"/>) and past that refuses
    /// (<c>grant_at_longest</c>, thrown); a Bypass grant is never extended (<see cref="InvalidOperationException"/>).
    /// </summary>
    public async Task<AgentGrant> KeepGoingAsync(string grantId, string? shownText = null, CancellationToken cancellationToken = default)
    {
        var grant = await _grants.ExtendAsync(grantId, KeepGoingBy, shownText: shownText, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (_records.TryGetValue(grantId, out var record))
            {
                record.Last = grant;
                record.AskedFor = null;
            }
        }
        Look();
        return grant;
    }

    /// <summary>
    /// One look: the server's list once in <see cref="RefreshEvery"/> while a grant runs, a resume the server could not take
    /// is tried again, the end after <see cref="LockedFor"/> locked, the grants that ended, the ones near their end.
    /// The lifecycle calls this by itself every <see cref="TickEvery"/> unless the host set it not to.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        if (Running.Count > 0 && _grants.ServerNow - _refreshedAt >= RefreshEvery)
        {
            _refreshedAt = _grants.ServerNow;
            await RefreshQuietlyAsync(cancellationToken).ConfigureAwait(false);
        }
        string[] retry;
        lock (_gate)
        {
            retry = _retry.ToArray();
        }
        if (retry.Length > 0)
        {
            await ResolvePausedAsync(new HashSet<string>(retry, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }
        bool endLocked;
        lock (_gate)
        {
            endLocked = _locked && _grants.ServerNow - _lockedAt >= LockedFor;
        }
        if (endLocked)
        {
            await EndAfterLockAsync(cancellationToken).ConfigureAwait(false);
        }
        Look();
    }

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
        _stop.Cancel();
        _grants.Changed -= OnGrantsChanged;
        _stop.Dispose();
    }

    // -- the copy ----------------------------------------------------------------------------------------------------

    private void OnGrantsChanged(object? sender, EventArgs e) => Look();

    // Called with the gate held: the record of a grant, made when the lifecycle first sees it.
    private Record Track(AgentGrant grant)
    {
        if (!_records.TryGetValue(grant.Id, out var record))
        {
            record = new Record(grant, _grants.ServerNow);
            _records[grant.Id] = record;
        }
        else
        {
            record.Last = grant;
        }
        return record;
    }

    // The copy moved, or a tick: new grants are followed, gone ones end, the ones near their end say so.
    private void Look()
    {
        if (_disposed)
        {
            return;
        }
        var live = _grants.Live.Where(_manages).ToList();
        var gone = new List<Record>();
        var soon = new List<GrantEndingSoonEventArgs>();
        lock (_gate)
        {
            foreach (var grant in live)
            {
                if (_records.ContainsKey(grant.Id) || !_ended.Contains(grant.Id))
                {
                    Track(grant);
                }
            }
            foreach (var record in _records.Values.Where(r => live.All(g => g.Id != r.Last.Id)).ToList())
            {
                _records.Remove(record.Last.Id);
                _ended.Add(record.Last.Id);
                _retry.Remove(record.Last.Id);
                gone.Add(record);
            }
            foreach (var record in _records.Values)
            {
                var left = _grants.Remaining(record.Last);
                if (record.Last.Kind != GrantKinds.Bypass && record.Last.Status == "active" && left > TimeSpan.Zero && left <= EndingSoonBefore)
                {
                    if (record.AskedFor != record.Last.ExpiresAt)
                    {
                        record.AskedFor = record.Last.ExpiresAt;
                        soon.Add(new GrantEndingSoonEventArgs(record.Last, left));
                    }
                }
                else if (record.AskedFor is not null && left > EndingSoonBefore)
                {
                    record.AskedFor = null;
                }
            }
        }
        foreach (var record in gone)
        {
            Conclude(record);
        }
        foreach (var args in soon)
        {
            EndingSoon?.Invoke(this, args);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // A grant left the copy: the reason this device knows (its own end, its calls, its time), else the server's word.
    private void Conclude(Record record)
    {
        var last = record.Last;
        var known = record.Ending ?? (last.MaxCalls is { } most && last.CallsUsed >= most ? GrantEndReason.Spent
            : _grants.ServerNow >= last.ExpiresAt ? GrantEndReason.Time
            : (GrantEndReason?)null);
        var endedAt = record.EndedAt ?? (known == GrantEndReason.Time ? last.ExpiresAt : _grants.ServerNow);
        var started = last.IssuedAt ?? record.FirstSeen;
        var ran = endedAt > started ? endedAt - started : TimeSpan.Zero;
        if (known is { } reason)
        {
            Ended?.Invoke(this, new GrantEndedEventArgs(last, reason, endedAt, ran));
        }
        else
        {
            _ = EndedByServerAsync(last, endedAt, ran);
        }
    }

    // The server ended it: its word says who (the person elsewhere, an administrator, the organisation, its time, its calls);
    // offline or unknown, the grant says the server ended it.
    private async Task EndedByServerAsync(AgentGrant last, DateTimeOffset endedAt, TimeSpan ran)
    {
        string? why = null;
        try
        {
            why = await _grants.WhyEndedAsync(last.Id, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            // the server cannot say
        }
        if (_disposed)
        {
            return;
        }
        Ended?.Invoke(this, new GrantEndedEventArgs(last, why switch
        {
            "person" => GrantEndReason.Stopped,
            "admin" => GrantEndReason.Admin,
            "kill_switch" => GrantEndReason.KillSwitch,
            "limit" => GrantEndReason.Limit,
            "expired" => GrantEndReason.Time,
            "spent" => GrantEndReason.Spent,
            _ => GrantEndReason.Elsewhere,
        }, endedAt, ran));
    }

    private async Task RefreshQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _grants.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException or OperationCanceledException)
        {
            // offline: the copy stays; its times still end it
        }
    }

    private async Task TickQuietlyAsync()
    {
        try
        {
            await TickAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // the next tick looks again
        }
    }

    // Revokes a grant; true when it is ended (one the server ended already counts), false when the server could not be told.
    private async Task<bool> TryRevokeAsync(string grantId, CancellationToken cancellationToken)
    {
        try
        {
            await _grants.RevokeAsync(grantId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FadenstackApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            return false; // offline: it runs on at the server until its time
        }
    }

    // -- the next start --------------------------------------------------------------------------------------------

    // The paused grants (only those in `only` when given: the tick's retry), one at a time.
    private async Task<IReadOnlyList<(AgentGrant, GrantResumeOutcome)>> ResolvePausedAsync(ISet<string>? only, CancellationToken cancellationToken)
    {
        var results = new List<(AgentGrant, GrantResumeOutcome)>();
        lock (_gate)
        {
            if (_resolving || _disposed)
            {
                return results;
            }
            _resolving = true;
        }
        var refused = false;
        try
        {
            foreach (var grant in Running.Where(g => g.Status == "paused" && (only is null || only.Contains(g.Id))))
            {
                results.Add((grant, await ResolveAsync(grant, cancellationToken).ConfigureAwait(false)));
                refused |= results[results.Count - 1].Item2 == GrantResumeOutcome.Refused;
            }
        }
        finally
        {
            lock (_gate)
            {
                _resolving = false;
            }
        }
        if (refused)
        {
            await RefreshQuietlyAsync(cancellationToken).ConfigureAwait(false); // the grants the server ended leave the copy
        }
        return results;
    }

    private async Task<GrantResumeOutcome> ResolveAsync(AgentGrant grant, CancellationToken cancellationToken)
    {
        var pausedAt = grant.PausedAt ?? _grants.ServerNow;
        bool awaitingUnlock;
        lock (_gate)
        {
            _retry.Remove(grant.Id);
            var record = Track(grant);
            if (_grants.ServerNow - pausedAt >= PausedLimit)
            {
                (record.Ending, record.EndedAt) = (GrantEndReason.Away, pausedAt);
                awaitingUnlock = false;
            }
            else
            {
                awaitingUnlock = _locked;
                record.PausedByLock |= awaitingUnlock;
            }
        }
        if (_grants.ServerNow - pausedAt >= PausedLimit)
        {
            if (await TryRevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false))
            {
                return GrantResumeOutcome.EndedAway;
            }
            lock (_gate)
            {
                _retry.Add(grant.Id); // ends at the next tick, or at its time
            }
            return GrantResumeOutcome.Offline;
        }
        if (awaitingUnlock)
        {
            return GrantResumeOutcome.WaitsForUnlock;
        }
        try
        {
            await _grants.ResumeAsync(grant.Id, cancellationToken).ConfigureAwait(false);
            return GrantResumeOutcome.Resumed;
        }
        catch (FadenstackApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return GrantResumeOutcome.Gone; // ended elsewhere meanwhile: the list drops it and Ended says why
        }
        catch (FadenstackApiException e) when (e.Status == HttpStatusCode.Forbidden)
        {
            return GrantResumeOutcome.Refused; // no longer allowed: the server ended it
        }
        catch (FadenstackApiException e) when (e.Status == HttpStatusCode.ServiceUnavailable)
        {
            lock (_gate)
            {
                _retry.Add(grant.Id);
            }
            return GrantResumeOutcome.Unavailable;
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            lock (_gate)
            {
                _retry.Add(grant.Id);
            }
            return GrantResumeOutcome.Offline;
        }
    }

    // -- the lock ----------------------------------------------------------------------------------------------------

    private async Task PauseForLockAsync(CancellationToken cancellationToken)
    {
        foreach (var grant in Running.Where(g => g.Status == "active" && _pausedByLock(g)))
        {
            try
            {
                await _grants.PauseAsync(grant.Id, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    Track(grant).PausedByLock = true;
                }
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException)
            {
                // ended meanwhile, or offline: the server's clock ends it anyway
            }
        }
    }

    // Back: what the lock paused resumes; what the lock ended (and could not tell the server then) ends now.
    private async Task ResumeAfterLockAsync(CancellationToken cancellationToken)
    {
        List<(string Id, bool End)> work;
        lock (_gate)
        {
            work = _records.Values.Where(r => r.PausedByLock || r.Ending is GrantEndReason.Locked or GrantEndReason.Away)
                .Select(r => (r.Last.Id, r.Ending is GrantEndReason.Locked or GrantEndReason.Away)).ToList();
        }
        foreach (var (id, end) in work)
        {
            try
            {
                if (end)
                {
                    await _grants.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _grants.ResumeAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException)
            {
                // ended meanwhile (404): the copy drops it; offline: it stays paused until its time
            }
            lock (_gate)
            {
                if (_records.TryGetValue(id, out var record) && !end)
                {
                    record.PausedByLock = false;
                }
            }
        }
    }

    private async Task EndAfterLockAsync(CancellationToken cancellationToken)
    {
        var now = _grants.ServerNow;
        foreach (var grant in Running.Where(_pausedByLock))
        {
            lock (_gate)
            {
                var record = Track(grant);
                record.Ending ??= GrantEndReason.Locked;
                record.EndedAt ??= now;
            }
            await TryRevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false); // offline: ends at the unlock, or at its time
        }
    }
}

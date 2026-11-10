using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Agents;

/// <summary>
/// The device's copy of its grants for the agent (sdk/38 §6): what the person let it run for a while without asking.
/// The server holds them, decides each call and counts it; this copy is the second line -- a call runs under a grant
/// only when the gateway said it is covered (<c>faden.grant_used</c>) and the copy holds that grant, live. The copy is
/// replaced by what the server answers: each grant made, extended or ended here, and the list a stream starts with
/// (<c>faden.grants</c>). Times are the server's (<see cref="ServerNow"/>).
/// </summary>
public sealed class AgentGrants
{
    /// <summary>What a grant card offers, at most up to the agent's longest grant (38 §7).</summary>
    public static readonly IReadOnlyList<TimeSpan> Durations =
        new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2) };

    /// <summary>What a grant card starts at (desktop 37 D2).</summary>
    public static readonly TimeSpan Preselected = TimeSpan.FromMinutes(30);

    private readonly FadenstackAgent _agent;
    private readonly object _gate = new();
    private List<AgentGrant> _grants = new();
    // Every grant the copy saw, as it last saw it, kept when a new list replaces the copy: why one ended (WhyEndedAsync).
    private readonly Dictionary<string, AgentGrant> _seen = new(StringComparer.Ordinal);
    private TimeSpan _offset;

    internal AgentGrants(FadenstackAgent agent) => _agent = agent;

    /// <summary>Raised when the copy changed: a grant made, extended, paused, ended, or a new list from the server.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether this agent can be given grants: the server holds them (<c>approval_scopes.grant</c>) and says what the
    /// agent allows. Nothing here asks the server otherwise.
    /// </summary>
    public bool Available => Settings is not null;

    /// <summary>What the agent allows (the profile's <c>approvals</c>); null without grants on the server.</summary>
    public ApprovalSettings? Settings =>
        _agent.ProfileOrNull is { } p && p.Features.Contains("approval_scopes.grant") ? p.Approvals : null;

    /// <summary>Whether the agent offers the standing approval <paramref name="kind"/>: <c>time</c> or <c>grant</c>.</summary>
    public bool Offers(string kind) => Settings?.Allows(kind) == true;

    /// <summary>The durations a card may offer for this agent: those within its longest grant.</summary>
    public IReadOnlyList<TimeSpan> Choices =>
        Settings is { } s ? Durations.Where(d => d.TotalMinutes <= s.LongestGrantMinutes).ToList() : Array.Empty<TimeSpan>();

    /// <summary>
    /// The durations a Bypass card may offer: those within its longest (the profile's <c>approvals.bypass</c>); none where
    /// the server does not offer Bypass to this agent.
    /// </summary>
    public IReadOnlyList<TimeSpan> BypassChoices =>
        Settings?.Bypass is { Allowed: true } b ? Durations.Where(d => d.TotalMinutes <= b.LongestMinutes).ToList() : Array.Empty<TimeSpan>();

    /// <summary>The client this SDK names itself as in a grant's record (L-59).</summary>
    internal static readonly string ClientVersion = "Fadenstack.Agents/" + (typeof(AgentGrants).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0");

    /// <summary>The server's clock now, from the offset its last answer showed.</summary>
    public DateTimeOffset ServerNow => _agent.Clock() + _offset;

    /// <summary>The grants that may still cover a call, the one ending first first.</summary>
    public IReadOnlyList<AgentGrant> Live
    {
        get
        {
            var now = ServerNow;
            lock (_gate)
            {
                return _grants.Where(g => g.IsLive && g.ExpiresAt > now && !Spent(g)).OrderBy(g => g.ExpiresAt).ToList();
            }
        }
    }

    /// <summary>The time a grant has left by the server's clock; zero once it ended.</summary>
    public TimeSpan Remaining(AgentGrant grant)
    {
        var left = grant.ExpiresAt - ServerNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Lets the agent run <paramref name="tool"/> for <paramref name="duration"/> without asking, here (and, with
    /// <paramref name="conversationId"/>, only in that conversation). The server's answer is the grant, perhaps shorter.
    /// </summary>
    public async Task<AgentGrant> GrantForTimeAsync(string tool, TimeSpan duration, string? conversationId = null,
        string? shownText = null, CancellationToken cancellationToken = default)
    {
        var grant = await _agent.Client.Agent!.CreateGrantAsync(new GrantRequest
        {
            Kind = GrantKinds.Time, Tools = new List<string> { tool }, Minutes = Minutes(duration),
            Conversation = conversationId is not null, ShownTextSha256 = Hash(shownText),
        }, conversationId, cancellationToken).ConfigureAwait(false);
        return Keep(grant);
    }

    /// <summary>
    /// Lets the agent read and write in <paramref name="places"/> for <paramref name="duration"/> in the conversation
    /// <paramref name="conversationId"/>, without asking -- never for anything destructive, unclassified, or an admin's
    /// rule. The classes are the agent's (<see cref="ApprovalSettings.GrantClasses"/>).
    /// </summary>
    public async Task<AgentGrant> GrantWorkAsync(IReadOnlyList<GrantPlace> places, TimeSpan duration, string conversationId,
        string? shownText = null, CancellationToken cancellationToken = default)
    {
        var grant = await _agent.Client.Agent!.CreateGrantAsync(new GrantRequest
        {
            Kind = GrantKinds.Work, Classes = new List<string>(Settings?.GrantClasses ?? new List<string> { "read", "write" }),
            Targets = Targets(places), Minutes = Minutes(duration), ShownTextSha256 = Hash(shownText),
        }, conversationId, cancellationToken).ConfigureAwait(false);
        return Keep(grant);
    }

    /// <summary>
    /// Switches approvals off in the conversation <paramref name="conversationId"/> for <paramref name="duration"/>
    /// (sdk/09 §3.12): what its mode would ask about runs, destructive and unclassified calls too -- never an admin's rule
    /// or a tool that asks every time. The server's answer is the record (L-59): Bypass holds only once it answered, and
    /// a refusal is thrown (403 <c>bypass_not_allowed</c>). <paramref name="reason"/> is the person's own words, if any.
    /// </summary>
    /// <remarks>
    /// The server makes the grant only with the proof of a fresh sign-in (sdk/41; ASVS 5.0 7.5.3): the one this device
    /// holds while it lasts, else a second sign-in in the browser through the app's <see cref="IStepUpHandler"/>, which
    /// the person approves there. Without a handler, or when the sign-in does not happen, no grant:
    /// <see cref="BypassSignInException"/>. A held proof the server no longer takes is dropped and the sign-in asked
    /// once more.
    /// </remarks>
    public async Task<AgentGrant> GrantBypassAsync(TimeSpan duration, string conversationId, string? reason = null,
        string? shownText = null, CancellationToken cancellationToken = default)
    {
        var handler = _agent.Settings.StepUp
            ?? throw new BypassSignInException(StepUpStage.Failed, "The app opens no second sign-in, which Bypass needs.");
        var request = new GrantRequest
        {
            Kind = GrantKinds.Bypass, Minutes = Minutes(duration), ShownTextSha256 = Hash(shownText),
            Reason = GrantAnswer.Cut(reason), ClientVersion = ClientVersion,
        };
        var longest = Settings?.Bypass?.LongestMinutes;
        for (var attempt = 0; ; attempt++)
        {
            var proof = await _agent.StepUps.ObtainAsync(handler, conversationId, longest, cancellationToken).ConfigureAwait(false);
            try
            {
                var grant = await _agent.Client.Agent!.CreateGrantAsync(request, conversationId, proof, cancellationToken)
                    .ConfigureAwait(false);
                return Keep(grant);
            }
            catch (FadenstackAuthenticationException e) when (e.Code == StepUpRequired && attempt == 0)
            {
                _agent.StepUps.Forget(); // too old by the server's clock, or not this device's: sign in again
            }
        }
    }

    /// <summary>The server's word that the grant needs a fresh sign-in (RFC 9470's challenge, sdk/41).</summary>
    internal const string StepUpRequired = "bypass_step_up_required";

    /// <summary>The live, active Bypass grant of the conversation <paramref name="conversationId"/>; null when it has none.</summary>
    public AgentGrant? BypassFor(string conversationId) =>
        Live.FirstOrDefault(g => g.Kind == GrantKinds.Bypass && g.Status == "active" && g.ConversationId == conversationId);

    /// <summary>The calls the device ran under a Bypass grant in the turn that just ended, for the gateway to compare (L-61).</summary>
    internal Task<GrantReport> ReportAsync(string grantId, IReadOnlyList<string> calls, CancellationToken cancellationToken) =>
        _agent.Client.Agent!.ReportGrantAsync(grantId, calls, cancellationToken);

    /// <summary>The copy's grant <paramref name="grantId"/>, live or not; null when it does not know it.</summary>
    internal AgentGrant? Find(string grantId)
    {
        lock (_gate)
        {
            return _grants.FirstOrDefault(g => g.Id == grantId);
        }
    }

    /// <summary>
    /// Why a grant this copy held has ended, for the app to say (sdk/09 L-59, L-63): <c>person</c> (ended here, in
    /// Settings or on another sign-in of theirs), <c>admin</c>, <c>kill_switch</c> (Bypass turned off for the
    /// organisation), <c>limit</c> (a narrower limit or list of the organisation's), <c>agent</c>, <c>expired</c> or
    /// <c>spent</c>; null while it runs, or when not even the server can say (an older server, offline). What the copy
    /// recorded comes first -- a Stop here answers at once -- then its time by the server's clock, then the server.
    /// </summary>
    public async Task<string?> WhyEndedAsync(string grantId, CancellationToken cancellationToken = default)
    {
        AgentGrant? seen;
        lock (_gate)
        {
            _seen.TryGetValue(grantId, out seen);
        }
        if (Ended(seen) is { } known)
        {
            return known;
        }
        try
        {
            var found = await _agent.Client.Agent!.GetGrantAsync(grantId, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _seen[found.Id] = found;
            }
            return Ended(found);
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null; // the server cannot say: the app says the server ended it
        }
    }

    // Why a grant as last seen has ended; null while it runs.
    private string? Ended(AgentGrant? grant)
    {
        if (grant is null)
        {
            return null;
        }
        if (grant.RevokedBy is { Length: > 0 } by)
        {
            return by;
        }
        return grant.Status switch
        {
            "expired" => "expired",
            "spent" => "spent",
            _ when Spent(grant) => "spent",
            _ when grant.ExpiresAt <= ServerNow => "expired",
            _ => null,
        };
    }

    /// <summary>
    /// Extends a running grant in place: <paramref name="more"/> time and/or <paramref name="places"/>. The server never
    /// lets a grant run past the agent's longest grant from when it was made (<see cref="AgentGrant.Narrowed"/> says it
    /// cut the time); past that it refuses, and a new grant needs the full card. A Bypass grant is not extended (L-58): the
    /// copy's own is refused here, unsent, and the server refuses one the copy does not know (<c>bypass_not_extendable</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="grantId"/> is a Bypass grant.</exception>
    public async Task<AgentGrant> ExtendAsync(string grantId, TimeSpan more, IReadOnlyList<GrantPlace>? places = null,
        string? shownText = null, CancellationToken cancellationToken = default)
    {
        if (Find(grantId) is { Kind: GrantKinds.Bypass })
        {
            throw new InvalidOperationException("A Bypass grant is not extended: more time is a new Bypass, confirmed again.");
        }
        var grant = await _agent.Client.Agent!.ExtendGrantAsync(grantId, new GrantExtension
        {
            Minutes = more <= TimeSpan.Zero ? 0 : Minutes(more), Targets = Targets(places ?? Array.Empty<GrantPlace>()),
            ShownTextSha256 = Hash(shownText),
        }, cancellationToken).ConfigureAwait(false);
        return Keep(grant);
    }

    /// <summary>Ends a grant from the next call on; a call already running finishes.</summary>
    public async Task RevokeAsync(string grantId, CancellationToken cancellationToken = default) =>
        Keep(await _agent.Client.Agent!.RevokeGrantAsync(grantId, cancellationToken).ConfigureAwait(false));

    /// <summary>Ends every grant this device holds for the agent: the marker's Stop.</summary>
    public async Task RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var grant in Live)
        {
            await RevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pauses a grant while the person is away (the screen locked); its time runs on.</summary>
    public async Task PauseAsync(string grantId, CancellationToken cancellationToken = default) =>
        Keep(await _agent.Client.Agent!.PauseGrantAsync(grantId, cancellationToken).ConfigureAwait(false));

    /// <summary>Resumes a paused grant, unless it ended meanwhile.</summary>
    public async Task ResumeAsync(string grantId, CancellationToken cancellationToken = default) =>
        Keep(await _agent.Client.Agent!.ResumeGrantAsync(grantId, cancellationToken).ConfigureAwait(false));

    /// <summary>How long a Bypass paused at the app's end may wait for the next start to resume it (D5, as a work grant).</summary>
    public static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The app's end (owner, 2026-11-09): pauses this device's running Bypass grants for the agent, as a work grant pauses.
    /// The clock runs on; the next start resumes them (<see cref="ResumeBypassAsync"/>). Offline, they run on at the server
    /// until their time. Returns those paused.
    /// </summary>
    public async Task<IReadOnlyList<AgentGrant>> PauseBypassAsync(CancellationToken cancellationToken = default)
    {
        var paused = new List<AgentGrant>();
        foreach (var grant in Live.Where(g => g.Kind == GrantKinds.Bypass && g.Status == "active").ToList())
        {
            await PauseAsync(grant.Id, cancellationToken).ConfigureAwait(false);
            paused.Add(grant);
        }
        return paused;
    }

    /// <summary>
    /// The next start: resumes each Bypass grant of this device paused less than <see cref="ResumeWithin"/> ago by the
    /// server's clock -- no card, no second sign-in; the server checks that Bypass is still allowed -- and ends each paused
    /// longer. Returns those that did not resume, with why (<see cref="WhyEndedAsync"/>'s words, or <c>paused_too_long</c>,
    /// <c>ended</c> when the server ended one after the list was read and cannot say why, or <c>unavailable</c> when the
    /// server could not check: it stays paused).
    /// </summary>
    public async Task<IReadOnlyList<(AgentGrant Grant, string Why)>> ResumeBypassAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var ended = new List<(AgentGrant, string)>();
        foreach (var grant in Live.Where(g => g.Kind == GrantKinds.Bypass && g.Status == "paused").ToList())
        {
            if (grant.PausedAt is { } since && ServerNow - since >= ResumeWithin)
            {
                await RevokeAsync(grant.Id, cancellationToken).ConfigureAwait(false);
                ended.Add((grant, "paused_too_long"));
                continue;
            }
            try
            {
                await ResumeAsync(grant.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (FadenstackApiException e) when ((int)e.Status is 403 or 404)
            {
                // No longer allowed (403: the server ended it now), or ended since the list was read (404: the kill switch,
                // an administrator): the server says why.
                ended.Add((grant, await WhyEndedAsync(grant.Id, cancellationToken).ConfigureAwait(false)
                    ?? ((int)e.Status == 403 ? "limit" : "ended")));
            }
            catch (FadenstackApiException e) when ((int)e.Status == 503)
            {
                ended.Add((grant, "unavailable"));
            }
        }
        return ended;
    }

    /// <summary>Reads the device's grants from the server again; without grants on the server, does nothing.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return;
        }
        var list = await _agent.Client.Agent!.ListGrantsAsync(cancellationToken).ConfigureAwait(false);
        Replace(list.Data, list.ServerNow);
    }

    /// <summary>
    /// The second line (38 §6): whether the copy holds <paramref name="grantId"/>, active and unexpired, for this call --
    /// a grant for a time for this very tool, a stretch of work for its class and the <paramref name="place"/> its tool
    /// names, which must lie in one of the grant's places where it really leads on this device (<see cref="GrantPaths"/>).
    /// Never a destructive or unclassified call -- but for a Bypass grant, which covers every class (the caller checks
    /// that it is the conversation's and the turn runs in Bypass). <paramref name="leadsOutside"/> is the real place when
    /// a link, a junction or a mount point took it out of the grant.
    /// </summary>
    internal bool Covers(string grantId, string tool, ToolClass toolClass, GrantPlace? place, out string? leadsOutside)
    {
        leadsOutside = null;
        var grant = Live.FirstOrDefault(g => g.Id == grantId && g.Status == "active");
        if (grant?.Kind == GrantKinds.Bypass)
        {
            return true;
        }
        if (toolClass is not (ToolClass.Read or ToolClass.Write))
        {
            return false;
        }
        switch (grant?.Kind)
        {
            case GrantKinds.Time:
                return grant.Scope.Tools is { Count: 1 } tools && tools[0] == tool;
            case GrantKinds.Work:
                return place is not null && grant.Scope.Classes?.Contains(toolClass.ToWire()) == true
                    && InPlaces(grant, place, toolClass.ToWire(), out leadsOutside);
            default:
                return false;
        }
    }

    // In one of the grant's places of its type whose actions hold the class, as the gateway checks it; a path on this device
    // by where it really leads, the place's and the targets' (ASVS 5.0 5.3.2: the gateway sees only the text).
    private static bool InPlaces(AgentGrant grant, GrantPlace place, string toolClass, out string? leadsOutside)
    {
        leadsOutside = null;
        var targets = (grant.Scope.Targets ?? new List<GrantTarget>())
            .Where(t => t.Type == place.Type && (t.Actions is null || t.Actions.Contains(toolClass)))
            .SelectMany(t => t.Identifiers)
            .ToList();
        if (!GrantPaths.NamesPath(place.Type) || !GrantPaths.IsLocal(place.Identifier))
        {
            return targets.Any(t => GrantPaths.Named(place.Type, t, place.Identifier));
        }
        if (GrantPaths.Real(place.Identifier) is not { } real)
        {
            return false; // where it leads cannot be told: ask
        }
        if (targets.Any(t => GrantPaths.IsLocal(t) && GrantPaths.Real(t) is { } target && GrantPaths.Lies(place.Type, target, real)))
        {
            return true;
        }
        leadsOutside = real;
        return false;
    }

    /// <summary>Whether the copy knows <paramref name="grantId"/> at all.</summary>
    internal bool Knows(string grantId)
    {
        lock (_gate)
        {
            return _grants.Any(g => g.Id == grantId);
        }
    }

    /// <summary>A stream's <c>faden.grants</c>: the device's live grants, which replace the copy.</summary>
    internal void Replace(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object || !snapshot.TryGetProperty("grants", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var grants = JsonSerializer.Deserialize<List<AgentGrant>>(list.GetRawText(), Fadenstack.Http.FadenstackJson.Options) ?? new();
        DateTimeOffset? now = snapshot.TryGetProperty("serverNow", out var n) && n.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(n.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
        Replace(grants, now);
    }

    private void Replace(IEnumerable<AgentGrant> grants, DateTimeOffset? serverNow)
    {
        lock (_gate)
        {
            _grants = grants.ToList();
            foreach (var grant in _grants)
            {
                _seen[grant.Id] = grant;
            }
            Sync(serverNow);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AgentGrant Keep(AgentGrant grant)
    {
        lock (_gate)
        {
            _grants = _grants.Where(g => g.Id != grant.Id).Append(grant).ToList();
            _seen[grant.Id] = grant;
            Sync(grant.ServerNow);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return grant;
    }

    private void Sync(DateTimeOffset? serverNow)
    {
        if (serverNow is { } now)
        {
            _offset = now - _agent.Clock();
        }
    }

    private static bool Spent(AgentGrant grant) => grant.MaxCalls is { } max && grant.CallsUsed >= max;

    private static int Minutes(TimeSpan duration) => Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));

    private static List<GrantTarget> Targets(IReadOnlyList<GrantPlace> places) => places
        .GroupBy(p => p.Type, StringComparer.Ordinal)
        .Select(g => new GrantTarget { Type = g.Key, Identifiers = g.Select(p => p.Identifier).Distinct().ToList() })
        .ToList();

    /// <summary>The SHA-256 of what the card showed, as the server keeps it; null when the app shows no text.</summary>
    internal static string? Hash(string? text)
    {
        if (text is null)
        {
            return null;
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
    }
}

/// <summary>The value a JSON pointer (RFC 6901) names in a call's arguments, as text; null when there is none.</summary>
internal static class GrantPointer
{
    public static string? Resolve(IReadOnlyDictionary<string, object?> arguments, string pointer)
    {
        var tokens = pointer.Split('/').Skip(1).Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToList();
        if (tokens.Count == 0 || !arguments.TryGetValue(tokens[0], out var found) || found is null)
        {
            return null;
        }
        if (tokens.Count == 1 && found is string text)
        {
            return text;
        }
        JsonElement? element = found is JsonElement json ? json : JsonSerializer.SerializeToElement(found);
        foreach (var token in tokens.Skip(1))
        {
            if (element is not { } e)
            {
                return null;
            }
            element = e.ValueKind switch
            {
                JsonValueKind.Object when e.TryGetProperty(token, out var child) => child,
                JsonValueKind.Array when int.TryParse(token, out var i) && i >= 0 && i < e.GetArrayLength() => e[i],
                _ => null,
            };
        }
        return element is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }
}

/// <summary>
/// A place a stretch of work may cover, as the app names it: a document's path, a folder, a site's origin, an app, the
/// desk. <see cref="Label"/> is what the card shows (<c>Word "report.docx"</c>).
/// </summary>
public sealed record GrantPlace(string Type, string Identifier, string Label)
{
    public static GrantPlace Document(string path, string? label = null) => new("document", path, label ?? Path.GetFileName(path));

    public static GrantPlace Folder(string path, string? label = null) => new("folder", path, label ?? path);

    public static GrantPlace Site(string origin, string? label = null) => new("site", origin, label ?? origin);

    public static GrantPlace App(string id, string label) => new("app", id, label);

    public static GrantPlace Desk(string id, string label) => new("desk", id, label);
}

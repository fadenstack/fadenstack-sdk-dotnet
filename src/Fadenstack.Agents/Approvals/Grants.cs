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
    /// Extends a running grant in place: <paramref name="more"/> time and/or <paramref name="places"/>. The server never
    /// lets a grant run past the agent's longest grant from when it was made (<see cref="AgentGrant.Narrowed"/> says it
    /// cut the time); past that it refuses, and a new grant needs the full card.
    /// </summary>
    public async Task<AgentGrant> ExtendAsync(string grantId, TimeSpan more, IReadOnlyList<GrantPlace>? places = null,
        string? shownText = null, CancellationToken cancellationToken = default)
    {
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
    /// a grant for a time for this very tool, a stretch of work for its class and a tool that names its place. Never a
    /// destructive or unclassified call.
    /// </summary>
    internal bool Covers(string grantId, string tool, ToolClass toolClass, bool namesPlace)
    {
        if (toolClass is not (ToolClass.Read or ToolClass.Write))
        {
            return false;
        }
        var grant = Live.FirstOrDefault(g => g.Id == grantId && g.Status == "active");
        return grant?.Kind switch
        {
            GrantKinds.Time => grant.Scope.Tools is { Count: 1 } tools && tools[0] == tool,
            GrantKinds.Work => namesPlace && grant.Scope.Classes?.Contains(toolClass.ToWire()) == true,
            _ => false,
        };
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
            Sync(serverNow);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AgentGrant Keep(AgentGrant grant)
    {
        lock (_gate)
        {
            _grants = _grants.Where(g => g.Id != grant.Id).Append(grant).ToList();
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

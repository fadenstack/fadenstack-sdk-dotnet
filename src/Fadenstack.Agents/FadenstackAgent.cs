using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>
/// An agent served by a Fadenstack server and run on this device: its profile, its tools, and its sessions.
/// The loop runs here; every model call goes through the gateway.
/// </summary>
public sealed class FadenstackAgent : IDisposable
{
    private readonly AgentSettings _settings;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private ISessionStore? _sessions;
    private IApprovalStore? _approvalStore;
    private AgentProfile? _profile;
    private volatile IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private IReadOnlyList<AgentTool> _hostTools = Array.Empty<AgentTool>();
    private readonly List<(IToolSource Source, IReadOnlyList<AgentTool> Tools)> _sourceTools = new();
    private readonly HashSet<IToolSource> _changed = new();
    private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly List<(IChangingToolSource Source, EventHandler Handler)> _subscriptions = new();
    private AgentContext? _context;
    private bool _warned;
    private readonly Dictionary<string, DateTimeOffset> _timeGrants = new();
    private ILogger? _logger;
    private ReasoningChoiceStore? _reasoningStore;
    private ReasoningChoices _reasoningChoices = ReasoningChoices.Everything;
    private ReasoningLevel? _reasoningPick;

    internal FadenstackAgent(AgentSettings settings)
    {
        _settings = settings;
        Reporter = new EventReporter(settings.Client, settings.Report);
    }

    public static AgentBuilder CreateBuilder() => new();

    public string Slug => _settings.Agent;

    public FadenstackClient Client => _settings.Client;

    /// <summary>The app the agent runs in, as the host described it (<see cref="AgentBuilder.DescribeApp"/>); null when it did not.</summary>
    public AppDescription? App => _settings.AppDescribed ? _settings.App : null;

    /// <summary>The agent's profile, once <see cref="StartAsync"/> ran.</summary>
    public AgentProfile Profile => _profile ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>The tools the agent runs, with their effective classes, once started.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    /// <summary>
    /// The most any session may use: the profile's ceiling, if the server sets one, or Auto. A ceiling this SDK does
    /// not know narrows to read-only: it cannot be taken for none.
    /// </summary>
    public ToolMode Ceiling => _profile?.ToolModes.Ceiling is { } ceiling && !string.IsNullOrWhiteSpace(ceiling)
        ? ToolModes.ParseMode(ceiling) ?? ToolMode.ReadOnly
        : ToolMode.Auto;

    /// <summary>Whether pictures tools return go to the model: the app allows it and the profile says the model sees.</summary>
    public bool SeesPictures => _settings.Pictures && _profile?.Vision == true;

    public ToolMode DefaultToolMode => ToolModes.Narrowest(_settings.DefaultMode, ToolModes.ParseMode(_profile?.ToolModes.Default) ?? ToolMode.Ask);

    /// <summary><see cref="PermissionMode.Ask"/>, or <see cref="PermissionMode.Bypass"/> when the app set it.</summary>
    public PermissionMode PermissionMode => _settings.PermissionMode;

    internal AgentSettings Settings => _settings;

    internal EventReporter Reporter { get; }

    /// <summary>Where the agent and its sessions log (<see cref="AgentBuilder.UseLogging"/>); nowhere by default.</summary>
    internal ILogger Logger => _logger ??= _settings.LoggerFactory.CreateLogger(Log.Category);

    /// <summary>A first-party module's logger (provenance), under the module's own category, through the agent's factory.</summary>
    internal ILogger CreateLogger(string category) => _settings.LoggerFactory.CreateLogger(category);

    /// <summary>The host's store, or the default one once <see cref="StartAsync"/> knows whose it is.</summary>
    internal ISessionStore SessionStore => _settings.Sessions ?? _sessions ?? throw new InvalidOperationException("Call StartAsync first.");

    internal IApprovalStore ApprovalStore => _settings.ApprovalStore ?? _approvalStore ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>
    /// Loads the profile, checks the modules the agent requires, and settles the tools: host tools outside the
    /// agent's contract are dropped, the others get the stricter of their own and the contract's class. Without a
    /// contract -- or for an entry without a risk -- nobody on the server vouched for a tool: it is unclassified and
    /// always asks.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profile is not null)
            {
                return;
            }
            var profile = await Client.Agent!.GetProfileAsync(cancellationToken).ConfigureAwait(false);
            var missing = profile.RequiredClientModules.Where(m => !_settings.Modules.Contains(m)).ToList();
            if (missing.Count > 0)
            {
                throw new FadenstackException(
                    $"The agent '{Slug}' needs the module(s) {string.Join(", ", missing)}; add them to the builder.");
            }
            var contract = profile.HostToolContract.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
            var tools = new List<AgentTool>();
            var dropped = 0;
            foreach (var tool in _settings.Tools)
            {
                ToolContractEntry? entry = null;
                if (contract.Count > 0 && !contract.TryGetValue(tool.Name, out entry))
                {
                    dropped++;
                    Log.ToolDropped(Logger, tool.Name);
                    continue; // the gateway refuses it anyway; not offering it keeps the model from trying
                }
                var risk = ToolModes.ParseClass(entry?.Risk);
                tools.Add(new AgentTool(tool.Function, risk == ToolClass.Unknown ? ToolClass.Unknown : AgentTool.Effective(tool.Class, risk),
                    tool.Source, tool.Server, tool.OpenWorld) {
                    Describe = tool.Describe, Outputs = tool.Outputs, Scope = tool.Scope, ReadOnlyHint = tool.ReadOnlyHint, DestructiveHint = tool.DestructiveHint,
                });
            }
            var hostTools = tools.Count;
            var context = new AgentContext(Client, profile, Reporter.ReportNowAsync, _settings.LoggerFactory);
            var fromSources = new List<(IToolSource Source, IReadOnlyList<AgentTool> Tools)>();
            foreach (var source in _settings.Sources)
            {
                if (source is IChangingToolSource changing)
                {
                    EventHandler handler = (_, _) => OnSourceChanged(source);
                    changing.Changed += handler;
                    _subscriptions.Add((changing, handler));
                }
                fromSources.Add((source, await source.GetToolsAsync(context, cancellationToken).ConfigureAwait(false)));
            }
            _hostTools = tools;
            _context = context;
            lock (_changed)
            {
                _sourceTools.AddRange(fromSources);
            }
            tools = Compose();
            UseDefaultStores(profile.Principal, _settings.Retention.Within(profile.LocalRetention));
            _tools = tools;
            _profile = profile;
            _reasoningChoices = ReasoningChoices.FromUi(profile.Ui);
            _reasoningPick = _reasoningStore?.Get(Slug);
            Log.ProfileLoaded(Logger, Slug, profile.Version, profile.Edition, profile.Features.Count, profile.Principal is not null);
            Log.ToolsSettled(Logger, Slug, hostTools, tools.Count - hostTools, dropped);
            if (_settings.PermissionMode == PermissionMode.Bypass && !_warned)
            {
                _warned = true;
                Log.BypassMode(Logger, Slug);
            }
            if (_settings.Report)
            {
                _ = ReportManifestAsync();
            }
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    /// The stores the host did not pass: in the signed-in user's own folder, or -- from a gateway that names no user -- in
    /// the shared one an older SDK used.
    /// </summary>
    private void UseDefaultStores(AgentPrincipal? principal, SessionRetention retention)
    {
        if (principal is not null && (_settings.Sessions is null || _settings.ApprovalStore is null))
        {
            UserDataMigration.Adopt(principal.UserId, Logger);
        }
        string Folder(string name) => principal is null ? FadenstackPaths.Data(name) : FadenstackPaths.User(principal.UserId, name);
        if (_sessions is null && _settings.Sessions is null)
        {
            // A transcript is personal data: it goes to disk only encrypted.
            if (SecretProtection.Default.Encrypts)
            {
                _sessions = new FileSessionStore(Folder("sessions"), retention: retention);
            }
            else
            {
                _sessions = new MemorySessionStore();
                Log.TranscriptsNotKept(Logger, Slug);
            }
        }
        _approvalStore ??= _settings.ApprovalStore is null ? new FileApprovalStore(Folder("approvals")) : null;
        if (_reasoningStore is null && (_settings.Sessions is null || _settings.ApprovalStore is null))
        {
            // A host that keeps both stores itself has taken over the device's storage: the pick then lasts for the run only.
            _reasoningStore = new ReasoningChoiceStore(Folder("preferences"));
        }
    }

    /// <summary>
    /// The levels of thinking the agent offers: Auto, Off, and Low, Medium and High up to the maximum the profile's
    /// <c>ui.reasoning.max</c> sets, if any.
    /// </summary>
    public ReasoningChoices ReasoningChoices => _reasoningChoices;

    /// <summary>
    /// The level this person picked for this agent on this device, if any. Kept per person and per agent in the SDK's user
    /// folder, so it survives a restart and a new agent instance -- unless the host passed both its own session and approval
    /// stores, which leaves the pick in memory for the run. A pick above a maximum the server set later still counts, and
    /// is lowered to it.
    /// </summary>
    public ReasoningLevel? ReasoningChoice => _reasoningPick;

    /// <summary>
    /// How long new messages think: the person's pick, else what the app set with <see cref="AgentBuilder.UseReasoning"/>,
    /// else <see cref="ReasoningLevel.Auto"/> -- each lowered to the server's maximum, if it sets one.
    /// </summary>
    public ReasoningLevel Reasoning => ResolveReasoning().Level;

    /// <summary>What a request carries when nothing else was set for the session; null (no field) for Auto without a maximum.</summary>
    internal ReasoningEffort? RequestEffort => ResolveReasoning().Effort;

    /// <summary>What a request carries for a level the session was given: Auto is the maximum, if there is one.</summary>
    internal ReasoningEffort? EffortFor(ReasoningLevel level) =>
        level == ReasoningLevel.Auto ? _reasoningChoices.Max : _reasoningChoices.Cap(level.ToEffort());

    private (ReasoningLevel Level, ReasoningEffort? Effort) ResolveReasoning()
    {
        if (_reasoningPick is { } pick)
        {
            return pick == ReasoningLevel.Auto ? (ReasoningLevel.Auto, _reasoningChoices.Max) : Capped(_reasoningChoices.Cap(pick.ToEffort()));
        }
        // The app's own effort goes out exactly as it set it (extra high stays extra high) unless the maximum is lower.
        return _settings.Reasoning is { } app ? Capped(_reasoningChoices.Cap(app)) : (ReasoningLevel.Auto, _reasoningChoices.Max);
    }

    private static (ReasoningLevel Level, ReasoningEffort? Effort) Capped(ReasoningEffort? effort) => (ReasoningLevels.FromEffort(effort), effort);

    /// <summary>
    /// Keeps the person's pick for this agent (null forgets it), from the next message on; a running turn is not changed.
    /// A level the agent does not offer is ignored. Returns the level new messages think at.
    /// </summary>
    public async Task<ReasoningLevel> SetReasoningAsync(ReasoningLevel? level, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return SetReasoning(level);
    }

    /// <summary>
    /// <see cref="SetReasoningAsync"/> for a UI thread, once the agent started: it writes one small file and waits for nothing.
    /// </summary>
    public ReasoningLevel SetReasoning(ReasoningLevel? level)
    {
        if (level is null || _reasoningChoices.Offers(level.Value))
        {
            _reasoningPick = level;
            _reasoningStore?.Set(Slug, level);
        }
        return Reasoning;
    }

    /// <summary>
    /// Keeps the language the user picked for all their clients and the console (null: each follows its host), and in
    /// this agent's profile. Returns the language kept.
    /// </summary>
    public async Task<string?> SetUserLanguageAsync(string? language, CancellationToken cancellationToken = default)
    {
        var kept = await Client.Agent!.SetUserLanguageAsync(language, cancellationToken).ConfigureAwait(false);
        if (_profile is { } profile)
        {
            _profile = profile with { User = profile.User with { Language = kept } };
        }
        return kept;
    }

    /// <summary>
    /// The skills the user can pick for a message (<see cref="SendOptions.Skills"/>); none when the profile says the server
    /// offers no skills. <paramref name="query"/> narrows them by name or description on the server.
    /// </summary>
    public async Task<IReadOnlyList<SkillOption>> ListSkillsAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        if (_profile is { Skills: false })
        {
            return Array.Empty<SkillOption>();
        }
        var list = await Client.Agent!.ListSkillsAsync(query, 100, cancellationToken).ConfigureAwait(false);
        return list.Skills;
    }

    /// <summary>
    /// Tells the server what this app brings: every host tool with the class its developer gave it -- also those the
    /// contract does not list yet, which is how the server learns of them -- and the app's context and greeting.
    /// Never fails the start: an older server without the route, or no network, just means no report.
    /// </summary>
    private async Task ReportManifestAsync()
    {
        var app = _settings.App;
        var manifest = new AppManifest
        {
            App = new ManifestApp { Name = app.Name, Version = app.Version },
            Sdk = new ManifestSdk { Language = SdkInfo.Language, Version = SdkInfo.Version },
            Tools = _settings.Tools.Where(t => t.Source == "host").Select(t => new ManifestTool
            {
                Name = t.Name,
                Description = string.IsNullOrEmpty(t.Function.Description) ? null : t.Function.Description,
                Parameters = t.Function.JsonSchema,
                Class = t.Class,
                OpenWorld = t.OpenWorld,
                Outputs = t.Outputs.Count == 0 ? null
                    : t.Outputs.Select(o => new ManifestOutput
                    {
                        // A kind decided per call is no default to state: left out, as the TypeScript SDK leaves it.
                        Argument = o.Argument, MediaType = o.MediaType, EditKind = o.EditKindOf is null ? o.EditKind : null,
                    }).ToList(),
            }).ToList(),
            Context = app.Context,
            Ui = app.Ui,
        };
        try
        {
            await Client.Agent!.ReportManifestAsync(manifest).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to do: the server learns of the tools at the next start.
        }
    }

    /// <summary>A new session, in the agent's default mode (Ask) unless <paramref name="toolMode"/> says otherwise.</summary>
    public async Task<AgentSession> StartSessionAsync(ToolMode? toolMode = null, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var session = new AgentSession(this, Guid.NewGuid().ToString("N"), new List<TranscriptEntry>(), toolMode ?? DefaultToolMode);
        await session.WriteStartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>A session from this device's store, to go on with.</summary>
    public async Task<AgentSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var entries = await SessionStore.ReadAsync(Slug, sessionId, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw new KeyNotFoundException($"No session '{sessionId}' for '{Slug}' on this device.");
        }
        return new AgentSession(this, sessionId, entries.ToList(), AgentSession.LastMode(entries) ?? DefaultToolMode);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await StartUnless(_settings.Sessions, cancellationToken).ConfigureAwait(false);
        return await SessionStore.ListAsync(Slug, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await StartUnless(_settings.Sessions, cancellationToken).ConfigureAwait(false);
        await SessionStore.DeleteAsync(Slug, sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The profile feature that tells the server takes the scope <c>time</c> (and <c>expires_at</c>): without it "for 1 hour" is not offered.</summary>
    public const string TimeScopeFeature = "approval_scopes.time";

    /// <summary>How long "Allow for 1 hour" lasts.</summary>
    public static readonly TimeSpan TimeGrantDuration = TimeSpan.FromMinutes(60);

    /// <summary>The clock for time grants; tests set it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// What the user allowed for 1 hour, in every session of this agent: the tools whose grant has not ended yet. Kept in
    /// memory only: a restart asks again.
    /// </summary>
    public IReadOnlyList<TimeGrant> TimeGrants
    {
        get
        {
            var now = Clock();
            lock (_timeGrants)
            {
                return _timeGrants.Where(g => g.Value > now).Select(g => new TimeGrant(g.Key, g.Value)).OrderBy(g => g.Tool, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Ends the grant of <paramref name="tool"/> now; the next call asks again.</summary>
    public void RevokeTimeGrant(string tool) => ClearTimeGrants(tool);

    /// <summary>Whether the server's profile says it takes the scope <c>time</c>.</summary>
    internal bool OffersTimeGrants => _profile?.Features.Contains(TimeScopeFeature) == true;

    /// <summary>When the grant of <paramref name="tool"/> ends; null when it has none (or none that still holds).</summary>
    internal DateTimeOffset? TimeGrantOf(string tool)
    {
        if (!OffersTimeGrants)
        {
            return null;
        }
        var now = Clock();
        lock (_timeGrants)
        {
            if (_timeGrants.TryGetValue(tool, out var expires))
            {
                if (expires > now)
                {
                    return expires;
                }
                _timeGrants.Remove(tool);
            }
            return null;
        }
    }

    /// <summary>Allows <paramref name="tool"/> for <see cref="TimeGrantDuration"/> from now; returns when that ends.</summary>
    internal DateTimeOffset GrantTime(string tool)
    {
        var expires = Clock() + TimeGrantDuration;
        lock (_timeGrants)
        {
            _timeGrants[tool] = expires;
        }
        return expires;
    }

    private void ClearTimeGrants(string? tool)
    {
        lock (_timeGrants)
        {
            if (tool is null)
            {
                _timeGrants.Clear();
            }
            else
            {
                _timeGrants.Remove(tool);
            }
        }
    }

    /// <summary>
    /// Forgets what the user allowed always on this device for this agent, and what they allowed for 1 hour: every tool,
    /// or <paramref name="tool"/> only. (What a chat allowed ends with that chat: <see cref="AgentSession.ResetApprovals"/>.)
    /// </summary>
    public async Task ResetApprovalsAsync(string? tool = null, CancellationToken cancellationToken = default)
    {
        ClearTimeGrants(tool); // also what was allowed for 1 hour: in memory, so first
        await StartUnless(_settings.ApprovalStore, cancellationToken).ConfigureAwait(false);
        await ApprovalStore.RemoveAsync(Slug, tool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a ZIP of the signed-in user's data on this device to <paramref name="destination"/> (left open): every session
    /// of every agent, one JSON file each, the "Allow always" approvals, and a README (an access request, GDPR Art. 15 and 20).
    /// Without a user from the gateway these are the shared folders of an older SDK. Sign-in tokens are never included, nor
    /// is what the server keeps. A store the host passed is read for this agent only.
    /// </summary>
    public async Task ExportMyDataAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var counts = await DataExport.WriteAsync(destination, SessionStore, ApprovalStore, Slug, cancellationToken).ConfigureAwait(false);
        Log.DataExported(Logger, counts.Agents, counts.Sessions, counts.Approvals, counts.Unreadable);
    }

    /// <summary>
    /// Deletes the signed-in user's data on this device (an erasure request, GDPR Art. 17): the whole folder of the user --
    /// the sessions and approvals of all agents, and anything else kept there -- or, from a gateway that names no user, the
    /// shared <c>sessions</c> and <c>approvals</c> folders of an older SDK. Afterwards the stores start empty. It does not
    /// sign out (the sign-in tokens stay; that is the host's call: <see cref="Auth.AgentTokenProvider.SignOutAsync"/>), and it
    /// leaves stores the host passed to their owner. What the server keeps is for its operator to erase.
    /// </summary>
    public async Task DeleteMyDataAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var principal = Profile.Principal;
        var folders = principal is null
            ? new[] { Path.Combine(FadenstackPaths.DataRoot, "sessions"), Path.Combine(FadenstackPaths.DataRoot, "approvals"), Path.Combine(FadenstackPaths.DataRoot, "preferences") }
            : new[] { FadenstackPaths.UserFolder(principal.UserId) };
        try
        {
            var removed = folders.Count(FadenstackPaths.DeleteUnderData);
            Log.DataDeleted(Logger, removed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.DataDeletionFailed(Logger, e.GetType().Name);
            throw;
        }
    }

    /// <summary>The default stores need the profile (whose folder); a store the host passed does not.</summary>
    private Task StartUnless(object? hostStore, CancellationToken cancellationToken) =>
        hostStore is null ? StartAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Sends what is left to report.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Reporter.FlushAsync(cancellationToken);

    public void Dispose()
    {
        ClearTimeGrants(null);
        foreach (var (source, handler) in _subscriptions)
        {
            source.Changed -= handler;
        }
        _subscriptions.Clear();
        foreach (var source in _settings.Sources.OfType<IDisposable>())
        {
            source.Dispose();
        }
        if (_settings.OwnsClient)
        {
            _settings.Client.Dispose();
        }
        _starting.Dispose();
        _refreshing.Dispose();
    }

    /// <summary>The host tools, then each source's tools in the builder's order; a host tool wins over an MCP tool of the same name.</summary>
    private List<AgentTool> Compose()
    {
        var tools = new List<AgentTool>(_hostTools);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
        foreach (var (_, fromSource) in _sourceTools)
        {
            foreach (var tool in fromSource)
            {
                if (names.Add(tool.Name))
                {
                    tools.Add(tool);
                }
            }
        }
        return tools;
    }

    private void OnSourceChanged(IToolSource source)
    {
        lock (_changed)
        {
            _changed.Add(source);
        }
    }

    /// <summary>
    /// Asks the sources that said they changed for their tools again, and settles them as at start. Called at the start of a
    /// turn, never inside one, so a turn's tools do not change under it. A tool that went away stays refused by name.
    /// A source that fails keeps the tools it had.
    /// </summary>
    internal async Task RefreshToolsAsync(CancellationToken cancellationToken)
    {
        lock (_changed)
        {
            if (_changed.Count == 0 || _context is null)
            {
                return;
            }
        }
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<IToolSource> changed;
            lock (_changed)
            {
                changed = _changed.ToList();
                _changed.Clear();
            }
            if (changed.Count == 0)
            {
                return;
            }
            var before = _tools;
            foreach (var source in changed)
            {
                IReadOnlyList<AgentTool> fresh;
                try
                {
                    fresh = await source.GetToolsAsync(_context!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.ToolsRefreshFailed(Logger, Slug, e.GetType().Name); // it keeps the tools it had
                    continue;
                }
                lock (_changed)
                {
                    var at = _sourceTools.FindIndex(s => ReferenceEquals(s.Source, source));
                    if (at >= 0)
                    {
                        _sourceTools[at] = (source, fresh);
                    }
                }
            }
            List<AgentTool> after;
            lock (_changed)
            {
                after = Compose();
            }
            var now = new HashSet<string>(after.Select(t => t.Name), StringComparer.Ordinal);
            lock (_gone)
            {
                foreach (var tool in before)
                {
                    if (!now.Contains(tool.Name))
                    {
                        _gone.Add(tool.Name);
                    }
                }
                _gone.RemoveWhere(now.Contains);
            }
            _tools = after;
            Log.ToolsRefreshed(Logger, Slug, changed.Count, after.Count);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Whether a tool of this name was offered and went away: a call to it is refused as such, not as unknown.</summary>
    internal bool IsGone(string name)
    {
        lock (_gone)
        {
            return _gone.Contains(name);
        }
    }

    /// <summary>The tools the model gets in <paramref name="session"/> this round: the mode's, and those the tool's scope offers.</summary>
    internal IList<AITool> Offer(ToolMode mode, AgentSession session) =>
        _tools.Where(t => (t.Scope is null || t.Scope.Offers(t, session)) && ToolModes.Decide(mode, t.Class) != ToolModeDecision.Hidden)
            .Select(t => (AITool)t.Function).ToList();

    internal AgentTool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);
}

using System.Diagnostics;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>
/// An agent served by a Fadenstack server and run on this device: its profile, its tools, and its sessions.
/// The loop runs here; every model call goes through the gateway.
/// </summary>
public sealed partial class FadenstackAgent : AIAgent, IDisposable
{
    private readonly AgentSettings _settings;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private ISessionStore? _sessions;
    private IApprovalStore? _approvalStore;
    private AgentProfile? _profile;
    private DateTimeOffset _profileRead;
    private volatile IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private IReadOnlyList<AgentTool> _hostTools = Array.Empty<AgentTool>();
    private readonly List<(IToolSource Source, IReadOnlyList<AgentTool> Tools)> _sourceTools = new();
    private readonly HashSet<IToolSource> _changed = new();
    private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly List<(IChangingToolSource Source, EventHandler Handler)> _subscriptions = new();
    private AgentContext? _context;
    private bool _warned;
    private ILogger? _logger;
    private ReasoningChoiceStore? _reasoningStore;
    private ReasoningChoices _reasoningChoices = ReasoningChoices.Everything;
    private ReasoningLevel? _reasoningPick;
    private ChatRetentionStore? _retentionStore;
    private ChatRetentionChoice? _retentionPick;
    private readonly List<WeakReference<ChatSession>> _handedOut = new(); // the chats this agent gave out: never pruned while they live
    private volatile bool _hostPolicySeen;

    internal FadenstackAgent(AgentSettings settings)
    {
        _settings = settings;
        Reporter = new EventReporter(settings.Backend, settings.Report);
        Grants = new AgentGrants(this);
        StepUps = new StepUpProofs(this);
        Hooks = new HookRunner(settings.Hooks, settings.HookContent, () => Logger);
    }

    /// <summary>The app's hooks (<see cref="AgentBuilder.AddHooks"/>), in order.</summary>
    internal HookRunner Hooks { get; }

    /// <summary>The proof of a fresh sign-in for Bypass while it lasts, in memory only (sdk/41).</summary>
    internal StepUpProofs StepUps { get; }

    /// <summary>
    /// What the person let the agent run for a while on this device, held by the server (<see cref="AgentGrants.Available"/>
    /// when it holds grants): grants for a time, stretches of work, and the marker's Stop.
    /// </summary>
    public AgentGrants Grants { get; }

    public static AgentBuilder CreateBuilder() => new();

    public string Slug => _settings.Agent;

    /// <summary>
    /// The agent's Fadenstack client: the server's routes, its options, its transport for a module's own resources. An
    /// agent without a server (on the person's own model endpoint) has none: this throws an
    /// <see cref="InvalidOperationException"/> there; <see cref="ServerClient"/> is the way to ask.
    /// </summary>
    public FadenstackClient Client => _settings.Backend.ServerClient
        ?? throw new InvalidOperationException("This agent has no Fadenstack server, so it has no server client (ServerClient is null).");

    /// <summary>The agent's Fadenstack client; null for an agent without a server.</summary>
    public FadenstackClient? ServerClient => _settings.Backend.ServerClient;

    /// <summary>
    /// The person's own model endpoint (its base) for a standalone agent built with <see cref="AgentBuilder.UseEndpoint"/>;
    /// null with a server, or with a host's own chat client (<see cref="AgentBuilder.UseChatClient"/>).
    /// </summary>
    public Uri? EndpointUrl => (_settings.Backend as LocalAgentBackend)?.EndpointUrl;

    /// <summary>
    /// For a standalone agent, whether its connection marks text with the server's watermark (sdk/51 F03 item 8, F10): the
    /// endpoint check's report, else the sourced table. Null with a Fadenstack server, whose receipts say it per answer.
    /// </summary>
    public Endpoints.EndpointWatermarkStatus? EndpointWatermark => (_settings.Backend as LocalAgentBackend)?.Watermark;

    /// <summary>
    /// What IT set for this machine (<see cref="AgentBuilder.UseMachinePolicy"/>): modules read its lists (the MCP sources, the
    /// skill folders); <see cref="MachinePolicy.None"/> when the app gave none.
    /// </summary>
    public MachinePolicy MachinePolicy => _settings.MachinePolicy;

    /// <summary>
    /// The profile under the machine policy: what it forbids is off whatever the server or the person says (R3-8); it never
    /// turns on what they left off.
    /// </summary>
    private AgentProfile Narrowed(AgentProfile profile) => profile with
    {
        LocalMcp = (_settings.MachinePolicy.Mcp.AllowsAny ? profile.LocalMcp : profile.LocalMcp with { Mode = "disable", DeveloperMode = false })
            is var mcp && _settings.MachinePolicy.PlainHttp != PolicyAccess.Allowed ? mcp with { PlainHttp = "deny" } : mcp,
        LocalSkills = _settings.MachinePolicy.Skills.AllowsAny ? profile.LocalSkills : profile.LocalSkills with { Mode = "disable", DeveloperMode = false },
    };

    /// <summary>For a standalone agent, the kind of server behind its endpoint; null with a Fadenstack server.</summary>
    internal Endpoints.EndpointServer? EndpointServer => (_settings.Backend as LocalAgentBackend)?.Server;

    /// <summary>
    /// How the agent is connected: <see cref="AgentConnection.Governed"/> to an organisation's Fadenstack server, or
    /// <see cref="AgentConnection.Standalone"/> to the person's own model endpoint. For showing or hiding the server's parts.
    /// </summary>
    public AgentConnection Connection => _settings.Backend.Connection;

    /// <summary>
    /// The tools of the organisation's MCP servers that the user and this agent can have, with what the agent's tool rules
    /// make of each (<c>GET /v1/agents/{agent}/tools</c>). Null when the server does not list them (an older one).
    /// </summary>
    public Task<IReadOnlyList<AgentToolInfo>?> ListServerToolsAsync(CancellationToken cancellationToken = default) =>
        _settings.Backend.ListToolsAsync(cancellationToken);

    /// <summary>What the agent talks to: the server, or later the person's own model endpoint (sdk/51 F02).</summary>
    internal IAgentBackend Backend => _settings.Backend;

    /// <summary>The server's provenance routes, for Fadenstack.Provenance's server backend; only a server has them.</summary>
    internal IProvenanceRoutes ProvenanceRoutes => _settings.Backend as IProvenanceRoutes
        ?? throw new InvalidOperationException("This agent has no Fadenstack server, so it has no provenance routes.");

    /// <summary>The app the agent runs in, as the host described it (<see cref="AgentBuilder.DescribeApp"/>); null when it did not.</summary>
    public AppDescription? App => _settings.AppDescribed ? _settings.App : null;

    /// <summary>The agent's profile, once <see cref="StartAsync"/> ran.</summary>
    public AgentProfile Profile => _profile ?? throw new InvalidOperationException("Call StartAsync first.");

    internal AgentProfile? ProfileOrNull => _profile;

    /// <summary>The tools the agent runs, with their effective classes, once started.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    /// <summary>
    /// The most any session may use: the narrowest of the profile's ceiling and the agent's highest level, where the server
    /// sets them, else Auto. A level this SDK does not know narrows to read-only: it cannot be taken for none (39 F8).
    /// Bypass only where the server offers it (<see cref="BypassOffered"/>); otherwise Auto.
    /// </summary>
    public ToolMode Ceiling
    {
        get
        {
            var top = ToolModes.Narrowest(Level(_profile?.ToolModes.Ceiling), Level(_profile?.ToolModes.Max));
            return top == ToolMode.Bypass && !BypassOffered ? ToolMode.Auto : top;
        }
    }

    /// <summary>
    /// Whether a person may switch approvals off in a conversation with this agent now (sdk/09 L-57): the organisation
    /// allows it, the agent's version goes up to it, and no ceiling is lower -- as the server says in the profile.
    /// </summary>
    public bool BypassOffered => Grants.Settings?.Bypass is { Allowed: true };

    /// <summary>Whether pictures tools return go to the model: the app allows it and the profile says the model sees.</summary>
    public bool SeesPictures => _settings.Pictures && _profile?.Vision == true;

    /// <summary>
    /// The mode new sessions start in: the agent's default level from the server (Ask from an older one, or one this SDK
    /// does not know), no higher than the app's own default (<see cref="AgentBuilder.UseDefaultToolMode"/>).
    /// <see cref="ChatSession.EffectiveToolMode"/> narrows it to the <see cref="Ceiling"/>. Never Bypass (sdk/38 D2): a
    /// default the server names as Bypass is Ask.
    /// </summary>
    public ToolMode DefaultToolMode => ToolModes.Narrowest(
        ToolModes.ParseMode(_profile?.ToolModes.Default) is { } level && level != ToolMode.Bypass ? level : ToolMode.Ask,
        _settings.DefaultMode);

    /// <summary>How old the profile may be when a session starts or opens before it is read again (39 F8).</summary>
    internal static readonly TimeSpan ProfileMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>How long one try at answering a gateway's approval may take before it is sent once more; tests shorten it.</summary>
    internal TimeSpan ApprovalAnswerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The time, for the profile's age and the grants' remaining time (<see cref="AgentGrants.ServerNow"/>); tests set it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // No level: no limit (Bypass, cut to Auto in Ceiling unless offered); one this SDK does not know: read-only.
    private static ToolMode Level(string? level) =>
        string.IsNullOrWhiteSpace(level) ? ToolMode.Bypass : ToolModes.ParseMode(level) ?? ToolMode.ReadOnly;

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
            var profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false);
            AssertHostPolicySupport(profile);
            var dropped = new List<string>();
            var tools = HostTools(profile, dropped);
            foreach (var name in dropped)
            {
                Log.ToolDropped(Logger, name);
            }
            var hostTools = tools.Count;
            var context = new AgentContext(ServerClient, profile, Reporter.ReportNowAsync, _settings.LoggerFactory, _settings.MachinePolicy, this);
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
            UseDefaultStores(profile.Principal, () => Limits(ComputeChatRetention()));
            if (profile.Principal is { } person && (_settings.Sessions is null || _settings.ApprovalStore is null))
            {
                // Which apps use the person's data on this device, for "Remove my data" (owner, 16 Nov); names and times only.
                MyDataApps.TryRecord(FadenstackPaths.UserFolder(person.UserId), AppName, Slug, Clock());
            }
            _tools = tools;
            _profile = profile;
            _reasoningChoices = ReasoningChoices.FromUi(profile.Ui);
            _reasoningPick = _reasoningStore?.Get(Slug);
            _retentionPick = _retentionStore?.Get();
            await PruneChatsAsync(cancellationToken).ConfigureAwait(false); // an idle profile ages out too (F08), when pruning is on
            Log.ProfileLoaded(Logger, Slug, profile.Version, profile.Edition, profile.Features.Count, profile.Principal is not null);
            Log.ToolsSettled(Logger, Slug, hostTools, tools.Count - hostTools, dropped.Count);
            if (_settings.PermissionMode == PermissionMode.Bypass && !_warned && !BypassOffered)
            {
                _warned = true;
                Log.BypassMode(Logger, Slug); // set to bypass, which no server allows yet: every call still asks
            }
            else if (_settings.PermissionMode == PermissionMode.Bypass && !_warned && _settings.StepUp is null)
            {
                _warned = true;
                Trace.TraceWarning($"The agent '{Slug}' is set to bypass mode, but the app opens no second sign-in " +
                    "(AgentBuilder.UseStepUpHandler): Bypass needs one, so its sessions run in Auto (sdk/41).");
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

    private async Task<AgentProfile> ReadProfileAsync(CancellationToken cancellationToken)
    {
        var profile = Narrowed(await Backend.GetProfileAsync(cancellationToken).ConfigureAwait(false));
        var missing = profile.RequiredClientModules.Where(m => !_settings.Modules.Contains(m)).ToList();
        if (missing.Count > 0)
        {
            throw new FadenstackException(
                $"The agent '{Slug}' needs the module(s) {string.Join(", ", missing)}; add them to the builder.");
        }
        _profileRead = Clock();
        return profile;
    }

    /// <summary>
    /// The host tools <paramref name="profile"/>'s contract allows, with its class and approval; the names of those it
    /// leaves out go to <paramref name="dropped"/>.
    /// </summary>
    private List<AgentTool> HostTools(AgentProfile profile, List<string>? dropped = null)
    {
        var contract = profile.HostToolContract.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var tools = new List<AgentTool>();
        foreach (var tool in _settings.Tools)
        {
            ToolContractEntry? entry = null;
            if (contract.Count > 0 && !contract.TryGetValue(tool.Name, out entry))
            {
                dropped?.Add(tool.Name);
                continue; // the gateway refuses it anyway; not offering it keeps the model from trying
            }
            var risk = ToolModes.ParseClass(entry?.Risk);
            tools.Add(new AgentTool(tool.Function, risk == ToolClass.Unknown ? ToolClass.Unknown : AgentTool.Effective(tool.Class, risk),
                tool.Source, tool.Server, tool.OpenWorld) {
                Describe = tool.Describe, Outputs = tool.Outputs, Scope = tool.Scope, ReadOnlyHint = tool.ReadOnlyHint, DestructiveHint = tool.DestructiveHint,
                Approval = entry?.Approval, MakesPictures = tool.MakesPictures,
            });
        }
        return tools;
    }

    /// <summary>
    /// Whether host tool calls of the model go through the server's fresh dispatch checks (<c>host_tools.dispatch_check</c>):
    /// the server advertised them, or the app required them (<see cref="AgentBuilder.RequireHostToolPolicyChecks"/>). Once
    /// negotiated, losing the support fails closed until a new agent is built.
    /// </summary>
    public bool HostToolPolicyChecks => _hostPolicySeen || _settings.RequireHostToolPolicyChecks;

    /// <summary>
    /// Reads the profile again, right now, for the approval limits and the server's support for the checks. The agent's
    /// identity and its published contract must be the ones the agent started with; a change throws a
    /// <see cref="FadenstackException"/> (reconnect before running tools).
    /// </summary>
    internal async Task RefreshPolicyAsync(CancellationToken cancellationToken)
    {
        var before = Profile;
        var fresh = Narrowed(await Backend.GetProfileAsync(cancellationToken).ConfigureAwait(false));
        AssertHostPolicySupport(fresh);
        if (Binding(before) != Binding(fresh))
        {
            throw new FadenstackException("The agent identity or published contract changed. Reconnect before running tools.");
        }
        _profile = fresh;
        _profileRead = Clock();
        if (_context is not null)
        {
            _context = new AgentContext(ServerClient, fresh, Reporter.ReportNowAsync, _settings.LoggerFactory, _settings.MachinePolicy, this);
        }
    }

    /// <summary>What must not change while the agent runs: who it is, what it was published with.</summary>
    private static string Binding(AgentProfile p) => System.Text.Json.JsonSerializer.Serialize(new
    {
        p.Id, p.Slug, p.Version, p.Draft, p.Principal, Contract = p.HostToolContract, p.InstructionsSha256,
        Modules = p.RequiredClientModules, p.Extensions,
    }, Http.FadenstackJson.Options);

    /// <summary>The approval limits a host call was reviewed under; a change while the person decides stops the call.</summary>
    internal string ApprovalControls => System.Text.Json.JsonSerializer.Serialize(
        new { Modes = Profile.ToolModes, Profile.Approvals }, Http.FadenstackJson.Options);

    private void AssertHostPolicySupport(AgentProfile profile)
    {
        var supported = profile.Features.Contains(HostToolFeatures.DispatchCheck);
        if ((_settings.RequireHostToolPolicyChecks || _hostPolicySeen) && !supported)
        {
            throw new FadenstackException(
                "This agent requires fresh central host-tool dispatch checks. The gateway does not advertise support.");
        }
        if (supported)
        {
            _hostPolicySeen = true;
        }
    }

    /// <summary>
    /// Reads the profile again when a session starts or opens and it is older than <see cref="ProfileMaxAge"/> (39 F8): a
    /// ceiling an administrator lowered, a highest level, or a tool's class or approval reach the next session without a
    /// restart. The tools of the agent's sources (local MCP) stay as they were. When the server cannot be reached the
    /// profile held stays: every model call goes through the server anyway, and a session on this device still opens.
    /// </summary>
    private async Task RefreshProfileAsync(CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profile is null || Clock() - _profileRead < ProfileMaxAge)
            {
                return;
            }
            AgentProfile profile;
            try
            {
                profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.ProfileRereadFailed(Logger, Slug, e.GetType().Name);
                return;
            }
            AssertHostPolicySupport(profile); // not swallowed above: a server that dropped the checks is not trusted with the old ones
            var hosts = HostTools(profile);
            // Settled as a changed tool source is (RefreshToolsAsync): one at a time, and a host tool that went away is refused by name.
            await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var before = _tools;
                List<AgentTool> after;
                lock (_changed)
                {
                    _hostTools = hosts;
                    after = Compose();
                }
                MarkGone(before, after);
                _tools = after;
            }
            finally
            {
                _refreshing.Release();
            }
            _profile = profile;
            _reasoningChoices = ReasoningChoices.FromUi(profile.Ui); // a maximum the server lowered counts from the next message
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
    private void UseDefaultStores(AgentPrincipal? principal, Func<SessionRetention?> retention)
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
                _sessions = new FileSessionStore(Folder("sessions"), null, retention, Logger);
            }
            else
            {
                _sessions = new MemorySessionStore();
                Log.TranscriptsNotKept(Logger, Slug);
            }
        }
        _approvalStore ??= _settings.ApprovalStore is null ? new FileApprovalStore(Folder("approvals"), logger: Logger) : null;
        if (_reasoningStore is null && (_settings.Sessions is null || _settings.ApprovalStore is null))
        {
            // A host that keeps both stores itself has taken over the device's storage: the pick then lasts for the run only.
            _reasoningStore = new ReasoningChoiceStore(Folder("preferences"));
            _retentionStore = new ChatRetentionStore(Folder("preferences"));
        }
    }

    /// <summary>
    /// How long old chats are kept on this device, and who decides (<see cref="ChatRetentionState.Control"/>). In a standalone
    /// agent the person chooses (<see cref="SetChatRetentionAsync"/>; on, 90 days, 50 chats unless the app's
    /// <see cref="AgentBuilder.UseSessionRetention"/> says otherwise) unless the machine policy fixes it (<c>chatRetention</c>). In a
    /// governed agent the server decides, as the stricter of its <c>local_retention</c> and the app's limits, and the person's stored
    /// choice is ignored. Call <see cref="StartAsync"/> first for the person's own choice and the server's limits.
    /// </summary>
    public ChatRetentionState ChatRetention => ComputeChatRetention();

    private ChatRetentionState ComputeChatRetention()
    {
        if (Connection == AgentConnection.Governed)
        {
            var server = _profile?.LocalRetention;
            var limits = _settings.Retention.Within(server);
            return new ChatRetentionState(ChatRetentionControl.Organisation, true, limits.MaxAgeDays, limits.MaxSessions, server is not null);
        }
        if (_settings.MachinePolicy.ChatRetention is { } fixedBy)
        {
            return new ChatRetentionState(ChatRetentionControl.MachinePolicy, fixedBy.AutoDelete, fixedBy.MaxAgeDays, fixedBy.MaxSessions);
        }
        var choice = _retentionPick ?? ChatRetentionChoice.From(_settings.Retention);
        return new ChatRetentionState(ChatRetentionControl.Person, choice.AutoDelete, choice.MaxAgeDays, choice.MaxSessions);
    }

    private static SessionRetention? Limits(ChatRetentionState state) =>
        state.AutoDelete ? new SessionRetention(state.MaxSessions, state.MaxAgeDays) : null;

    /// <summary>
    /// Keeps the person's choice for old chats (a standalone agent only), on this device and for this person, and applies it at
    /// once: when automatic deletion is on, chats beyond the limits go now, except the ones open. Returns the state in force.
    /// In governed mode, or when the machine policy fixes it, the choice is ignored (nothing is kept) and the state is returned as it is.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null.</exception>
    public async Task<ChatRetentionState> SetChatRetentionAsync(ChatRetentionChoice choice, CancellationToken cancellationToken = default)
    {
        if (choice is null)
        {
            throw new ArgumentNullException(nameof(choice));
        }
        await StartAsync(cancellationToken).ConfigureAwait(false);
        if (!ComputeChatRetention().CanChange)
        {
            return ComputeChatRetention();
        }
        _retentionPick = choice;
        _retentionStore?.Set(choice);
        await PruneChatsAsync(cancellationToken).ConfigureAwait(false);
        return ComputeChatRetention();
    }

    /// <summary>Deletes this agent's chats beyond the limits in force (nothing when automatic deletion is off), but never one that is open.</summary>
    private async Task PruneChatsAsync(CancellationToken cancellationToken)
    {
        if (SessionStore is not IPrunesSessions store || Limits(ComputeChatRetention()) is null)
        {
            return;
        }
        try
        {
            await store.PruneAsync(Slug, OpenChatIds(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a chat another process holds stays for now; the next start tries again
        }
    }

    private List<string> OpenChatIds()
    {
        lock (_handedOut)
        {
            _handedOut.RemoveAll(r => !r.TryGetTarget(out _));
            return _handedOut.Select(r => r.TryGetTarget(out var s) ? s.Id : "").Where(id => id.Length > 0).ToList();
        }
    }

    private ChatSession Handed(ChatSession session)
    {
        lock (_handedOut)
        {
            _handedOut.Add(new WeakReference<ChatSession>(session));
        }
        return session;
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
        var kept = await Backend.SetUserLanguageAsync(language, cancellationToken).ConfigureAwait(false);
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
        var list = await Backend.ListSkillsAsync(query, 100, cancellationToken).ConfigureAwait(false);
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
            await Backend.ReportManifestAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to do: the server learns of the tools at the next start.
        }
    }

    /// <summary>A new session, in the agent's default mode (<see cref="DefaultToolMode"/>) unless <paramref name="toolMode"/> says otherwise.</summary>
    public async Task<ChatSession> StartSessionAsync(ToolMode? toolMode = null, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
        var session = Handed(new ChatSession(this, Guid.NewGuid().ToString("N"), new List<TranscriptEntry>(), toolMode ?? DefaultToolMode));
        await session.WriteStartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private IAnswerCopyMarker? _copyMarker;

    /// <summary>
    /// Marks an answer the person copies out of a chat, through the module that marks content (<c>Fadenstack.Provenance</c>), as
    /// a tool's placement is: the same policy, a record signed by the server (or by this device, without one), a report and a
    /// transcript entry. Without that module the copy comes back as it is (<see cref="MarkedCopy.Marked"/> false). The chat view
    /// calls it for its Copy; an app that shows answers in its own view calls it itself, before it puts them on the clipboard.
    /// TS: <c>markCopy</c>.
    /// </summary>
    /// <returns>What to put on the clipboard; <see cref="MarkedCopy.Refused"/> when the policy forbids the copy and nothing is to be copied.</returns>
    public async Task<MarkedCopy> MarkCopyAsync(AnswerCopy copy, CancellationToken cancellationToken = default)
    {
        if (copy is null)
        {
            throw new ArgumentNullException(nameof(copy));
        }
        var marker = _copyMarker ??= _settings.CopyMarker?.Invoke(this);
        if (marker is null)
        {
            return new MarkedCopy(copy.Text, copy.Html, null, false);
        }
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await marker.MarkAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A call that is no tool call, for the module that marks copies to place content with: the tool is <c>copy</c>, the chat and
    /// turn are the copy's, and its transcript entries go into the chat when this agent gave that chat out.
    /// </summary>
    internal ToolInvocation CopyInvocation(AnswerCopy copy)
    {
        ChatSession? session = null;
        lock (_handedOut)
        {
            foreach (var reference in _handedOut)
            {
                if (copy.Chat is not null && reference.TryGetTarget(out var open) && open.Id == copy.Chat)
                {
                    session = open;
                    break;
                }
            }
        }
        var tool = new AgentTool(AIFunctionFactory.Create(() => "", ToolName.Copy));
        var turn = copy.Turn ?? "";
        return new ToolInvocation(tool, new FunctionCallContent("copy-" + Guid.NewGuid().ToString("N"), ToolName.Copy),
            new AIFunctionArguments(), copy.Chat ?? "", turn, this,
            session is null ? null : (type, data) => session.RecordAsync(type, copy.Turn, data), null, session);
    }

    private static class ToolName
    {
        public const string Copy = "copy";
    }

    /// <summary>A session from this device's store, to go on with.</summary>
    public async Task<ChatSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
        var entries = await SessionStore.ReadAsync(Slug, sessionId, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw new KeyNotFoundException($"No session '{sessionId}' for '{Slug}' on this device.");
        }
        // A conversation whose Bypass grant still lives opens in Bypass, with its marker; one without never does
        // (owner, 2026-11-09: parallel chats never break). The copy is read first where the server holds grants.
        if (Grants.Available)
        {
            try
            {
                await Grants.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FadenstackException or HttpRequestException)
            {
                // offline: the copy as it is; the next turn settles it
            }
        }
        // The grant decides, not the transcript: Bypass switched on before the chat's first message left no turn in it.
        var lives = Grants.Live.Any(g => g.Kind == GrantKinds.Bypass && g.ConversationId == sessionId);
        var before = ChatSession.LastMode(entries) ?? DefaultToolMode;
        var session = Handed(new ChatSession(this, sessionId, entries.ToList(), lives ? ToolMode.Bypass : before));
        session.Reopened(before);
        return session;
    }

    /// <summary>This device's sessions of the agent: the signed-in user's, so the agent starts first unless the host keeps them.</summary>
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

    /// <summary>
    /// Forgets what the user allowed always on this device for this agent: every tool, or <paramref name="tool"/> only.
    /// (What a chat allowed ends with that chat: <see cref="ChatSession.ResetApprovals"/>.)
    /// </summary>
    public async Task ResetApprovalsAsync(string? tool = null, CancellationToken cancellationToken = default)
    {
        await StartUnless(_settings.ApprovalStore, cancellationToken).ConfigureAwait(false);
        await ApprovalStore.RemoveAsync(Slug, tool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the user allowed always on this device for this agent, for Settings → Approvals (38 §7); each is ended with
    /// <see cref="ResetApprovalsAsync"/>. The grants the server holds are in <see cref="Grants"/>.
    /// </summary>
    public async Task<IReadOnlyList<StoredApproval>> ListApprovalsAsync(CancellationToken cancellationToken = default)
    {
        await StartUnless(_settings.ApprovalStore, cancellationToken).ConfigureAwait(false);
        return await ApprovalStore.ListAsync(Slug, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a ZIP of this app's part of the signed-in user's data on this device to <paramref name="destination"/> (left
    /// open), what <see cref="DeleteMyDataAsync(CancellationToken)"/> removes (an access request, GDPR Art. 15 and 20; since
    /// standalone.9 this app's scope, <see cref="DataScope.ThisApp"/>, owner 16 Nov). All of it, with
    /// <see cref="DataScope.AllApps"/>: every session of every agent, one JSON file each, the "Allow always" approvals, the
    /// preferences, the standalone language, skills, the MCP servers without what can hold a secret and their audit log, the
    /// provenance records and their log, the apps' records, and a README. Without a user from the gateway these are the shared
    /// folders of an older SDK. Sign-in tokens, keys and secret values are never included, nor is what the server keeps. A
    /// store the host passed is read for this agent only.
    /// </summary>
    public Task ExportMyDataAsync(Stream destination, CancellationToken cancellationToken = default) =>
        ExportMyDataAsync(destination, DataScope.ThisApp, cancellationToken);

    /// <summary>
    /// Writes a ZIP of the person's data in <paramref name="scope"/> (see <see cref="ExportMyDataAsync(Stream, CancellationToken)"/>):
    /// <see cref="DataScope.ThisApp"/> holds exactly what <see cref="DeleteMyDataAsync(DataScope, CancellationToken)"/> of this
    /// app would remove, and its README names what stayed because other apps use it; <see cref="DataScope.AllApps"/> holds all of it.
    /// </summary>
    public async Task ExportMyDataAsync(Stream destination, DataScope scope, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var principal = Profile.Principal;
        var folder = principal is null ? null : FadenstackPaths.UserFolder(principal.UserId);
        var app = scope == DataScope.ThisApp
            ? new DataExport.AppScope(AppName, folder is null ? MyDataPlan.NoUser(Slug) : MyDataApps.Plan(folder, AppName, Slug)) : null;
        var counts = await DataExport.WriteAsync(destination, SessionStore, ApprovalStore, Slug, cancellationToken, folder,
            principal is null ? Path.Combine(FadenstackPaths.DataRoot, "preferences") : null, app).ConfigureAwait(false);
        Log.DataExported(Logger, counts.Agents, counts.Sessions, counts.Approvals, counts.Unreadable);
    }

    /// <summary>
    /// The apps that use the signed-in person's data on this device, this app first: what a removal of all of it affects, and
    /// why a removal of this app's data leaves the shared data. An app on an older SDK shows as its agent (<see cref="MyDataApp.Recorded"/> false).
    /// </summary>
    public async Task<IReadOnlyList<MyDataApp>> MyDataAppsAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return Profile.Principal is { } principal
            ? MyDataApps.List(FadenstackPaths.UserFolder(principal.UserId), AppName, Slug)
            : new[] { new MyDataApp(AppName, new[] { Slug }, null, IsThisApp: true, Recorded: false) };
    }

    /// <summary>The app's name for the person's data: what the host described (<see cref="AgentBuilder.DescribeApp"/>), else the slug.</summary>
    internal string AppName => MyDataApps.NameOf(App, Slug);

    /// <summary>
    /// Deletes this app's part of the signed-in user's data on this device (an erasure request, GDPR Art. 17; since
    /// standalone.9 this app's scope, <see cref="DataScope.ThisApp"/>, owner 16 Nov; see
    /// <see cref="DeleteMyDataAsync(DataScope, CancellationToken)"/>). With <see cref="DataScope.AllApps"/>: the whole folder of
    /// the user -- the sessions and approvals of all agents, and anything else kept there -- or, from a gateway that names no
    /// user, the shared <c>sessions</c> and <c>approvals</c> folders of an older SDK. Afterwards the stores start empty. It does not
    /// sign out (the sign-in tokens stay; that is the host's call: <see cref="Auth.AgentTokenProvider.SignOutAsync"/>), and it
    /// leaves stores the host passed to their owner -- they may hold other people's data -- but says so: in its result and
    /// as a warning in the log, so a host never believes a person's data gone that its own store still keeps
    /// (fadenstack-dev sdk/46 D-1). What the server keeps is for its operator to erase.
    /// </summary>
    public Task<DataDeletion> DeleteMyDataAsync(CancellationToken cancellationToken = default) =>
        DeleteMyDataAsync(DataScope.ThisApp, cancellationToken);

    /// <summary>
    /// Deletes the person's data in <paramref name="scope"/> (an erasure request, GDPR Art. 17; owner, 16 Nov).
    /// <see cref="DataScope.ThisApp"/>: this app's agents' sessions, "Allow always" and thinking levels, and its record; an agent
    /// another app also uses stays (<see cref="DataDeletion.AgentsKept"/>); the shared data (skills, the MCP list, keys, provenance
    /// records, the person's other preferences) and the folder go only when no other app uses the person's data
    /// (<see cref="DataDeletion.OtherApps"/> names those that do). <see cref="DataScope.AllApps"/>: the person's whole folder, as
    /// <see cref="MyDataAppsAsync"/> lists the apps it affects. Neither signs out, and stores the host passed stay the host's.
    /// </summary>
    public async Task<DataDeletion> DeleteMyDataAsync(DataScope scope, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var principal = Profile.Principal;
        if (scope == DataScope.ThisApp)
        {
            return DeleteThisAppsData(principal);
        }
        var folders = principal is null
            ? new[] { Path.Combine(FadenstackPaths.DataRoot, "sessions"), Path.Combine(FadenstackPaths.DataRoot, "approvals"), Path.Combine(FadenstackPaths.DataRoot, "preferences") }
            : new[] { FadenstackPaths.UserFolder(principal.UserId) };
        try
        {
            var removed = folders.Count(FadenstackPaths.DeleteUnderData);
            Log.DataDeleted(Logger, removed);
            var deletion = new DataDeletion(removed, _settings.Sessions is not null, _settings.ApprovalStore is not null) { Scope = DataScope.AllApps };
            _reasoningPick = null;
            _retentionPick = null;
            if (deletion.HostStoresLeft)
            {
                Log.DataLeftInHostStores(Logger, deletion.HostSessionStoreLeft, deletion.HostApprovalStoreLeft);
            }
            return deletion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.DataDeletionFailed(Logger, e.GetType().Name);
            throw;
        }
    }

    // This app's part (DataScope.ThisApp). From a gateway that names no user, the shared folders of an older SDK: this agent's
    // sessions, "Allow always" and thinking level only (MyDataPlan.NoUser); the other agents' data and the shared preferences stay.
    private DataDeletion DeleteThisAppsData(AgentPrincipal? principal)
    {
        try
        {
            int removed;
            MyDataPlan plan;
            if (principal is null)
            {
                var safe = FadenstackPaths.SafeName(Slug);
                var sessions = Path.Combine(FadenstackPaths.DataRoot, "sessions", safe);
                var approvals = Path.Combine(FadenstackPaths.DataRoot, "approvals", safe + ".json");
                removed = (Directory.Exists(sessions) && FadenstackPaths.DeleteUnderData(sessions) ? 1 : 0)
                    + (File.Exists(approvals) ? 1 : 0);
                if (File.Exists(approvals))
                {
                    File.Delete(approvals);
                }
                plan = MyDataPlan.NoUser(Slug);
                removed += MyDataApps.ForgetReasoning(Path.Combine(FadenstackPaths.DataRoot, "preferences", "reasoning.json"), plan.OwnAgents);
            }
            else
            {
                var folder = FadenstackPaths.UserFolder(principal.UserId);
                plan = MyDataApps.Plan(folder, AppName, Slug);
                removed = MyDataApps.Remove(folder, AppName, plan);
            }
            Log.DataDeleted(Logger, removed);
            if (plan.OwnAgents.Contains(Slug, StringComparer.Ordinal))
            {
                _reasoningPick = null;
            }
            if (plan.SharedGoes)
            {
                _retentionPick = null;
            }
            var deletion = new DataDeletion(removed, _settings.Sessions is not null, _settings.ApprovalStore is not null)
            {
                Scope = DataScope.ThisApp,
                OtherApps = plan.OtherApps.Select(a => a.Name).ToList(),
                AgentsKept = plan.KeptAgents,
            };
            if (deletion.HostStoresLeft)
            {
                Log.DataLeftInHostStores(Logger, deletion.HostSessionStoreLeft, deletion.HostApprovalStoreLeft);
            }
            return deletion;
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
        foreach (var (source, handler) in _subscriptions)
        {
            source.Changed -= handler;
        }
        _subscriptions.Clear();
        foreach (var source in _settings.Sources.OfType<IDisposable>())
        {
            source.Dispose();
        }
        _settings.Backend.Dispose(); // the client, when the builder made it
        _starting.Dispose();
        _refreshing.Dispose();
    }

    /// <summary>
    /// The host tools, then each source's tools in the builder's order: a host tool wins over a source's of the same name, the
    /// first source over a later one, and the tool left out is logged with the reason. A name the SDK keeps for itself
    /// comes only from the SDK's own sources.
    /// </summary>
    private List<AgentTool> Compose()
    {
        var tools = new List<AgentTool>(_hostTools);
        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
        foreach (var (source, fromSource) in _sourceTools)
        {
            foreach (var tool in fromSource)
            {
                if (AgentTool.IsReserved(tool.Name) && source is not ICoreToolSource)
                {
                    Log.ToolNameReserved(Logger, tool.Name); // an app's source: the name is the SDK's
                    continue;
                }
                if (names.Add(tool.Name))
                {
                    tools.Add(tool);
                }
                else
                {
                    Log.ToolNameTaken(Logger, tool.Name, _hostTools.Any(t => t.Name == tool.Name) ? "the app's own tool" : "an earlier tool source");
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
            MarkGone(before, after);
            _tools = after;
            Log.ToolsRefreshed(Logger, Slug, changed.Count, after.Count);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Keeps the names of the tools that were offered and are no more; a name offered again is taken off.</summary>
    private void MarkGone(IReadOnlyList<AgentTool> before, IReadOnlyList<AgentTool> after)
    {
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
    }

    /// <summary>Whether a tool of this name was offered and went away: a call to it is refused as such, not as unknown.</summary>
    internal bool IsGone(string name)
    {
        lock (_gone)
        {
            return _gone.Contains(name);
        }
    }

    /// <summary>
    /// The tools the model gets in <paramref name="session"/> this round: the mode's, and those the tool's scope offers; a tool that
    /// <see cref="AgentTool.MakesPictures"/> only while <see cref="ImagesAvailable"/> (read at each round, so a profile that changed
    /// <c>images</c> or a refusal counts from the next one).
    /// </summary>
    internal IList<AITool> Offer(ToolMode mode, ChatSession session)
    {
        var pictures = ImagesAvailable;
        return _tools.Where(t => (pictures || !t.MakesPictures) && (t.Scope is null || t.Scope.Offers(t, session))
                && ToolModes.Decide(mode, t.Class) != ToolModeDecision.Hidden)
            .Select(t => (AITool)t.Function).ToList();
    }

    /// <summary>The tool of this name a call may run; a picture tool while <see cref="ImagesAvailable"/> is false is not one (refused as unknown).</summary>
    internal AgentTool? Find(string name)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        return tool is { MakesPictures: true } && !ImagesAvailable ? null : tool;
    }

    private volatile bool _imagesRefused;

    /// <summary>Whether <paramref name="name"/> is a picture tool that is hidden because the server refused pictures (a second call in the round that got the refusal).</summary>
    internal bool IsRefusedPictureTool(string name) => _imagesRefused && _tools.Any(t => t.MakesPictures && t.Name == name);

    /// <summary>
    /// Whether tools that make pictures (<see cref="AgentTool.MakesPictures"/>) are offered: only for a governed agent whose
    /// current profile says <c>images: true</c> and that the server has not refused with <c>images_disabled</c> since
    /// (<see cref="FadenstackApiException.IsImagesDisabled"/>). False in standalone (no pictures there yet) and before
    /// <see cref="StartAsync"/>. An app that keeps its own list of picture tools (the Office add-ins) passes this instead of its own check.
    /// </summary>
    public bool ImagesAvailable =>
        Connection == AgentConnection.Governed && _profile is { Images: true } && !_imagesRefused;

    /// <summary>The server refused a picture with <c>images_disabled</c>: the picture tools are hidden from the next round on.</summary>
    internal void MarkImagesRefused() => _imagesRefused = true;
}

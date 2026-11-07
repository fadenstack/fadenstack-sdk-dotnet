using System.Diagnostics;
using Fadenstack.Agents.Sessions;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

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
    private DateTimeOffset _profileRead;
    private IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private bool _warned;

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
    /// The most any session may use: the narrowest of the profile's ceiling and the agent's highest level, where the server
    /// sets them, else Auto. A level this SDK does not know narrows to read-only: it cannot be taken for none (39 F8).
    /// </summary>
    public ToolMode Ceiling => ToolModes.Narrowest(Level(_profile?.ToolModes.Ceiling), Level(_profile?.ToolModes.Max));

    /// <summary>Whether pictures tools return go to the model: the app allows it and the profile says the model sees.</summary>
    public bool SeesPictures => _settings.Pictures && _profile?.Vision == true;

    /// <summary>
    /// The mode new sessions start in: the agent's default level from the server (Ask from an older one, or one this SDK
    /// does not know), no higher than the app's own default (<see cref="AgentBuilder.UseDefaultToolMode"/>).
    /// <see cref="AgentSession.EffectiveToolMode"/> narrows it to the <see cref="Ceiling"/>.
    /// </summary>
    public ToolMode DefaultToolMode => ToolModes.Narrowest(
        ToolModes.ParseMode(_profile?.ToolModes.Default) ?? ToolMode.Ask, _settings.DefaultMode);

    /// <summary>How old the profile may be when a session starts or opens before it is read again (39 F8).</summary>
    internal static readonly TimeSpan ProfileMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The time, for the profile's age; tests set it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static ToolMode Level(string? level) =>
        string.IsNullOrWhiteSpace(level) ? ToolMode.Auto : ToolModes.ParseMode(level) ?? ToolMode.ReadOnly;

    /// <summary><see cref="PermissionMode.Ask"/>, or <see cref="PermissionMode.Bypass"/> when the app set it.</summary>
    public PermissionMode PermissionMode => _settings.PermissionMode;

    internal AgentSettings Settings => _settings;

    internal EventReporter Reporter { get; }

    internal ISessionStore SessionStore => _sessions ??= _settings.Sessions ?? new FileSessionStore();

    internal IApprovalStore ApprovalStore => _approvalStore ??= _settings.ApprovalStore ?? new FileApprovalStore();

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
            var tools = HostTools(profile);
            var context = new AgentContext(Client, profile, Reporter.ReportNowAsync);
            foreach (var source in _settings.Sources)
            {
                foreach (var tool in await source.GetToolsAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    if (tools.Any(t => t.Name == tool.Name))
                    {
                        continue; // a host tool wins over an MCP tool of the same name
                    }
                    tools.Add(tool);
                }
            }
            _tools = tools;
            _profile = profile;
            if (_settings.PermissionMode == PermissionMode.Bypass && !_warned)
            {
                _warned = true;
                Trace.TraceWarning($"The agent '{Slug}' is set to bypass mode, which this server does not allow: every tool call " +
                    "that would ask still asks. Bypass mode never answers the gateway's own tools.");
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
        var profile = await Client.Agent!.GetProfileAsync(cancellationToken).ConfigureAwait(false);
        var missing = profile.RequiredClientModules.Where(m => !_settings.Modules.Contains(m)).ToList();
        if (missing.Count > 0)
        {
            throw new FadenstackException(
                $"The agent '{Slug}' needs the module(s) {string.Join(", ", missing)}; add them to the builder.");
        }
        _profileRead = Clock();
        return profile;
    }

    /// <summary>The host tools <paramref name="profile"/>'s contract allows, with its class and approval.</summary>
    private List<AgentTool> HostTools(AgentProfile profile)
    {
        var contract = profile.HostToolContract.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var tools = new List<AgentTool>();
        foreach (var tool in _settings.Tools)
        {
            ToolContractEntry? entry = null;
            if (contract.Count > 0 && !contract.TryGetValue(tool.Name, out entry))
            {
                continue; // the gateway refuses it anyway; not offering it keeps the model from trying
            }
            var risk = ToolModes.ParseClass(entry?.Risk);
            tools.Add(new AgentTool(tool.Function, risk == ToolClass.Unknown ? ToolClass.Unknown : AgentTool.Effective(tool.Class, risk),
                tool.Source, tool.Server, tool.OpenWorld) { Describe = tool.Describe, Outputs = tool.Outputs, Approval = entry?.Approval });
        }
        return tools;
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
                Trace.TraceWarning($"The profile of the agent '{Slug}' could not be read again ({e.GetType().Name}); the one held stays.");
                return;
            }
            var hosts = HostTools(profile);
            var own = new HashSet<string>(_settings.Tools.Select(t => t.Name));
            _tools = hosts.Concat(_tools.Where(t => !own.Contains(t.Name))).ToList();
            _profile = profile;
        }
        finally
        {
            _starting.Release();
        }
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

    /// <summary>A new session, in the agent's default mode (<see cref="DefaultToolMode"/>) unless <paramref name="toolMode"/> says otherwise.</summary>
    public async Task<AgentSession> StartSessionAsync(ToolMode? toolMode = null, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
        var session = new AgentSession(this, Guid.NewGuid().ToString("N"), new List<TranscriptEntry>(), toolMode ?? DefaultToolMode);
        await session.WriteStartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>A session from this device's store, to go on with.</summary>
    public async Task<AgentSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
        var entries = await SessionStore.ReadAsync(Slug, sessionId, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw new KeyNotFoundException($"No session '{sessionId}' for '{Slug}' on this device.");
        }
        return new AgentSession(this, sessionId, entries.ToList(), AgentSession.LastMode(entries) ?? DefaultToolMode);
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        SessionStore.ListAsync(Slug, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SessionStore.DeleteAsync(Slug, sessionId, cancellationToken);

    /// <summary>
    /// Forgets what the user allowed always on this device for this agent: every tool, or <paramref name="tool"/> only.
    /// (What a chat allowed ends with that chat: <see cref="AgentSession.ResetApprovals"/>.)
    /// </summary>
    public Task ResetApprovalsAsync(string? tool = null, CancellationToken cancellationToken = default) =>
        ApprovalStore.RemoveAsync(Slug, tool, cancellationToken);

    /// <summary>Sends what is left to report.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Reporter.FlushAsync(cancellationToken);

    public void Dispose()
    {
        foreach (var source in _settings.Sources.OfType<IDisposable>())
        {
            source.Dispose();
        }
        if (_settings.OwnsClient)
        {
            _settings.Client.Dispose();
        }
        _starting.Dispose();
    }

    internal IList<AITool> Offer(ToolMode mode) =>
        _tools.Where(t => ToolModes.Decide(mode, t.Class) != ToolModeDecision.Hidden).Select(t => (AITool)t.Function).ToList();

    internal AgentTool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);
}

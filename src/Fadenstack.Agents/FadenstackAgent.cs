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

    /// <summary>The agent's profile, once <see cref="StartAsync"/> ran.</summary>
    public AgentProfile Profile => _profile ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>The tools the agent runs, with their effective classes, once started.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    /// <summary>The most any session may use: the profile's ceiling, if the server sets one, or Auto.</summary>
    public ToolMode Ceiling => ToolModes.ParseMode(_profile?.ToolModes.Ceiling) ?? ToolMode.Auto;

    /// <summary>Whether pictures tools return go to the model: the app allows it and the profile says the model sees.</summary>
    public bool SeesPictures => _settings.Pictures && _profile?.Vision == true;

    public ToolMode DefaultToolMode => ToolModes.Narrowest(_settings.DefaultMode, ToolModes.ParseMode(_profile?.ToolModes.Default) ?? ToolMode.Ask);

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
            var profile = await Client.Agent!.GetProfileAsync(cancellationToken).ConfigureAwait(false);
            var missing = profile.RequiredClientModules.Where(m => !_settings.Modules.Contains(m)).ToList();
            if (missing.Count > 0)
            {
                throw new FadenstackException(
                    $"The agent '{Slug}' needs the module(s) {string.Join(", ", missing)}; add them to the builder.");
            }
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
                    tool.Source, tool.Server, tool.OpenWorld));
            }
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
                Trace.TraceWarning($"The agent '{Slug}' runs in bypass mode: tool calls that would ask are approved without asking, " +
                    "destructive ones too; an admin's \"Ask first\" rule still asks. Use it only for development and trusted automation.");
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

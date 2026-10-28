using Fadenstack.Agents.Sessions;
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
    private IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private bool _warned;
    private ILogger? _logger;

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
                    tool.Source, tool.Server, tool.OpenWorld) { Describe = tool.Describe, Outputs = tool.Outputs });
            }
            var hostTools = tools.Count;
            var context = new AgentContext(Client, profile, Reporter.ReportNowAsync, _settings.LoggerFactory);
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
            UseDefaultStores(profile.Principal, _settings.Retention.Within(profile.LocalRetention));
            _tools = tools;
            _profile = profile;
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
        _sessions ??= _settings.Sessions is null ? new FileSessionStore(Folder("sessions"), retention: retention) : null;
        _approvalStore ??= _settings.ApprovalStore is null ? new FileApprovalStore(Folder("approvals")) : null;
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

    /// <summary>
    /// Forgets what the user allowed always on this device for this agent: every tool, or <paramref name="tool"/> only.
    /// (What a chat allowed ends with that chat: <see cref="AgentSession.ResetApprovals"/>.)
    /// </summary>
    public async Task ResetApprovalsAsync(string? tool = null, CancellationToken cancellationToken = default)
    {
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
            ? new[] { Path.Combine(FadenstackPaths.DataRoot, "sessions"), Path.Combine(FadenstackPaths.DataRoot, "approvals") }
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

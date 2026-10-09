using Faden.Agents.Sessions;
using Faden.Models;
using Faden.Tools;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// An agent served by a Fadenstack server and run on this device: its profile, its tools, and its sessions.
/// The loop runs here (01-design §5); every model call goes through the gateway.
/// </summary>
public sealed class FadenAgent : IDisposable
{
    private readonly AgentSettings _settings;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private ISessionStore? _sessions;
    private AgentProfile? _profile;
    private IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();

    internal FadenAgent(AgentSettings settings)
    {
        _settings = settings;
        Reporter = new EventReporter(settings.Client, settings.Report);
    }

    public static AgentBuilder CreateBuilder() => new();

    public string Slug => _settings.Agent;

    public FadenClient Client => _settings.Client;

    /// <summary>The agent's profile, once <see cref="StartAsync"/> ran.</summary>
    public AgentProfile Profile => _profile ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>The tools the agent runs, with their effective classes, once started.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    /// <summary>The most any session may use: the profile's ceiling (EE), or Auto.</summary>
    public ToolMode Ceiling => ToolModes.ParseMode(_profile?.ToolModes.Ceiling) ?? ToolMode.Auto;

    /// <summary>Whether pictures tools return go to the model: the app allows it and the profile says the model sees.</summary>
    public bool SeesPictures => _settings.Pictures && _profile?.Vision == true;

    public ToolMode DefaultToolMode => ToolModes.Narrowest(_settings.DefaultMode, ToolModes.ParseMode(_profile?.ToolModes.Default) ?? ToolMode.Ask);

    internal AgentSettings Settings => _settings;

    internal EventReporter Reporter { get; }

    internal ISessionStore SessionStore => _sessions ??= _settings.Sessions ?? new FileSessionStore();

    /// <summary>
    /// Loads the profile, checks the modules the agent requires, and settles the tools: host tools outside the
    /// agent's contract are dropped, the others get the stricter of their own and the contract's class.
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
                throw new FadenException(
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
                tools.Add(new AgentTool(tool.Function, AgentTool.Effective(tool.Class, ToolModes.ParseClass(entry?.Risk)),
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

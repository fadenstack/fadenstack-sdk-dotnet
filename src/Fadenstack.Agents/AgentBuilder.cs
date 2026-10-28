using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Agents;

/// <summary>
/// Puts an agent together: the server, the agent, its host tools, who approves, where sessions live, and
/// the modules. <c>FadenstackAgent.CreateBuilder().UseServer(...).UseAgent("docs-assistant").AddTools(tools).Build()</c>.
/// </summary>
public sealed class AgentBuilder
{
    private readonly List<AgentTool> _tools = new();
    private readonly List<IToolSource> _sources = new();
    private readonly List<(int Order, Func<IChatClient, IChatClient> Wrap)> _chatStages = new();
    private readonly List<(int Order, IToolStage Stage)> _toolStages = new();
    private readonly Dictionary<Type, Func<ToolInvocation, object>> _toolServices = new();
    private readonly List<string> _modules = new();
    private FadenstackClient? _client;
    private FadenstackClientOptions? _options;
    private string? _agent;
    private IApprovalHandler _approvals = DenyAllApprovals.Instance;
    private ISessionStore? _sessions;
    private IApprovalStore? _approvalStore;
    private SessionRetention _retention = SessionRetention.Default;
    private PermissionMode _permissionMode = PermissionMode.Ask;
    private ToolMode _defaultMode = ToolMode.Ask;
    private int _maxRounds = 16;
    private int _historyTokens = 24_000;
    private bool _report = true;
    private ReasoningEffort? _reasoning;
    private bool _pictures = true;
    private AppDescription? _app;
    private ILoggerFactory? _loggerFactory;

    internal AgentBuilder()
    {
    }

    /// <summary>The server, and how the agent signs in (usually an <see cref="AgentTokenProvider"/>).</summary>
    public AgentBuilder UseServer(Uri serverUrl, ITokenProvider credentials, Action<FadenstackClientOptions>? configure = null)
    {
        _options = new FadenstackClientOptions { ServerUrl = serverUrl, Credentials = credentials };
        configure?.Invoke(_options);
        _client = null;
        return this;
    }

    /// <summary>A client made elsewhere; it must be for the agent's route (<see cref="FadenstackClientOptions.Agent"/>).</summary>
    public AgentBuilder UseClient(FadenstackClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _agent ??= client.Options.Agent;
        _options = null;
        return this;
    }

    public AgentBuilder UseAgent(string agent)
    {
        _agent = string.IsNullOrWhiteSpace(agent) ? throw new ArgumentException("An agent slug is required.", nameof(agent)) : agent;
        return this;
    }

    /// <summary>The public methods of <paramref name="target"/> marked <see cref="AgentToolAttribute"/>.</summary>
    public AgentBuilder AddTools(object target)
    {
        _tools.AddRange(AgentTool.From(target));
        return this;
    }

    public AgentBuilder AddTools<T>() where T : new() => AddTools(new T());

    /// <summary>A function as a host tool. Without a class it is <see cref="ToolClass.Unknown"/> and always asks.</summary>
    public AgentBuilder AddTool(AIFunction function, ToolClass toolClass = ToolClass.Unknown)
    {
        _tools.Add(new AgentTool(function, toolClass));
        return this;
    }

    public AgentBuilder AddTool(AgentTool tool)
    {
        _tools.Add(tool ?? throw new ArgumentNullException(nameof(tool)));
        return this;
    }

    public AgentBuilder AddToolSource(IToolSource source)
    {
        _sources.Add(source ?? throw new ArgumentNullException(nameof(source)));
        return this;
    }

    /// <summary>Who asks the user. Without one every call that needs approval is declined.</summary>
    public AgentBuilder UseApprovals(IApprovalHandler handler)
    {
        _approvals = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public AgentBuilder UseApprovals(Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> ask) =>
        UseApprovals(new DelegateApprovals(ask));

    /// <summary>Where sessions live; by default encrypted files under the user's data folder.</summary>
    public AgentBuilder UseSessionStore(ISessionStore store)
    {
        _sessions = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    /// <summary>
    /// How many sessions, and how old, the default session store keeps (default <see cref="SessionRetention.Default"/>). If the
    /// server sets limits too (<see cref="AgentProfile.LocalRetention"/>), the stricter of each applies.
    /// </summary>
    public AgentBuilder UseSessionRetention(SessionRetention retention)
    {
        if (retention is null)
        {
            throw new ArgumentNullException(nameof(retention));
        }
        if (retention.MaxSessions < 1 || retention.MaxAgeDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Both limits must be positive.");
        }
        _retention = retention;
        return this;
    }

    /// <summary>Where "Allow always" is kept; by default encrypted files under the user's data folder.</summary>
    public AgentBuilder UseApprovalStore(IApprovalStore store)
    {
        _approvalStore = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    /// <summary>
    /// <see cref="PermissionMode.Ask"/> (the default): calls the tool mode asks about wait for the user.
    /// <see cref="PermissionMode.Bypass"/>: they are approved without asking, destructive ones too -- for development
    /// and trusted automation, never a user's choice. An admin's "Ask first" rule still asks, and the transcript and
    /// the server's record say <c>bypass</c> for each call it let through. The agent warns once when it starts in bypass.
    /// </summary>
    public AgentBuilder UsePermissionMode(PermissionMode mode)
    {
        _permissionMode = mode is PermissionMode.Ask or PermissionMode.Bypass ? mode : throw new ArgumentOutOfRangeException(nameof(mode));
        return this;
    }

    /// <summary>
    /// The mode new sessions start in. It can only be stricter than Ask: every session starts asking unless the
    /// user widens that session.
    /// </summary>
    public AgentBuilder UseDefaultToolMode(ToolMode mode)
    {
        _defaultMode = ToolModes.Narrowest(ToolMode.Ask, mode);
        return this;
    }

    /// <summary>Model calls per turn before the loop stops (default 16).</summary>
    public AgentBuilder UseMaxRounds(int rounds)
    {
        _maxRounds = rounds < 1 ? throw new ArgumentOutOfRangeException(nameof(rounds)) : rounds;
        return this;
    }

    /// <summary>About how many tokens of history a request carries (default 24 000); older turns are left out.</summary>
    public AgentBuilder UseHistoryBudget(int tokens)
    {
        _historyTokens = tokens < 1 ? throw new ArgumentOutOfRangeException(nameof(tokens)) : tokens;
        return this;
    }

    /// <summary>
    /// How much the model reasons before it answers, on every request (default: the model's own). None asks for
    /// no reasoning: a reasoning model answers at once, which suits quick reads. Models without a setting ignore it.
    /// </summary>
    public AgentBuilder UseReasoning(ReasoningEffort effort)
    {
        _reasoning = effort;
        return this;
    }

    /// <summary>
    /// Whether pictures the tools return (a rendered slide) go to the model. Default on, and then only when the
    /// agent's profile says its model sees them; off, they never do.
    /// </summary>
    public AgentBuilder UsePictures(bool enabled)
    {
        _pictures = enabled;
        return this;
    }

    /// <summary>
    /// Whether tool runs and approvals (names and counts only), and the app's manifest, are reported to the server.
    /// Default on.
    /// </summary>
    public AgentBuilder UseEventReporting(bool enabled)
    {
        _report = enabled;
        return this;
    }

    /// <summary>
    /// The app the agent runs in. When the agent starts it tells the server what the app brings -- its host tools
    /// with their classes, this context and greeting, the versions -- so the server's admins take new tools in with a
    /// click instead of typing them; until they do, the server holds them back.
    /// </summary>
    public AgentBuilder DescribeApp(AppDescription app)
    {
        if (string.IsNullOrWhiteSpace(app?.Name))
        {
            throw new ArgumentException("An app needs a name.", nameof(app));
        }
        _app = app!;
        return this;
    }

    /// <summary>
    /// Where the agent logs, and its client and local MCP servers with it: names, ids, counts, durations and outcomes, never
    /// content (no prompts, answers, reasoning, attachments, tool arguments or results, tokens, secrets or user ids). Spans
    /// and metrics need no factory: subscribe to <see cref="FadenstackTelemetry"/>'s sources. Default: no logging.
    /// </summary>
    public AgentBuilder UseLogging(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>A delegating chat client at <paramref name="order"/> (<see cref="StageOrder"/>).</summary>
    public AgentBuilder AddChatStage(int order, Func<IChatClient, IChatClient> wrap)
    {
        _chatStages.Add((order, wrap ?? throw new ArgumentNullException(nameof(wrap))));
        return this;
    }

    /// <summary>A tool stage at <paramref name="order"/> (<see cref="StageOrder"/>).</summary>
    public AgentBuilder AddToolStage(int order, IToolStage stage)
    {
        _toolStages.Add((order, stage ?? throw new ArgumentNullException(nameof(stage))));
        return this;
    }

    /// <summary>
    /// A service each tool call gets (<see cref="AIFunctionArguments.Services"/>), made by <paramref name="factory"/> the
    /// first time the call or one of its stages asks for it, and the same instance for both. A tool method's
    /// parameter of a type marked <see cref="ToolServiceAttribute"/> is bound to it and left out of the tool's schema;
    /// any tool can also take an <see cref="IServiceProvider"/>. One factory per type: a later one replaces it.
    /// </summary>
    public AgentBuilder AddToolService<T>(Func<ToolInvocation, T> factory) where T : class
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }
        _toolServices[typeof(T)] = invocation => factory(invocation);
        return this;
    }

    public AgentBuilder Use(IFadenstackModule module)
    {
        if (module is null)
        {
            throw new ArgumentNullException(nameof(module));
        }
        if (module.Name is { } name)
        {
            _modules.Add(name);
        }
        module.Configure(this);
        return this;
    }

    public FadenstackAgent Build()
    {
        var agent = _agent ?? throw new InvalidOperationException("Call UseAgent(slug).");
        var client = _client;
        var ownsClient = false;
        if (client is null)
        {
            var options = _options ?? throw new InvalidOperationException("Call UseServer(...) or UseClient(...).");
            options.Agent = agent;
            if (_loggerFactory is not null)
            {
                options.LoggerFactory = _loggerFactory;
            }
            client = new FadenstackClient(options);
            ownsClient = true;
        }
        else if (!string.Equals(client.Options.Agent, agent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The client is for '{client.Options.Agent ?? "(no agent)"}', not '{agent}': one agent per client.");
        }
        var duplicate = _tools.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two tools are named '{duplicate.Key}'.");
        }
        IChatClient chat = client.Chat;
        foreach (var stage in _chatStages.OrderByDescending(s => s.Order))
        {
            chat = stage.Wrap(chat);
        }
        return new FadenstackAgent(new AgentSettings
        {
            Client = client,
            OwnsClient = ownsClient,
            Agent = agent,
            Chat = chat,
            LoggerFactory = _loggerFactory ?? client.Options.LoggerFactory,
            Tools = _tools.ToList(),
            Sources = _sources.ToList(),
            ToolStages = _toolStages.OrderBy(s => s.Order).Select(s => s.Stage).ToList(),
            ToolServices = new Dictionary<Type, Func<ToolInvocation, object>>(_toolServices),
            Modules = _modules.ToList(),
            Approvals = _approvals,
            Sessions = _sessions,
            ApprovalStore = _approvalStore,
            Retention = _retention,
            PermissionMode = _permissionMode,
            DefaultMode = _defaultMode,
            MaxRounds = _maxRounds,
            HistoryTokens = _historyTokens,
            Report = _report,
            Reasoning = _reasoning,
            Pictures = _pictures,
            App = _app ?? new AppDescription { Name = "Fadenstack SDK app" },
            AppDescribed = _app is not null,
        });
    }
}

/// <summary>The app an agent runs in: what its manifest says besides the tools.</summary>
public sealed record AppDescription
{
    /// <summary>"Word add-in", "Viewer add-in".</summary>
    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>What the app offers the agent to read, by id.</summary>
    public IReadOnlyList<ManifestContext> Context { get; init; } = [];

    /// <summary>The greeting and suggestions the app would show.</summary>
    public ManifestUi? Ui { get; init; }
}

internal sealed class AgentSettings
{
    public FadenstackClient Client { get; init; } = null!;
    public bool OwnsClient { get; init; }
    public string Agent { get; init; } = "";
    public IChatClient Chat { get; init; } = null!;

    /// <summary>The builder's, else the client's (a client made elsewhere logs to its own).</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    public List<AgentTool> Tools { get; init; } = new();
    public List<IToolSource> Sources { get; init; } = new();
    public List<IToolStage> ToolStages { get; init; } = new();
    public Dictionary<Type, Func<ToolInvocation, object>> ToolServices { get; init; } = new();
    public List<string> Modules { get; init; } = new();
    public IApprovalHandler Approvals { get; init; } = DenyAllApprovals.Instance;
    public ISessionStore? Sessions { get; init; }
    public IApprovalStore? ApprovalStore { get; init; }
    public SessionRetention Retention { get; init; } = SessionRetention.Default;
    public PermissionMode PermissionMode { get; init; }
    public ToolMode DefaultMode { get; init; } = ToolMode.Ask;
    public int MaxRounds { get; init; }
    public int HistoryTokens { get; init; }
    public bool Report { get; init; }
    public ReasoningEffort? Reasoning { get; init; }
    public bool Pictures { get; init; } = true;
    public AppDescription App { get; init; } = new();

    /// <summary>The host described <see cref="App"/> (<see cref="AgentBuilder.DescribeApp"/>); otherwise it is the SDK's default.</summary>
    public bool AppDescribed { get; init; }
}

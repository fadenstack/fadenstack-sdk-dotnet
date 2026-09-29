using Faden.Agents.Sessions;
using Faden.Auth;
using Faden.Tools;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// Puts an agent together: the server, the agent, its host tools, who approves, where sessions live, and
/// the modules. <c>FadenAgent.CreateBuilder().UseServer(...).UseAgent("eplan-assistant").AddTools(tools).Build()</c>.
/// </summary>
public sealed class AgentBuilder
{
    private readonly List<AgentTool> _tools = new();
    private readonly List<IToolSource> _sources = new();
    private readonly List<(int Order, Func<IChatClient, IChatClient> Wrap)> _chatStages = new();
    private readonly List<(int Order, IToolStage Stage)> _toolStages = new();
    private readonly List<string> _modules = new();
    private FadenClient? _client;
    private FadenClientOptions? _options;
    private string? _agent;
    private IApprovalHandler _approvals = DenyAllApprovals.Instance;
    private ISessionStore? _sessions;
    private ToolMode _defaultMode = ToolMode.Ask;
    private int _maxRounds = 16;
    private int _historyTokens = 24_000;
    private bool _report = true;

    internal AgentBuilder()
    {
    }

    /// <summary>The server, and how the agent signs in (usually an <see cref="AgentTokenProvider"/>).</summary>
    public AgentBuilder UseServer(Uri serverUrl, ITokenProvider credentials, Action<FadenClientOptions>? configure = null)
    {
        _options = new FadenClientOptions { ServerUrl = serverUrl, Credentials = credentials };
        configure?.Invoke(_options);
        _client = null;
        return this;
    }

    /// <summary>A client made elsewhere; it must be for the agent's route (<see cref="FadenClientOptions.Agent"/>).</summary>
    public AgentBuilder UseClient(FadenClient client)
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
    /// The mode new sessions start in. It can only be stricter than Ask: every session starts asking unless the
    /// user widens that session (sdk/07 §1).
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

    /// <summary>Whether tool runs and approvals are reported to the server (names and counts only). Default on.</summary>
    public AgentBuilder UseEventReporting(bool enabled)
    {
        _report = enabled;
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

    public AgentBuilder Use(IFadenModule module)
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

    public FadenAgent Build()
    {
        var agent = _agent ?? throw new InvalidOperationException("Call UseAgent(slug).");
        var client = _client;
        var ownsClient = false;
        if (client is null)
        {
            var options = _options ?? throw new InvalidOperationException("Call UseServer(...) or UseClient(...).");
            options.Agent = agent;
            client = new FadenClient(options);
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
        return new FadenAgent(new AgentSettings
        {
            Client = client,
            OwnsClient = ownsClient,
            Agent = agent,
            Chat = chat,
            Tools = _tools.ToList(),
            Sources = _sources.ToList(),
            ToolStages = _toolStages.OrderBy(s => s.Order).Select(s => s.Stage).ToList(),
            Modules = _modules.ToList(),
            Approvals = _approvals,
            Sessions = _sessions,
            DefaultMode = _defaultMode,
            MaxRounds = _maxRounds,
            HistoryTokens = _historyTokens,
            Report = _report,
        });
    }
}

internal sealed class AgentSettings
{
    public FadenClient Client { get; init; } = null!;
    public bool OwnsClient { get; init; }
    public string Agent { get; init; } = "";
    public IChatClient Chat { get; init; } = null!;
    public List<AgentTool> Tools { get; init; } = new();
    public List<IToolSource> Sources { get; init; } = new();
    public List<IToolStage> ToolStages { get; init; } = new();
    public List<string> Modules { get; init; } = new();
    public IApprovalHandler Approvals { get; init; } = DenyAllApprovals.Instance;
    public ISessionStore? Sessions { get; init; }
    public ToolMode DefaultMode { get; init; } = ToolMode.Ask;
    public int MaxRounds { get; init; }
    public int HistoryTokens { get; init; }
    public bool Report { get; init; }
}

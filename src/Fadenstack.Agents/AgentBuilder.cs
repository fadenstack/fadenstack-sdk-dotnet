using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
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
    private readonly List<(int Order, IAgentHooks Hooks)> _hooks = new();
    private bool _hookContent;
    private AgentOpenTelemetryOptions? _openTelemetry;
    private readonly Dictionary<Type, Func<ToolInvocation, object>> _toolServices = new();
    private Func<FadenstackAgent, IAnswerCopyMarker>? _copyMarker;
    private readonly List<string> _modules = new();
    private FadenstackClient? _client;
    private FadenstackClientOptions? _options;
    private ModelEndpoint? _endpoint;
    private EndpointReport? _endpointReport;
    private IChatClient? _chatClient;
    private AgentDefinition? _definition;
    private string? _ownInstructions;
    private LocalProfileOptions? _localOptions;
    private MachinePolicy _policy = MachinePolicy.None;
    private string? _agent;
    private IApprovalHandler _approvals = DenyAllApprovals.Instance;
    private ISessionStore? _sessions;
    private IApprovalStore? _approvalStore;
    private SessionRetention _retention = SessionRetention.Default;
    private PermissionMode _permissionMode = PermissionMode.Ask;
    private bool _requireHostToolPolicyChecks;
    private IStepUpHandler? _stepUp;
    private ToolMode? _defaultMode;
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

    /// <summary>
    /// Standalone mode (sdk/51 F03): the person's own OpenAI-compatible endpoint, with no Fadenstack server. Needs
    /// <see cref="UseLocalProfile"/>; never together with <see cref="UseServer"/> or <see cref="UseClient"/>. The endpoint
    /// check's <paramref name="report"/>, if the app kept one, says whether the model sees pictures.
    /// </summary>
    public AgentBuilder UseEndpoint(ModelEndpoint endpoint, EndpointReport? report = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _endpointReport = report;
        return this;
    }

    /// <summary>
    /// Standalone mode with any <see cref="IChatClient"/> as the model, instead of <see cref="UseEndpoint"/>: the host's own
    /// client (an SDK of a provider, a test's), which stays the host's. Needs <see cref="UseLocalProfile"/>.
    /// </summary>
    public AgentBuilder UseChatClient(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        return this;
    }

    /// <summary>
    /// The agent's definition for standalone mode (the app's own <c>contracts/*.json</c>): its slug, instructions, host tool
    /// contract and greeting. The person's own instructions, if any, come after the definition's (D7). The person's settings
    /// (<paramref name="options"/>) say whether local MCP servers and skills are on.
    /// </summary>
    public AgentBuilder UseLocalProfile(AgentDefinition definition, string? ownInstructions = null, LocalProfileOptions? options = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _ownInstructions = ownInstructions;
        _localOptions = options;
        return this;
    }

    /// <summary>
    /// What IT set for this machine (sdk/51 F09): <c>Build()</c> refuses an endpoint the policy forbids or does not list, takes
    /// away the plain-http opt-in where it forbids plain http, and narrows local MCP and skills, with a server too. The app
    /// reads the policy from its managed location (<see cref="MachinePolicy.Load"/>) and locks what it sets.
    /// </summary>
    public AgentBuilder UseMachinePolicy(MachinePolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
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
        foreach (var tool in AgentTool.From(target))
        {
            Add(tool);
        }
        return this;
    }

    public AgentBuilder AddTools<T>() where T : new() => AddTools(new T());

    /// <summary>
    /// A function as a host tool. Without a class it is <see cref="ToolClass.Unknown"/> and always asks. <paramref name="makesPictures"/>
    /// says it asks the image model for a picture (<see cref="AgentTool.MakesPictures"/>).
    /// </summary>
    public AgentBuilder AddTool(AIFunction function, ToolClass toolClass = ToolClass.Unknown, bool makesPictures = false)
    {
        Add(new AgentTool(function, toolClass) { MakesPictures = makesPictures });
        return this;
    }

    public AgentBuilder AddTool(AgentTool tool)
    {
        Add(tool ?? throw new ArgumentNullException(nameof(tool)));
        return this;
    }

    /// <summary>An app's tool: never under a name the SDK keeps for itself (<see cref="AgentTool.ReservedNames"/>).</summary>
    private void Add(AgentTool tool)
    {
        if (AgentTool.IsReserved(tool.Name))
        {
            throw new ArgumentException(
                $"'{tool.Name}' is a name the SDK keeps for its own tools ({string.Join(", ", AgentTool.ReservedNames)}), so an app's tool cannot take it: name it differently.",
                nameof(tool));
        }
        _tools.Add(tool);
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
    /// <see cref="PermissionMode.Bypass"/>: sessions run in Bypass where the server offers it for the agent -- the SDK
    /// makes the conversation's Bypass grant itself, without a card, and the server records every call (sdk/40 A3) --
    /// for development and trusted automation, never a user's choice. Where the server does not offer it, every call
    /// still asks, and the agent warns once when it starts. An admin's "Ask first" rule always asks.
    /// </summary>
    public AgentBuilder UsePermissionMode(PermissionMode mode)
    {
        _permissionMode = mode is PermissionMode.Ask or PermissionMode.Bypass ? mode : throw new ArgumentOutOfRangeException(nameof(mode));
        return this;
    }

    /// <summary>
    /// Requires the server's fresh dispatch checks for host tools (<c>host_tools.dispatch_check</c>): the agent refuses to
    /// start against a server that does not advertise them, and never falls back to cached policy. A server that advertises
    /// them is used without this call; once seen, losing them fails closed (see <c>docs/host-commands.md</c>).
    /// </summary>
    public AgentBuilder RequireHostToolPolicyChecks()
    {
        _requireHostToolPolicyChecks = true;
        return this;
    }

    /// <summary>
    /// Who opens the second sign-in before Bypass is switched on (sdk/41): the link in the system browser, and what the
    /// person sees meanwhile. A chat's approval broker does it already; an app set to <see cref="PermissionMode.Bypass"/>
    /// without a UI gives its own (<see cref="SystemBrowser"/> opens the page). Without one, no Bypass: the app runs in Auto.
    /// </summary>
    public AgentBuilder UseStepUpHandler(IStepUpHandler handler)
    {
        _stepUp = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// The highest mode new sessions start in. Sessions start in the agent's default level from the server (Ask unless its
    /// author set another); this can only narrow it, never widen it -- for an app that never starts in Auto, say.
    /// </summary>
    public AgentBuilder UseDefaultToolMode(ToolMode mode)
    {
        _defaultMode = mode;
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
    /// click instead of typing them; until they do, the server holds them back. (An agent with no contract at all holds
    /// nothing back: every tool is unclassified then, and asks every time.)
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
    /// Adds hooks (<see cref="IAgentHooks"/>): code of the app that sees each model call and each tool call, and may make a tool call
    /// stricter. Before-hooks run in ascending <paramref name="order"/>, then in the order added; after-hooks in the reverse order.
    /// A hook can never approve a call the policy asks about or refuses (<see cref="ToolHookVerdict"/> has no such answer), and one that
    /// throws is logged and ignored. See docs/interop.md.
    /// </summary>
    public AgentBuilder AddHooks(IAgentHooks hooks, int order = 0)
    {
        _hooks.Add((order, hooks ?? throw new ArgumentNullException(nameof(hooks))));
        return this;
    }

    /// <summary>
    /// Whether the model-call hooks see the messages and the answer text (<see cref="ModelCallContext.Messages"/>,
    /// <see cref="ModelCallResult.Text"/>). Off by default: hooks see names, ids, counts, outcomes and times only.
    /// </summary>
    public AgentBuilder UseHookContent(bool enabled = true)
    {
        _hookContent = enabled;
        return this;
    }

    /// <summary>
    /// Switches OpenTelemetry on (off by default): model calls through Microsoft.Extensions.AI's OpenTelemetry chat client (source
    /// <see cref="AgentOpenTelemetryOptions.ModelSourceName"/>), and GenAI attributes on the SDK's <c>invoke_agent</c> and <c>execute_tool</c> spans
    /// (<see cref="FadenstackTelemetry.AgentsSource"/>). No exporter is bundled: the app wires OTLP or anything else, and until it does
    /// nothing leaves the process. No prompt or answer content unless <see cref="AgentOpenTelemetryOptions.EnableSensitiveData"/> is set. The
    /// GenAI conventions are in "Development" status and may change.
    /// </summary>
    public AgentBuilder UseOpenTelemetry(Action<AgentOpenTelemetryOptions>? configure = null)
    {
        var options = new AgentOpenTelemetryOptions();
        configure?.Invoke(options);
        _openTelemetry = options;
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

    /// <summary>
    /// Who marks what the person copies out of a chat (<see cref="FadenstackAgent.MarkCopyAsync"/>): <paramref name="create"/> is
    /// given the agent when it is first needed. <c>Fadenstack.Provenance</c> sets it; one set later replaces it. TS: <c>useAnswerCopyMarker</c>.
    /// </summary>
    public AgentBuilder UseAnswerCopyMarker(Func<FadenstackAgent, IAnswerCopyMarker> create)
    {
        _copyMarker = create ?? throw new ArgumentNullException(nameof(create));
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
        var standalone = _endpoint is not null || _chatClient is not null;
        if (standalone && (_client is not null || _options is not null))
        {
            throw new InvalidOperationException(
                "An agent talks to a Fadenstack server (UseServer, UseClient) or to an endpoint (UseEndpoint, UseChatClient), never both.");
        }
        IAgentBackend backend;
        string agent;
        string? instructions = null;
        ILoggerFactory? clientLogging = null;
        if (standalone)
        {
            if (_endpoint is not null && _chatClient is not null)
            {
                throw new InvalidOperationException("Give the agent one model: UseEndpoint or UseChatClient, not both.");
            }
            var definition = _definition ?? throw new InvalidOperationException(
                "An agent on an endpoint needs its definition: call UseLocalProfile(AgentDefinition.FromJson(...)).");
            if (_agent is not null && !string.Equals(_agent, definition.Slug, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The definition is for '{definition.Slug}', not '{_agent}': in standalone mode the slug comes from the definition.");
            }
            agent = definition.Slug;
            if (_policy.Standalone != PolicyAccess.Allowed)
            {
                throw new MachinePolicyException("standalone",
                    "This computer's policy does not allow an agent on its own model endpoint: connect to the organisation's server.");
            }
            if (_endpoint is not null && !_policy.AllowsEndpoint(_endpoint.BaseUrl))
            {
                throw new MachinePolicyException("endpoints", "This computer's policy does not list this endpoint.");
            }
            if (_endpoint is not null && string.IsNullOrWhiteSpace(_endpoint.Model))
            {
                throw new InvalidOperationException("The endpoint needs a model (ModelEndpoint.Model): say which one the agent uses.");
            }
            if (_requireHostToolPolicyChecks)
            {
                throw new InvalidOperationException(
                    "RequireHostToolPolicyChecks needs a Fadenstack server: an agent on its own endpoint has no server to check with.");
            }
            if (_chatClient is not null && _policy.Endpoints is not null)
            {
                throw new MachinePolicyException("endpoints",
                    "This computer's policy allows only listed endpoints, and a host's own chat client names none.");
            }
            AssertNoDuplicateTools();
            var endpoint = _endpoint is null || _loggerFactory is null || _endpoint.LoggerFactory is not null
                ? _endpoint : _endpoint with { LoggerFactory = _loggerFactory };
            if (endpoint is not null && _policy.PlainHttp != PolicyAccess.Allowed)
            {
                endpoint = endpoint with { AllowPlainHttpInLan = false, PlainHttpForbidden = true };
            }
            var local = _localOptions ?? new LocalProfileOptions();
            local = local with
            {
                LocalMcp = local.LocalMcp && _policy.Mcp.AllowsAny,
                LocalSkills = local.LocalSkills && _policy.Skills.AllowsAny,
            };
            // An endpoint's client is the agent's (disposed with it); a host's own chat client stays the host's.
            backend = new LocalAgentBackend(definition, _chatClient ?? new DirectChatClient(endpoint!), ownsChat: _chatClient is null,
                endpoint, _endpointReport, local);
            // Marking by default as far as the layering allows (owner, 14 Nov): the agent package cannot load the provenance
            // module, so it says so once, here, when a standalone agent goes without it.
            if (!_modules.Contains(ProvenanceModuleName))
            {
                Log.NoProvenanceModule((_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(Log.Category), agent);
            }
            // D7: the person's own instructions come after the definition's.
            instructions = string.Join("\n\n", new[] { definition.Instructions, _ownInstructions?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        }
        else
        {
            if (_definition is not null)
            {
                throw new InvalidOperationException(
                    "UseLocalProfile is for an agent on an endpoint: with a server the agent's definition is the server's.");
            }
            agent = _agent ?? throw new InvalidOperationException("Call UseAgent(slug).");
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
            // The backend owns the client it was given when the builder made it (disposed with the agent).
            backend = new ServerAgentBackend(client, ownsClient);
            clientLogging = client.Options.LoggerFactory;
            AssertNoDuplicateTools();
        }
        IChatClient chat = backend.ChatClient; // the model, under the chat stages
        if (_openTelemetry is { } telemetry)
        {
            // Innermost: it sees what goes out after the stages (masking, marking), not what they were given.
            chat = new OpenTelemetryChatClient(chat, sourceName: telemetry.ModelSourceName) { EnableSensitiveData = telemetry.EnableSensitiveData };
        }
        var hooks = _hooks.Select((h, i) => (h.Order, Index: i, h.Hooks)).ToList();
        if (_openTelemetry is { } traced)
        {
            hooks.Add((int.MinValue, -1, new OpenTelemetryHooks(traced.EnableSensitiveData)));
        }
        foreach (var stage in _chatStages.OrderByDescending(s => s.Order))
        {
            chat = stage.Wrap(chat);
        }
        return new FadenstackAgent(new AgentSettings
        {
            Backend = backend,
            Agent = agent,
            Chat = chat,
            Instructions = string.IsNullOrEmpty(instructions) ? null : instructions,
            MachinePolicy = _policy,
            LoggerFactory = _loggerFactory ?? clientLogging ?? NullLoggerFactory.Instance,
            Tools = _tools.ToList(),
            Sources = _sources.ToList(),
            ToolStages = _toolStages.OrderBy(s => s.Order).Select(s => s.Stage).ToList(),
            Hooks = hooks.OrderBy(h => h.Order).ThenBy(h => h.Index).Select(h => h.Hooks).ToList(),
            HookContent = _hookContent,
            ToolServices = new Dictionary<Type, Func<ToolInvocation, object>>(_toolServices),
            CopyMarker = _copyMarker,
            Modules = _modules.ToList(),
            Approvals = _approvals,
            Sessions = _sessions,
            ApprovalStore = _approvalStore,
            Retention = _retention,
            PermissionMode = _permissionMode,
            RequireHostToolPolicyChecks = _requireHostToolPolicyChecks,
            StepUpHandler = _stepUp,
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

    /// <summary>The name <c>Fadenstack.Provenance</c>'s module registers with (<c>ProvenanceModule.ModuleName</c>).</summary>
    internal const string ProvenanceModuleName = "provenance";

    private void AssertNoDuplicateTools()
    {
        var duplicate = _tools.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two tools are named '{duplicate.Key}'.");
        }
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
    /// <summary>What the agent talks to (sdk/51 F02); it owns the client it connects with.</summary>
    public IAgentBackend Backend { get; init; } = null!;
    public string Agent { get; init; } = "";
    public IChatClient Chat { get; init; } = null!;

    /// <summary>
    /// A standalone agent's instructions, its definition's then the person's own (D7); null with a server, whose gateway adds
    /// the agent's own. The session sets them as the system message, above the chat stages.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>What IT set for this machine; <see cref="MachinePolicy.None"/> when the app gave none.</summary>
    public MachinePolicy MachinePolicy { get; init; } = MachinePolicy.None;

    /// <summary>About how many tokens <see cref="Instructions"/> take (4 characters a token): the history budget leaves room.</summary>
    public int InstructionsTokens => Instructions is null ? 0 : (Instructions.Length + 3) / 4;

    /// <summary>The builder's, else the client's (a client made elsewhere logs to its own).</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    public List<AgentTool> Tools { get; init; } = new();
    public List<IToolSource> Sources { get; init; } = new();
    public List<IToolStage> ToolStages { get; init; } = new();
    public List<IAgentHooks> Hooks { get; init; } = new();
    public bool HookContent { get; init; }
    public Dictionary<Type, Func<ToolInvocation, object>> ToolServices { get; init; } = new();
    public Func<FadenstackAgent, IAnswerCopyMarker>? CopyMarker { get; init; }
    public List<string> Modules { get; init; } = new();
    public IApprovalHandler Approvals { get; init; } = DenyAllApprovals.Instance;

    /// <summary>Who draws the grant card: the approval handler, when it draws those too.</summary>
    public IGrantHandler? Grants => Approvals as IGrantHandler;

    /// <summary>Who opens the second sign-in for Bypass (sdk/41): the app's own, else the approval handler when it does.</summary>
    public IStepUpHandler? StepUpHandler { get; init; }

    public IStepUpHandler? StepUp => StepUpHandler ?? Approvals as IStepUpHandler;
    public ISessionStore? Sessions { get; init; }
    public IApprovalStore? ApprovalStore { get; init; }
    public SessionRetention Retention { get; init; } = SessionRetention.Default;
    public PermissionMode PermissionMode { get; init; }
    public bool RequireHostToolPolicyChecks { get; init; }
    /// <summary>The app's cap on the server's default level; null: none.</summary>
    public ToolMode? DefaultMode { get; init; }
    public int MaxRounds { get; init; }
    public int HistoryTokens { get; init; }
    public bool Report { get; init; }
    public ReasoningEffort? Reasoning { get; init; }
    public bool Pictures { get; init; } = true;
    public AppDescription App { get; init; } = new();

    /// <summary>The host described <see cref="App"/> (<see cref="AgentBuilder.DescribeApp"/>); otherwise it is the SDK's default.</summary>
    public bool AppDescribed { get; init; }
}

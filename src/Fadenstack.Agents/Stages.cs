using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// Where a stage sits: the builder owns the order. Outgoing: context → local PII masking → content provenance →
/// transport. Tool call: approval (the loop) → unmask arguments → check the content the tool places is marked → run →
/// mask the result. A lower number is further from the transport; modules pick their slot, not their call order.
/// </summary>
public static class StageOrder
{
    public const int Context = 100;
    public const int Pii = 200;

    /// <summary>Content marking (<c>Fadenstack.Provenance</c>): the turn's receipts, and the check that tools mark what they place.</summary>
    public const int Provenance = 300;

    public const int Custom = 500;
}

/// <summary>One tool call on its way to the tool.</summary>
public sealed class ToolInvocation
{
    private readonly Func<string, object, Task>? _record;
    private readonly Func<Governance.GovernanceNotice, Task>? _notice;

    internal ToolInvocation(AgentTool tool, FunctionCallContent call, AIFunctionArguments arguments, string sessionId, string turnId,
        FadenstackAgent? agent = null, Func<string, object, Task>? record = null, Func<Governance.GovernanceNotice, Task>? notice = null)
    {
        Tool = tool;
        Call = call;
        Arguments = arguments;
        SessionId = sessionId;
        TurnId = turnId;
        Agent = agent;
        _record = record;
        _notice = notice;
    }

    public AgentTool Tool { get; }

    public FunctionCallContent Call { get; }

    /// <summary>
    /// The arguments the tool gets; a stage may change them (e.g. put masked values back). Their
    /// <see cref="AIFunctionArguments.Services"/> are the call's tool services (<see cref="AgentBuilder.AddToolService{T}"/>).
    /// </summary>
    public AIFunctionArguments Arguments { get; }

    public ToolClass Class => Tool.Class;

    public string SessionId { get; }

    public string TurnId { get; }

    /// <summary>The agent the call runs in: its client, its profile; null outside a session.</summary>
    public FadenstackAgent? Agent { get; }

    /// <summary>A tool service of this call (<see cref="AgentBuilder.AddToolService{T}"/>): the same instance the tool gets; null when none is added.</summary>
    public T? GetService<T>() where T : class => Arguments.Services?.GetService(typeof(T)) as T;

    /// <summary>
    /// Writes an entry of <paramref name="type"/> (a name of the module's own, such as <c>provenance.placed</c>) into the
    /// session's transcript, in this call's turn: names, counts and outcomes, never more than the call itself carries.
    /// The SDK's own types (<see cref="Sessions.EntryTypes"/>) are not taken: they make the history the model gets.
    /// </summary>
    public Task RecordAsync(string type, object data)
    {
        if (string.IsNullOrWhiteSpace(type) || Sessions.EntryTypes.All.Contains(type))
        {
            throw new ArgumentException($"'{type}' is not an entry type a stage or tool service may write.", nameof(type));
        }
        return _record is null ? Task.CompletedTask : _record(type, data ?? throw new ArgumentNullException(nameof(data)));
    }

    /// <summary>Tells the user something about this call: the turn's notices, the transcript, a <see cref="NoticeRaised"/> event.</summary>
    public Task NoticeAsync(Governance.GovernanceNotice notice) =>
        _notice is null ? Task.CompletedTask : _notice(notice ?? throw new ArgumentNullException(nameof(notice)));
}

public delegate Task<object?> ToolInvoker(ToolInvocation invocation, CancellationToken cancellationToken);

/// <summary>Wraps a tool call: sees the arguments before, the result after.</summary>
public interface IToolStage
{
    Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken);
}

/// <summary>Adds tools once the agent's profile is known, e.g. local MCP servers under the agent's policy.</summary>
public interface IToolSource
{
    Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken);
}

/// <summary>What a tool source or module can see of the running agent.</summary>
public sealed class AgentContext
{
    internal AgentContext(FadenstackClient client, Models.AgentProfile profile, Func<IReadOnlyList<Models.AgentClientEvent>, Task> report)
    {
        Client = client;
        Profile = profile;
        _report = report;
    }

    private readonly Func<IReadOnlyList<Models.AgentClientEvent>, Task> _report;

    public FadenstackClient Client { get; }

    public Models.AgentProfile Profile { get; }

    /// <summary>Reports an event to the server (names and counts, never content).</summary>
    public Task ReportAsync(Models.AgentClientEvent clientEvent) => _report(new[] { clientEvent });
}

/// <summary>
/// A package that plugs into the agent, such as <c>Fadenstack.Mcp</c> or an extension's local PII masking. It is active
/// only when referenced and added to the builder, never by being installed.
/// </summary>
public interface IFadenstackModule
{
    /// <summary>The name an agent profile's <c>required_client_modules</c> uses (<c>pii.local</c>), or null.</summary>
    string? Name { get; }

    void Configure(AgentBuilder builder);
}

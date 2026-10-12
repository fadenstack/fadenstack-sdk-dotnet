using Faden.Tools;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// Where a stage sits: the builder owns the order. Outgoing: context → local PII masking →
/// transport. Tool call: approval (the loop) → unmask arguments → run → mask the result. A lower number is
/// further from the transport; modules pick their slot, not their call order.
/// </summary>
public static class StageOrder
{
    public const int Context = 100;
    public const int Pii = 200;
    public const int Custom = 500;
}

/// <summary>One tool call on its way to the tool.</summary>
public sealed class ToolInvocation
{
    internal ToolInvocation(AgentTool tool, FunctionCallContent call, AIFunctionArguments arguments, string sessionId, string turnId)
    {
        Tool = tool;
        Call = call;
        Arguments = arguments;
        SessionId = sessionId;
        TurnId = turnId;
    }

    public AgentTool Tool { get; }

    public FunctionCallContent Call { get; }

    /// <summary>The arguments the tool gets; a stage may change them (e.g. put masked values back).</summary>
    public AIFunctionArguments Arguments { get; }

    public ToolClass Class => Tool.Class;

    public string SessionId { get; }

    public string TurnId { get; }
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
    internal AgentContext(FadenClient client, Models.AgentProfile profile, Func<IReadOnlyList<Models.AgentClientEvent>, Task> report)
    {
        Client = client;
        Profile = profile;
        _report = report;
    }

    private readonly Func<IReadOnlyList<Models.AgentClientEvent>, Task> _report;

    public FadenClient Client { get; }

    public Models.AgentProfile Profile { get; }

    /// <summary>Reports an event to the server (names and counts, never content).</summary>
    public Task ReportAsync(Models.AgentClientEvent clientEvent) => _report(new[] { clientEvent });
}

/// <summary>
/// A package that plugs into the agent: <c>Faden.Mcp</c>, <c>Faden.Enterprise</c>'s local PII. It is active
/// only when referenced and added to the builder, never by being installed.
/// </summary>
public interface IFadenModule
{
    /// <summary>The name an agent profile's <c>required_client_modules</c> uses (<c>pii.local</c>), or null.</summary>
    string? Name { get; }

    void Configure(AgentBuilder builder);
}

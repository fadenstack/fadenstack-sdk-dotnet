using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fadenstack.Agents;

/// <summary>
/// The agent's spans and metrics (<see cref="FadenstackTelemetry"/> names them). Attribute names follow the OpenTelemetry GenAI
/// conventions where one exists; tags are names, ids, counts and outcomes, never content.
/// </summary>
internal static class AgentTelemetry
{
    internal static readonly ActivitySource Source = new(FadenstackTelemetry.AgentsSource, SdkInfo.Version);

    private static readonly Meter Meter = new(FadenstackTelemetry.AgentsMeter, SdkInfo.Version);

    /// <summary>Turns that ended, by <c>outcome</c> (the turn's status).</summary>
    internal static readonly Counter<long> Turns =
        Meter.CreateCounter<long>("fadenstack.agent.turns", description: "Turns that ended, by outcome.");

    /// <summary>Tool calls that ended, by <c>class</c> and <c>outcome</c>.</summary>
    internal static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("fadenstack.agent.tool_calls", description: "Tool calls that ended, by tool class and outcome.");

    /// <summary>How long tools ran, not counting the time the user took to approve.</summary>
    internal static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>("fadenstack.agent.tool.duration", unit: "ms", description: "How long tool calls ran.");

    /// <summary>How long users took to answer an approval.</summary>
    internal static readonly Histogram<double> ApprovalWait =
        Meter.CreateHistogram<double>("fadenstack.agent.approval.wait", unit: "ms", description: "How long approvals waited for the user.");
}

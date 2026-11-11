using System.Diagnostics;

namespace Fadenstack;

/// <summary>
/// The names of the SDK's traces and metrics, for a host that subscribes (OpenTelemetry: <c>AddSource(...)</c>,
/// <c>AddMeter(...)</c>). Tags carry names, ids, counts and outcomes -- never content.
/// </summary>
public static class FadenstackTelemetry
{
    /// <summary>The spans of the client's HTTP requests.</summary>
    public const string ClientSource = "Fadenstack.Client";

    /// <summary>The spans of turns, model requests and tool calls.</summary>
    public const string AgentsSource = "Fadenstack.Agents";

    /// <summary>The counters and histograms of turns, tool calls and approvals.</summary>
    public const string AgentsMeter = "Fadenstack.Agents";

    internal static readonly ActivitySource Client = new(ClientSource, SdkInfo.Version);
}

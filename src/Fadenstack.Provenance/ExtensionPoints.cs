using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>What an edit classifier sees: the text the edit started from, the text it placed, and the kind declared.</summary>
public sealed record EditInput(string? Original, string? Output, EditKind Declared, string MediaType);

/// <summary>An edit classifier's answer: the kind, and why (required to lower a kind to one that is exempt).</summary>
public sealed record EditDecision(EditKind Kind, string? Reason = null);

/// <summary>
/// Decides, for a domain, what kind of edit a placement is (<c>corrected</c> or <c>rewritten</c>). It may lower a
/// marked kind to an exempt one only with a reason, which is recorded; without one the declared kind stays.
/// </summary>
public interface IEditClassifier
{
    EditDecision Classify(EditInput input);
}

/// <summary>What an enricher knows about a placement: never its content.</summary>
public sealed record EnrichmentContext(string Tool, SinkInfo Sink, string MediaType, EditKind EditKind, ToolInvocation? Call);

/// <summary>
/// Adds the integrator's own facts to a placement, under its namespace (<c>com.example</c>: keys
/// <c>com.example.matter</c>): a reference such as a matter number, never anything about a person. The facts go to
/// the markers (a host may keep them beside the record), the transcript and the observers; they cannot change the
/// record. A fact under another namespace, or one that looks like personal data (an e-mail address, a phone number,
/// an IBAN), is dropped and recorded as dropped.
/// </summary>
public interface IRecordEnricher
{
    /// <summary>A reverse domain name of the integrator's own (<c>com.example</c>).</summary>
    string Namespace { get; }

    IReadOnlyDictionary<string, string> Enrich(EnrichmentContext context);
}

/// <summary>Receives each placement report, for the integrator's own audit or telemetry. Read-only; a failure is ignored.</summary>
public interface IPlacementObserver
{
    Task OnPlacedAsync(PlacementReport report, Placement placement, CancellationToken cancellationToken);
}

/// <summary>
/// Where policies come from, records are signed and placements are reported: the Fadenstack server
/// (<see cref="ServerProvenanceBackend"/>), or a replacement for tests or an organisation's own signing service.
/// </summary>
public interface IProvenanceBackend
{
    Task<ProvenancePolicy> GetPolicyAsync(FadenstackAgent agent, CancellationToken cancellationToken);

    Task<SignedRecord> SignRecordAsync(FadenstackAgent agent, RecordRequest request, CancellationToken cancellationToken);

    Task ReportPlacementAsync(FadenstackAgent agent, PlacementReport report, CancellationToken cancellationToken);
}

/// <summary>The agent's Fadenstack server: <c>/v1/agents/{agent}/provenance/...</c> with the agent's token.</summary>
public sealed class ServerProvenanceBackend : IProvenanceBackend
{
    public static ServerProvenanceBackend Instance { get; } = new();

    private static Resources.AgentProvenanceResource Routes(FadenstackAgent agent) =>
        agent.Client.Agent?.Provenance ?? throw new InvalidOperationException("The agent's client has no agent.");

    public Task<ProvenancePolicy> GetPolicyAsync(FadenstackAgent agent, CancellationToken cancellationToken) =>
        Routes(agent).GetPolicyAsync(cancellationToken);

    public Task<SignedRecord> SignRecordAsync(FadenstackAgent agent, RecordRequest request, CancellationToken cancellationToken) =>
        Routes(agent).SignRecordAsync(request, cancellationToken);

    public Task ReportPlacementAsync(FadenstackAgent agent, PlacementReport report, CancellationToken cancellationToken) =>
        Routes(agent).ReportPlacementAsync(report, cancellationToken);
}

using Fadenstack.Http;
using Fadenstack.Models;

namespace Fadenstack.Resources;

/// <summary>
/// Content marking for the client's agent: the policy it follows, signed records of what it places, and reports of
/// how each placement went. Only hashes and names go to the server, never the content. A server with the
/// provenance module off answers 503 (<c>provenance_unavailable</c>); one from before the module, 404.
/// </summary>
public sealed class AgentProvenanceResource
{
    private readonly FadenstackTransport _transport;
    private readonly string _agent;

    internal AgentProvenanceResource(FadenstackTransport transport, string agent)
    {
        _transport = transport;
        _agent = agent;
    }

    private Uri Route(string rest) => _transport.Gateway($"v1/agents/{Uri.EscapeDataString(_agent)}/provenance/{rest}");

    /// <summary>The marking policy the agent follows.</summary>
    public Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<ProvenancePolicy>(Route("policy"), cancellationToken);

    /// <summary>Signs the record of one placement: the server keeps it and returns it signed.</summary>
    public Task<SignedRecord> SignRecordAsync(RecordRequest request, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<SignedRecord>(Route("records"), request ?? throw new ArgumentNullException(nameof(request)),
            cancellationToken);

    /// <summary>Reports how a placement went: which layers held and why the others did not.</summary>
    public Task ReportPlacementAsync(PlacementReport report, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<bool>(Route("placements"), report ?? throw new ArgumentNullException(nameof(report)),
            cancellationToken);
}

/// <summary>What anyone checking this installation's marks needs, without sign-in.</summary>
public sealed class ProvenanceResource
{
    private readonly FadenstackTransport _transport;

    internal ProvenanceResource(FadenstackTransport transport) => _transport = transport;

    /// <summary>Every signing certificate chain the installation used, the CAs they end in, and the detector's id.</summary>
    public Task<ProvenanceAnchors> GetAnchorsAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<ProvenanceAnchors>(_transport.Gateway("v1/provenance/anchors"), cancellationToken);
}

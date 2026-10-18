using Fadenstack.Http;
using Fadenstack.Models;

namespace Fadenstack.Resources;

/// <summary>
/// Content marking for the client's agent: the policy it follows, signed records of what it places, reports of how
/// each placement went, and checking content for marks. Only hashes and names go to the server, never the content it
/// places. A server with the provenance module off answers 503 (<c>provenance_unavailable</c>); one from before the
/// module, 404.
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

    /// <summary>Checks content for marks with the agent's token (<c>POST .../provenance/verify</c>). Nothing submitted is kept.</summary>
    public Task<VerifyResult> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken = default) =>
        ProvenanceResource.VerifyAtAsync(_transport, Route("verify"), request, cancellationToken);
}

/// <summary>What anyone checking this installation's marks needs: the anchors (without sign-in) and the detector.</summary>
public sealed class ProvenanceResource
{
    private readonly FadenstackTransport _transport;
    private readonly string? _agent;

    /// <param name="transport">The client's requests.</param>
    /// <param name="agent">The client's agent, whose route <see cref="VerifyAsync"/> takes (an agent token is refused on the user's).</param>
    internal ProvenanceResource(FadenstackTransport transport, string? agent = null)
    {
        _transport = transport;
        _agent = string.IsNullOrEmpty(agent) ? null : agent;
    }

    /// <summary>Every signing certificate chain the installation used, the CAs they end in, and the detector's id.</summary>
    public Task<ProvenanceAnchors> GetAnchorsAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<ProvenanceAnchors>(_transport.Gateway("v1/provenance/anchors"), cancellationToken);

    /// <summary>
    /// Checks content for marks. Nothing submitted is kept. An agent's client checks on its agent's route
    /// (<c>POST /v1/agents/{agent}/provenance/verify</c>, with its agent token), as its other routes go; a client without
    /// an agent on the signed-in user's (<c>POST /v1/provenance/verify</c>).
    /// </summary>
    public Task<VerifyResult> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken = default) =>
        VerifyAtAsync(_transport, _transport.Gateway(_agent is null
            ? "v1/provenance/verify"
            : $"v1/agents/{Uri.EscapeDataString(_agent)}/provenance/verify"), request, cancellationToken);

    internal static Task<VerifyResult> VerifyAtAsync(FadenstackTransport transport, Uri route, VerifyRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if ((request.Text is null) == (request.File is null))
        {
            throw new ArgumentException("Check either a text or a file (base64), not both and not neither.", nameof(request));
        }
        if (request.File is not null && string.IsNullOrWhiteSpace(request.MediaType))
        {
            throw new ArgumentException("A file to check needs its media type.", nameof(request));
        }
        return transport.PostAsync<VerifyResult>(route, request, cancellationToken);
    }
}

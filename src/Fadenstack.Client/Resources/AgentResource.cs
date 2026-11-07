using System.Text.Json;
using Fadenstack.Http;
using Fadenstack.Models;

namespace Fadenstack.Resources;

/// <summary>The client's agent: its profile, the events the SDK reports, and approvals the gateway waits for.</summary>
public sealed class AgentResource
{
    private readonly FadenstackTransport _transport;
    private readonly string _agent;

    internal AgentResource(FadenstackTransport transport, string agent)
    {
        _transport = transport;
        _agent = agent;
        Provenance = new AgentProvenanceResource(transport, agent);
    }

    private Uri Route(string rest) => _transport.Gateway($"v1/agents/{Uri.EscapeDataString(_agent)}/{rest}");

    /// <summary>Content marking: the agent's policy, signed records of what it places, placement reports.</summary>
    public AgentProvenanceResource Provenance { get; }

    /// <summary>The agent's current version: model, tool contract, local MCP mode, tool modes; not its instructions.</summary>
    public Task<AgentProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<AgentProfile>(Route("profile"), cancellationToken);

    /// <summary>Reports what the SDK did -- names, counts, outcomes. Up to 100 per call.</summary>
    public async Task ReportAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }
        // Serialized by their runtime type, so each carries its own fields.
        var batch = new { events = events.Select(e => (object)e).ToList() };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(batch, FadenstackJson.Options));
        await _transport.PostAsync<bool>(Route("events"), doc.RootElement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the server what the app brings -- its tools with their classes, its context, its greeting -- for its
    /// admins to review. The same manifest again is only counted (<see cref="ManifestReceipt.IsNew"/> false).
    /// </summary>
    public Task<ManifestReceipt> ReportManifestAsync(AppManifest manifest, CancellationToken cancellationToken = default) =>
        _transport.PutAsync<ManifestReceipt>(Route("manifest"), manifest, cancellationToken);

    /// <summary>
    /// Pictures from a prompt, by the image model the server runs (<see cref="AgentProfile.Images"/> says whether
    /// it has one); for a tool that puts a picture into a document. The prompt is checked like any text.
    /// </summary>
    public async Task<ImagesResponse> GenerateImagesAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        var (images, receipts) = await _transport.SendJsonAsync<ImagesResponse>(System.Net.Http.HttpMethod.Post, Route("images/generations"),
            request, ProvenanceHeader.Name, cancellationToken).ConfigureAwait(false);
        return images with { Receipts = ProvenanceHeader.Parse(receipts) };
    }

    /// <summary>
    /// Keeps the language the user picked (a BCP 47 tag such as <c>de</c> or <c>zh-Hans</c>) for all their clients and the
    /// console; null forgets it, so each client follows its host. Returns the language kept. An older server, without the
    /// route, answers 404.
    /// </summary>
    public async Task<string?> SetUserLanguageAsync(string? language, CancellationToken cancellationToken = default)
    {
        // A dictionary, so a null is sent as null ("forget it"), not left out ("change nothing").
        var body = new Dictionary<string, string?> { ["language"] = language };
        var kept = await _transport.PatchAsync<UserSettings>(Route("user"), body, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(kept?.Language) ? null : kept!.Language;
    }

    /// <summary>
    /// The skills the signed-in user can pick for a message in this agent's chats (they go as
    /// <see cref="Chat.FadenstackChatOptions.Skills"/>): the ones a chat could be given, <paramref name="query"/> narrowing
    /// them by name or description (at most 200 characters), at most <paramref name="limit"/> (1 to 100). A server without
    /// skills, or an older one without the route, offers none (<see cref="SkillList.Available"/> false).
    /// </summary>
    public async Task<SkillList> ListSkillsAsync(string? query = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        if (query?.Trim() is { Length: > 0 } q)
        {
            parameters.Add("q=" + Uri.EscapeDataString(Cut(q, 200)));
        }
        if (limit is { } l)
        {
            parameters.Add("limit=" + Math.Max(1, Math.Min(100, l)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var route = parameters.Count == 0 ? "skills" : "skills?" + string.Join("&", parameters);
        try
        {
            var list = await _transport.GetAsync<SkillListWire>(Route(route), cancellationToken).ConfigureAwait(false);
            return list?.ToList() ?? new SkillList();
        }
        catch (FadenstackApiException e) when (e.Status == System.Net.HttpStatusCode.NotFound && e.Code != "agent_not_found")
        {
            return new SkillList(); // no route: a server from before skills
        }
    }

    /// <summary>At most <paramref name="length"/> characters, never half a surrogate pair (it could not be sent).</summary>
    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text.Substring(0, char.IsHighSurrogate(text[length - 1]) ? length - 1 : length);

    /// <summary>Approves or declines a gateway tool call waiting for the user (<c>faden.approval_required</c>).</summary>
    public Task AnswerApprovalAsync(string approvalId, bool approved, CancellationToken cancellationToken = default) =>
        AnswerApprovalAsync(approvalId, approved, null, cancellationToken);

    /// <summary>
    /// The same, saying what an approval covers: <c>once</c> (this call), <c>chat</c> or <c>always</c>. A destructive or
    /// unclassified call, or one an administrator's rule asks about, takes <c>once</c> only: the gateway refuses another
    /// scope with HTTP 409, and the call goes on waiting. A server may also refuse an approval that names no scope.
    /// </summary>
    public Task AnswerApprovalAsync(string approvalId, bool approved, string? scope, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<bool>(Route($"approvals/{Uri.EscapeDataString(approvalId)}"), new { approved, scope },
            cancellationToken);

    /// <summary>
    /// Asks the server for a grant the person allowed on this device: what it answers is what holds, perhaps less than
    /// asked (<see cref="AgentGrant.Narrowed"/>). A refusal is a <see cref="FadenstackApiException"/> (403
    /// <c>grant_not_offered</c> or <c>grant_needs_device</c>, 422 for a request it cannot grant). Needs the server's
    /// <c>approval_scopes.grant</c>; a grant for a stretch of work needs the conversation's id on the client.
    /// </summary>
    public Task<AgentGrant> CreateGrantAsync(GrantRequest request, CancellationToken cancellationToken = default) =>
        CreateGrantAsync(request, null, cancellationToken);

    /// <summary>
    /// The same, for the conversation <paramref name="conversationId"/> (sent as <c>X-Faden-Conversation-Id</c>, as the
    /// chat's requests are): a stretch of work belongs to one, a grant for a time only with
    /// <see cref="GrantRequest.Conversation"/>.
    /// </summary>
    public Task<AgentGrant> CreateGrantAsync(GrantRequest request, string? conversationId, CancellationToken cancellationToken = default) =>
        conversationId is null
            ? _transport.PostAsync<AgentGrant>(Route("grants"), request, cancellationToken)
            : _transport.PostAsync<AgentGrant>(Route("grants"), request,
                new Dictionary<string, string> { ["X-Faden-Conversation-Id"] = conversationId }, cancellationToken);

    /// <summary>
    /// Extends a running grant of this device in place: more time, more places. The server keeps its whole span within
    /// the agent's longest grant from when it was made (<see cref="AgentGrant.Narrowed"/>: <c>minutes</c> when it cut
    /// the time); past that it refuses with 422 <c>grant_at_longest</c>, and a new grant needs the full card.
    /// </summary>
    public Task<AgentGrant> ExtendGrantAsync(string grantId, GrantExtension extension, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<AgentGrant>(Route($"grants/{Uri.EscapeDataString(grantId)}/extend"), extension, cancellationToken);

    /// <summary>This device's live grants for the agent: active or paused, not expired, not spent.</summary>
    public Task<GrantList> ListGrantsAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<GrantList>(Route("grants"), cancellationToken);

    /// <summary>Ends one of the person's grants for this agent, from the next call on; it answers with the grant.</summary>
    public Task<AgentGrant> RevokeGrantAsync(string grantId, CancellationToken cancellationToken = default) =>
        _transport.SendJsonAsync<AgentGrant>(System.Net.Http.HttpMethod.Delete, Route($"grants/{Uri.EscapeDataString(grantId)}"),
            null, cancellationToken);

    /// <summary>Pauses a grant of this device (its screen locked, the person away); its time runs on.</summary>
    public Task<AgentGrant> PauseGrantAsync(string grantId, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<AgentGrant>(Route($"grants/{Uri.EscapeDataString(grantId)}/pause"), null, cancellationToken);

    /// <summary>Resumes a paused grant of this device, unless it ended meanwhile (then 404).</summary>
    public Task<AgentGrant> ResumeGrantAsync(string grantId, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<AgentGrant>(Route($"grants/{Uri.EscapeDataString(grantId)}/resume"), null, cancellationToken);
}

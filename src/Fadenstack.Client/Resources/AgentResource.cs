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
        _transport.PostAsync<bool>(Route($"approvals/{Uri.EscapeDataString(approvalId)}"), new { approved },
            cancellationToken);
}

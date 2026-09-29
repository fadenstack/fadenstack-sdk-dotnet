using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Faden.Http;
using Faden.Models;
using Faden.Tools;

namespace Faden.Resources;

/// <summary>Server sessions (the web chat's and the API's): CRUD, messages, summary.</summary>
public sealed class SessionsResource
{
    private readonly FadenTransport _t;

    internal SessionsResource(FadenTransport transport) => _t = transport;

    public Task<Session> CreateAsync(string? title = null, Guid? projectId = null,
        Dictionary<string, object?>? metadataJson = null, CancellationToken cancellationToken = default) =>
        _t.PostAsync<Session>(_t.Gateway("v1/sessions"),
            new { title, project_id = projectId, metadata_json = metadataJson }, cancellationToken);

    public async Task<List<Session>> ListAsync(Guid? projectId = null, string? sessionStatus = null,
        CancellationToken cancellationToken = default)
    {
        var query = Query(("project_id", projectId?.ToString()), ("session_status", sessionStatus));
        return (await _t.GetAsync<DataWrapper<Session>>(_t.Gateway("v1/sessions" + query), cancellationToken)
            .ConfigureAwait(false)).Data ?? new();
    }

    public Task<Session> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _t.GetAsync<Session>(_t.Gateway($"v1/sessions/{sessionId}"), cancellationToken);

    public Task<Session> UpdateAsync(Guid sessionId, string? title = null, string? status = null,
        Dictionary<string, object?>? metadataJson = null, CancellationToken cancellationToken = default) =>
        _t.PatchAsync<Session>(_t.Gateway($"v1/sessions/{sessionId}"),
            new { title, status, metadata_json = metadataJson }, cancellationToken);

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _t.DeleteAsync(_t.Gateway($"v1/sessions/{sessionId}"), cancellationToken);

    /// <summary>The latest messages, oldest first; a refused one has <see cref="SessionMessage.Notice"/>.</summary>
    public async Task<List<SessionMessage>> MessagesAsync(Guid sessionId, int limit = 100,
        CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<SessionMessage>>(_t.Gateway($"v1/sessions/{sessionId}/messages?limit={limit}"),
            cancellationToken).ConfigureAwait(false)).Data ?? new();

    public async Task<Summary?> SummaryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<Summary>>(_t.Gateway($"v1/sessions/{sessionId}/summary"), cancellationToken)
            .ConfigureAwait(false)).Data?.FirstOrDefault();

    internal static string Query(params (string Key, string? Value)[] pairs)
    {
        var given = pairs.Where(p => p.Value is not null).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return given.Count == 0 ? "" : "?" + string.Join("&", given);
    }
}

public sealed class ProjectsResource
{
    private readonly FadenTransport _t;

    internal ProjectsResource(FadenTransport transport) => _t = transport;

    public Task<Project> CreateAsync(string name, string? description = null, string? systemInstructions = null,
        string? modelAlias = null, CancellationToken cancellationToken = default) =>
        _t.PostAsync<Project>(_t.Gateway("v1/sessions/projects"),
            new { name, description, system_instructions = systemInstructions, model_alias = modelAlias }, cancellationToken);

    public async Task<List<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<Project>>(_t.Gateway("v1/sessions/projects"), cancellationToken)
            .ConfigureAwait(false)).Data ?? new();

    public Task<Project> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _t.GetAsync<Project>(_t.Gateway($"v1/sessions/projects/{projectId}"), cancellationToken);

    public Task<Project> UpdateAsync(Guid projectId, string? name = null, string? description = null,
        string? systemInstructions = null, string? modelAlias = null, CancellationToken cancellationToken = default) =>
        _t.PatchAsync<Project>(_t.Gateway($"v1/sessions/projects/{projectId}"),
            new { name, description, system_instructions = systemInstructions, model_alias = modelAlias }, cancellationToken);

    public Task DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _t.DeleteAsync(_t.Gateway($"v1/sessions/projects/{projectId}"), cancellationToken);
}

/// <summary>A server session's tools, and its tool policy -- including the tool mode (sdk/07).</summary>
public sealed class ToolsResource
{
    private readonly FadenTransport _t;

    internal ToolsResource(FadenTransport transport) => _t = transport;

    public Task<ToolAvailabilityResponse> GetAvailableAsync(Guid sessionId, bool includeDisabled = true,
        bool includeUnavailable = true, CancellationToken cancellationToken = default) =>
        _t.GetAsync<ToolAvailabilityResponse>(_t.Gateway("v1/tools/available" + SessionsResource.Query(
            ("session_id", sessionId.ToString()),
            ("include_disabled", includeDisabled ? "true" : "false"),
            ("include_unavailable", includeUnavailable ? "true" : "false"))), cancellationToken);

    public Task<SessionToolPolicy> GetPolicyAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _t.GetAsync<SessionToolPolicy>(_t.Gateway($"v1/sessions/{sessionId}/tool-policy"), cancellationToken);

    public Task<SessionToolPolicy> UpdatePolicyAsync(Guid sessionId, ToolMode? toolMode = null,
        string? executionMode = null, string? hybridPreference = null, CancellationToken cancellationToken = default) =>
        _t.PatchAsync<SessionToolPolicy>(_t.Gateway($"v1/sessions/{sessionId}/tool-policy"), new
        {
            tool_mode = toolMode?.ToWire(),
            execution_mode = executionMode,
            hybrid_preference = hybridPreference,
        }, cancellationToken);
}

public sealed class MemoryResource
{
    private readonly FadenTransport _t;

    internal MemoryResource(FadenTransport transport) => _t = transport;

    public async Task<List<MemoryFact>> ListAsync(string? scope = null, Guid? projectId = null, Guid? sessionId = null,
        CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<MemoryFact>>(_t.Gateway("v1/memory/facts" + SessionsResource.Query(
            ("scope", scope), ("project_id", projectId?.ToString()), ("session_id", sessionId?.ToString()))),
            cancellationToken).ConfigureAwait(false)).Data ?? new();

    public Task<MemoryFact> CreateAsync(string key, string value, string scope = "user", Guid? sessionId = null,
        Guid? projectId = null, double confidence = 1.0, CancellationToken cancellationToken = default) =>
        _t.PostAsync<MemoryFact>(_t.Gateway("v1/memory/facts"),
            new { key, value, scope, confidence, session_id = sessionId, project_id = projectId }, cancellationToken);

    public Task UpdateAsync(Guid factId, string? value = null, double? confidence = null, string? status = null,
        CancellationToken cancellationToken = default) =>
        _t.PatchAsync<bool>(_t.Gateway($"v1/memory/facts/{factId}"), new { value, confidence, status }, cancellationToken);

    public Task DeleteAsync(Guid factId, CancellationToken cancellationToken = default) =>
        _t.DeleteAsync(_t.Gateway($"v1/memory/facts/{factId}"), cancellationToken);
}

public sealed class AttachmentsResource
{
    private readonly FadenTransport _t;

    internal AttachmentsResource(FadenTransport transport) => _t = transport;

    public Task<AttachmentUploadResponse> UploadAsync(Guid sessionId, string fileName, byte[] content,
        string contentType = "application/octet-stream", CancellationToken cancellationToken = default) =>
        Upload(_t.Gateway($"v1/sessions/{sessionId}/attachments"), fileName, content, contentType, cancellationToken);

    public Task<AttachmentUploadResponse> UploadToProjectAsync(Guid projectId, string fileName, byte[] content,
        string contentType = "application/octet-stream", CancellationToken cancellationToken = default) =>
        Upload(_t.Gateway($"v1/sessions/projects/{projectId}/attachments"), fileName, content, contentType,
            cancellationToken);

    public async Task<List<Attachment>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<Attachment>>(_t.Gateway($"v1/sessions/{sessionId}/attachments"),
            cancellationToken).ConfigureAwait(false)).Data ?? new();

    public async Task<List<Attachment>> ListProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await _t.GetAsync<DataWrapper<Attachment>>(_t.Gateway($"v1/sessions/projects/{projectId}/attachments"),
            cancellationToken).ConfigureAwait(false)).Data ?? new();

    public Task DeleteAsync(Guid sessionId, Guid attachmentId, CancellationToken cancellationToken = default) =>
        _t.DeleteAsync(_t.Gateway($"v1/sessions/{sessionId}/attachments/{attachmentId}"), cancellationToken);

    public Task<AttachmentStats> StatsAsync(CancellationToken cancellationToken = default) =>
        _t.GetAsync<AttachmentStats>(_t.Gateway("v1/sessions/attachments/stats"), cancellationToken);

    private async Task<AttachmentUploadResponse> Upload(Uri url, string fileName, byte[] content, string contentType,
        CancellationToken cancellationToken)
    {
        using var response = await _t.SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", fileName);
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ErrorReader.ToException(response, text);
        }
        return JsonSerializer.Deserialize<AttachmentUploadResponse>(text, FadenJson.Options)!;
    }
}

public sealed class ServicesResource
{
    private readonly FadenTransport _t;

    internal ServicesResource(FadenTransport transport) => _t = transport;

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _t.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _t.Gateway("api/health")),
                HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (FadenException)
        {
            return false;
        }
    }

    public async Task<List<ServiceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var doc = await _t.GetAsync<JsonElement>(_t.Gateway("v1/services"), cancellationToken).ConfigureAwait(false);
        return doc.TryGetProperty("services", out var services)
            ? JsonSerializer.Deserialize<List<ServiceInfo>>(services.GetRawText(), FadenJson.Options) ?? new()
            : new();
    }
}

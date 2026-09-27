using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LLMPort.Client.Models;

namespace LLMPort.Client.Resources;

/// <summary>Attachment upload, list, delete for sessions and projects.</summary>
public sealed class AttachmentsResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal AttachmentsResource(HttpClient http) => _http = http;

    // -- session-scoped ------------------------------------------------------

    public async Task<AttachmentUploadResponse> UploadAsync(Guid sessionId, string fileName, byte[] fileContent, string contentType = "application/octet-stream", CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileStreamContent = new ByteArrayContent(fileContent);
        fileStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileStreamContent, "file", fileName);
        var resp = await _http.PostAsync($"/v1/sessions/{sessionId}/attachments", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AttachmentUploadResponse>(JsonOpts, ct))!;
    }

    public async Task<List<Attachment>> ListAsync(Guid sessionId, CancellationToken ct = default)
    {
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<Attachment>>($"/v1/sessions/{sessionId}/attachments", JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    public async Task DeleteAsync(Guid sessionId, Guid attachmentId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/v1/sessions/{sessionId}/attachments/{attachmentId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    // -- project-scoped ------------------------------------------------------

    public async Task<AttachmentUploadResponse> UploadToProjectAsync(Guid projectId, string fileName, byte[] fileContent, string contentType = "application/octet-stream", CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileStreamContent = new ByteArrayContent(fileContent);
        fileStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileStreamContent, "file", fileName);
        var resp = await _http.PostAsync($"/v1/sessions/projects/{projectId}/attachments", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AttachmentUploadResponse>(JsonOpts, ct))!;
    }

    public async Task<List<Attachment>> ListProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<Attachment>>($"/v1/sessions/projects/{projectId}/attachments", JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    // -- stats ---------------------------------------------------------------

    public async Task<AttachmentStats> StatsAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<AttachmentStats>("/v1/sessions/attachments/stats", JsonOpts, ct))!;
}

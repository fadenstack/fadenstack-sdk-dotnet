using System.Net.Http.Json;
using System.Text.Json;
using LLMPort.Client.Models;

namespace LLMPort.Client.Resources;

/// <summary>Sessions CRUD, messages, summary, capacity.</summary>
public sealed class SessionsResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal SessionsResource(HttpClient http) => _http = http;

    public async Task<Session> CreateAsync(string? title = null, Guid? projectId = null, Dictionary<string, object>? metadataJson = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (projectId is not null) body["project_id"] = projectId.Value.ToString();
        if (metadataJson is not null) body["metadata_json"] = metadataJson;
        var resp = await _http.PostAsJsonAsync("/v1/sessions", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Session>(JsonOpts, ct))!;
    }

    public async Task<List<Session>> ListAsync(Guid? projectId = null, string? sessionStatus = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (projectId is not null) query.Add($"project_id={projectId}");
        if (sessionStatus is not null) query.Add($"session_status={Uri.EscapeDataString(sessionStatus)}");
        var path = query.Count > 0 ? $"/v1/sessions?{string.Join('&', query)}" : "/v1/sessions";
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<Session>>(path, JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    public async Task<Session> GetAsync(Guid sessionId, CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<Session>($"/v1/sessions/{sessionId}", JsonOpts, ct))!;

    public async Task<Session> UpdateAsync(Guid sessionId, string? title = null, string? status = null, Dictionary<string, object>? metadataJson = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (status is not null) body["status"] = status;
        if (metadataJson is not null) body["metadata_json"] = metadataJson;
        var resp = await _http.PatchAsJsonAsync($"/v1/sessions/{sessionId}", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Session>(JsonOpts, ct))!;
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/v1/sessions/{sessionId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<ChatMessage>> MessagesAsync(Guid sessionId, int limit = 100, CancellationToken ct = default)
    {
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<ChatMessage>>($"/v1/sessions/{sessionId}/messages?limit={limit}", JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    public async Task<Summary?> SummaryAsync(Guid sessionId, CancellationToken ct = default)
    {
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<Summary>>($"/v1/sessions/{sessionId}/summary", JsonOpts, ct);
        return wrapper?.Data?.FirstOrDefault();
    }

    public async Task<bool> StreamStatusAsync(Guid sessionId, CancellationToken ct = default)
    {
        var doc = await _http.GetFromJsonAsync<JsonDocument>($"/v1/sessions/{sessionId}/stream/status", ct);
        return doc?.RootElement.GetProperty("active").GetBoolean() ?? false;
    }
}

internal sealed record DataWrapper<T>
{
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public List<T>? Data { get; init; }
}

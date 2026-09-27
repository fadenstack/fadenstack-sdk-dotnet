using System.Net.Http.Json;
using System.Text.Json;
using LLMPort.Client.Models;

namespace LLMPort.Client.Resources;

/// <summary>Memory facts CRUD.</summary>
public sealed class MemoryResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal MemoryResource(HttpClient http) => _http = http;

    public async Task<List<MemoryFact>> ListAsync(string? scope = null, Guid? projectId = null, Guid? sessionId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (scope is not null) query.Add($"scope={Uri.EscapeDataString(scope)}");
        if (projectId is not null) query.Add($"project_id={projectId}");
        if (sessionId is not null) query.Add($"session_id={sessionId}");
        var path = query.Count > 0 ? $"/v1/memory/facts?{string.Join('&', query)}" : "/v1/memory/facts";
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<MemoryFact>>(path, JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    public async Task<MemoryFact> CreateAsync(string key, string value, string scope = "user", Guid? sessionId = null, Guid? projectId = null, double confidence = 1.0, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["value"] = value,
            ["scope"] = scope,
            ["confidence"] = confidence,
        };
        if (sessionId is not null) body["session_id"] = sessionId.Value.ToString();
        if (projectId is not null) body["project_id"] = projectId.Value.ToString();
        var resp = await _http.PostAsJsonAsync("/v1/memory/facts", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MemoryFact>(JsonOpts, ct))!;
    }

    public async Task UpdateAsync(Guid factId, string? value = null, double? confidence = null, string? status = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (value is not null) body["value"] = value;
        if (confidence is not null) body["confidence"] = confidence;
        if (status is not null) body["status"] = status;
        var resp = await _http.PatchAsJsonAsync($"/v1/memory/facts/{factId}", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(Guid factId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/v1/memory/facts/{factId}", ct);
        resp.EnsureSuccessStatusCode();
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using LLMPort.Client.Models;

namespace LLMPort.Client.Resources;

/// <summary>Tool availability catalog and session tool policy.</summary>
public sealed class ToolsResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal ToolsResource(HttpClient http) => _http = http;

    public async Task<ToolAvailabilityResponse> GetAvailableAsync(Guid sessionId, bool includeDisabled = true, bool includeUnavailable = true, CancellationToken ct = default)
    {
        var path = $"/v1/tools/available?session_id={sessionId}&include_disabled={includeDisabled.ToString().ToLowerInvariant()}&include_unavailable={includeUnavailable.ToString().ToLowerInvariant()}";
        return (await _http.GetFromJsonAsync<ToolAvailabilityResponse>(path, JsonOpts, ct))!;
    }

    public async Task<SessionToolPolicy> GetPolicyAsync(Guid sessionId, CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<SessionToolPolicy>($"/v1/sessions/{sessionId}/tool-policy", JsonOpts, ct))!;

    public async Task<SessionToolPolicy> UpdatePolicyAsync(Guid sessionId, string? executionMode = null, string? hybridPreference = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (executionMode is not null) body["execution_mode"] = executionMode;
        if (hybridPreference is not null) body["hybrid_preference"] = hybridPreference;
        var resp = await _http.PatchAsJsonAsync($"/v1/sessions/{sessionId}/tool-policy", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionToolPolicy>(JsonOpts, ct))!;
    }
}

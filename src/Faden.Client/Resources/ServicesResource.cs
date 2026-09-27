using System.Net.Http.Json;
using System.Text.Json;
using LLMPort.Client.Models;

namespace LLMPort.Client.Resources;

/// <summary>Health check and service discovery.</summary>
public sealed class ServicesResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal ServicesResource(HttpClient http) => _http = http;

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<ServiceInfo>> ListAsync(CancellationToken ct = default)
    {
        var doc = await _http.GetFromJsonAsync<JsonDocument>("/v1/services", ct);
        if (doc is null) return [];
        var servicesElement = doc.RootElement.GetProperty("services");
        return JsonSerializer.Deserialize<List<ServiceInfo>>(servicesElement.GetRawText(), JsonOpts) ?? [];
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Faden.Client.Models;

namespace Faden.Client.Resources;

/// <summary>Projects CRUD.</summary>
public sealed class ProjectsResource
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal ProjectsResource(HttpClient http) => _http = http;

    public async Task<Project> CreateAsync(string name, string? description = null, string? systemInstructions = null, string? modelAlias = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (description is not null) body["description"] = description;
        if (systemInstructions is not null) body["system_instructions"] = systemInstructions;
        if (modelAlias is not null) body["model_alias"] = modelAlias;
        var resp = await _http.PostAsJsonAsync("/v1/sessions/projects", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Project>(JsonOpts, ct))!;
    }

    public async Task<List<Project>> ListAsync(CancellationToken ct = default)
    {
        var wrapper = await _http.GetFromJsonAsync<DataWrapper<Project>>("/v1/sessions/projects", JsonOpts, ct);
        return wrapper?.Data ?? [];
    }

    public async Task<Project> GetAsync(Guid projectId, CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<Project>($"/v1/sessions/projects/{projectId}", JsonOpts, ct))!;

    public async Task<Project> UpdateAsync(Guid projectId, string? name = null, string? description = null, string? systemInstructions = null, string? modelAlias = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        if (systemInstructions is not null) body["system_instructions"] = systemInstructions;
        if (modelAlias is not null) body["model_alias"] = modelAlias;
        var resp = await _http.PatchAsJsonAsync($"/v1/sessions/projects/{projectId}", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Project>(JsonOpts, ct))!;
    }

    public async Task DeleteAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/v1/sessions/projects/{projectId}", ct);
        resp.EnsureSuccessStatusCode();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;

namespace Fadenstack.Client.Tests;

/// <summary>Plays the gateway and the backend: answers by route, and keeps what it was sent.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<Recorded, HttpResponseMessage> Answer)> _routes = new();

    public List<Recorded> Requests { get; } = new();

    public FakeServer On(string method, string path, Func<Recorded, HttpResponseMessage> answer)
    {
        _routes.Add((method, path, answer));
        return this;
    }

    public FakeServer Json(string method, string path, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, path, _ => Respond(status, body));

    public static HttpResponseMessage Respond(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events), Encoding.UTF8, "text/event-stream"),
    };

    /// <summary>
    /// <c>GET {path}</c> as the gateway's skills route answers: the skills whose slug, name or description has <c>q</c>,
    /// at most <c>limit</c> (1 to 100, 50 when not said). Without skills (null) the server has no skills module:
    /// <c>available</c> false.
    /// </summary>
    public FakeServer Skills(string path, IReadOnlyList<FakeSkill>? skills) => On("GET", path, r =>
    {
        if (skills is null)
        {
            return Respond(HttpStatusCode.OK, new { @object = "list", available = false, data = Array.Empty<object>() });
        }
        var q = (r.QueryValue("q") ?? "").ToLowerInvariant();
        var limit = int.TryParse(r.QueryValue("limit"), out var l) ? Math.Max(1, Math.Min(100, l)) : 50;
        var data = skills
            .Where(s => q.Length == 0 || new[] { s.Slug, s.Name ?? "", s.Description ?? "" }.Any(t => t.ToLowerInvariant().Contains(q)))
            .Take(limit)
            .Select(s => new { id = $"skill-{s.Slug}", slug = s.Slug, name = s.Name ?? s.Slug, description = s.Description ?? "", activation = s.Activation });
        return Respond(HttpStatusCode.OK, new { @object = "list", available = true, data });
    });

    public static string Data(object chunk) => $"data: {JsonSerializer.Serialize(chunk)}\n\n";

    public static string Fadenstack(object @event) => $"event: faden\ndata: {JsonSerializer.Serialize(@event)}\n\n";

    public const string Done = "data: [DONE]\n\n";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
            request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)), body);
        Requests.Add(recorded);
        var route = _routes.LastOrDefault(r => r.Method == recorded.Method && r.Path == recorded.Path);
        return route.Answer is null
            ? Respond(HttpStatusCode.NotFound, new { detail = "Not Found" })
            : route.Answer(recorded);
    }
}

public sealed record Recorded(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

    /// <summary>A query parameter, decoded; null when the request has none of that name.</summary>
    public string? QueryValue(string name) => Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => Uri.UnescapeDataString(p[0]) == name)
        .Select(p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "")
        .FirstOrDefault();
}

/// <summary>A skill a fake server offers (<see cref="FakeServer.Skills"/>).</summary>
public sealed record FakeSkill(string Slug, string? Name = null, string? Description = null, string Activation = "manual");

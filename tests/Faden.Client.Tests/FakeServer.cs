using System.Net;
using System.Text;
using System.Text.Json;

namespace Faden.Client.Tests;

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

    public static string Data(object chunk) => $"data: {JsonSerializer.Serialize(chunk)}\n\n";

    public static string Faden(object @event) => $"event: faden\ndata: {JsonSerializer.Serialize(@event)}\n\n";

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
}

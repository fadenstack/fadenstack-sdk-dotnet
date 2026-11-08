using System.Net;
using System.Text.Json;
using Fadenstack.Client.Tests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The gateway's grants (<c>/v1/agents/docs-assistant/grants</c>), as far as the SDK sees them: made, listed, ended,
/// paused, extended within the longest grant from their start. Ids are <c>g-1</c>, <c>g-2</c>...; times are
/// <see cref="ServerNow"/>'s, which tests set apart from the device's clock.
/// </summary>
public sealed class FakeGrants
{
    private const string Route = "/v1/agents/docs-assistant/grants";
    private readonly List<Dictionary<string, object?>> _grants = new();

    public FakeGrants(FakeServer server)
    {
        Server = server;
        server.On("POST", Route, Create).On("GET", Route, _ => FakeServer.Respond(HttpStatusCode.OK, new
        {
            @object = "list", data = _grants.Where(g => (string)g["status"]! is "active" or "paused").Select(Shown).ToList(),
            server_now = ServerNow,
        }));
        for (var i = 1; i <= 5; i++)
        {
            var id = $"g-{i}";
            server.On("DELETE", $"{Route}/{id}", _ => Change(id, g => { g["status"] = "revoked"; g["revoked_by"] = "person"; }))
                .On("POST", $"{Route}/{id}/pause", _ => Change(id, g => g["status"] = "paused"))
                .On("POST", $"{Route}/{id}/resume", _ => Change(id, g => g["status"] = "active"))
                .On("POST", $"{Route}/{id}/extend", r => Extend(id, r))
                .On("POST", $"{Route}/{id}/report", r => Report(id, r));
        }
    }

    public FakeServer Server { get; }

    /// <summary>The server's clock: a minute ahead of the device's unless a test says otherwise.</summary>
    public DateTimeOffset ServerNow { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);

    /// <summary>The server's refusal of the next grants, as its error code (e.g. <c>grant_not_offered</c>); null: it grants.</summary>
    public string? Refuse { get; set; }

    public int LongestMinutes { get; set; } = 120;

    /// <summary>The requests that made grants.</summary>
    public List<Recorded> Made => Server.Requests.Where(r => r.Method == "POST" && r.Path == Route).ToList();

    public List<Recorded> Extensions => Server.Requests.Where(r => r.Path.EndsWith("/extend")).ToList();

    /// <summary>The reports of turns in Bypass (L-61): the grant, and the calls the device said it ran.</summary>
    public List<(string Grant, List<string> Calls)> Reports { get; } = new();

    public string Status(string id) => (string)_grants.Single(g => (string)g["id"]! == id)["status"]!;

    /// <summary>A grant held on the server without the device having made it (another app on the device, the console).</summary>
    public string Hold(string kind, object scope, int minutes = 30, string status = "active")
    {
        var grant = New(kind, scope, minutes);
        grant["status"] = status;
        return (string)grant["id"]!;
    }

    /// <summary>The value of a stream's <c>faden.grants</c>: the live grants, as the gateway sends them.</summary>
    public object Snapshot() => new
    {
        serverNow = ServerNow,
        grants = _grants.Where(g => (string)g["status"]! is "active" or "paused").Select(Shown).ToList(),
    };

    private HttpResponseMessage Create(Recorded r)
    {
        if (Refuse is { } code)
        {
            return FakeServer.Respond(HttpStatusCode.Forbidden, new { error = new { message = "Not offered.", type = "permission_error", code } });
        }
        var body = r.Json;
        var kind = body.GetProperty("kind").GetString()!;
        var minutes = body.GetProperty("minutes").GetInt32();
        object scope = kind == "time"
            ? new { tools = body.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToList() }
            : kind == "bypass" ? new { classes = new[] { "read", "write", "destructive", "unknown" } }
            : new { classes = body.GetProperty("classes").EnumerateArray().Select(t => t.GetString()).ToList(), targets = body.GetProperty("targets") };
        var grant = New(kind, scope, Math.Min(minutes, LongestMinutes));
        grant["conversation_id"] = kind is "work" or "bypass" || body.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.True
            ? r.Header("x-faden-conversation-id") : null;
        grant["narrowed"] = minutes > LongestMinutes ? new[] { "minutes" } : Array.Empty<string>();
        return FakeServer.Respond(HttpStatusCode.Created, Shown(grant));
    }

    private Dictionary<string, object?> New(string kind, object scope, int minutes)
    {
        var grant = new Dictionary<string, object?>
        {
            ["object"] = "agent.grant", ["id"] = $"g-{_grants.Count + 1}", ["kind"] = kind, ["status"] = "active",
            ["scope"] = scope, ["issued_at"] = ServerNow, ["expires_at"] = ServerNow.AddMinutes(minutes),
            ["max_calls"] = null, ["calls_used"] = 0, ["device_id"] = "device-7", ["narrowed"] = Array.Empty<string>(),
        };
        _grants.Add(grant);
        return grant;
    }

    private HttpResponseMessage Change(string id, Action<Dictionary<string, object?>> change)
    {
        var grant = _grants.FirstOrDefault(g => (string)g["id"]! == id);
        if (grant is null)
        {
            return FakeServer.Respond(HttpStatusCode.NotFound, new { error = new { message = "You have no such grant.", code = "grant_not_found" } });
        }
        change(grant);
        return FakeServer.Respond(HttpStatusCode.OK, Shown(grant));
    }

    private HttpResponseMessage Extend(string id, Recorded r) => Change(id, grant =>
    {
        var minutes = r.Json.TryGetProperty("minutes", out var m) ? m.GetInt32() : 0;
        var issued = (DateTimeOffset)grant["issued_at"]!;
        var wanted = ((DateTimeOffset)grant["expires_at"]!).AddMinutes(minutes);
        var cap = issued.AddMinutes(LongestMinutes);
        grant["expires_at"] = wanted > cap ? cap : wanted;
        grant["narrowed"] = wanted > cap ? new[] { "minutes" } : Array.Empty<string>();
    });

    private HttpResponseMessage Report(string id, Recorded r)
    {
        var calls = r.Json.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToList();
        Reports.Add((id, calls));
        return FakeServer.Respond(HttpStatusCode.OK, new { @object = "agent.grant_report", matched = calls.Count, device_only = 0, gateway_only = 0 });
    }

    private Dictionary<string, object?> Shown(Dictionary<string, object?> grant) => new(grant)
    {
        ["server_now"] = ServerNow,
        ["remaining_s"] = (int)Math.Max(0, ((DateTimeOffset)grant["expires_at"]! - ServerNow).TotalSeconds),
    };
}

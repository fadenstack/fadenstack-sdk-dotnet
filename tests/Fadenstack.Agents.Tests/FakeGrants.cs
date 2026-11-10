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
            server.On("GET", $"{Route}/{id}", _ => CanSayWhy
                    ? Change(id, _ => { })
                    : FakeServer.Respond(HttpStatusCode.NotFound, new { detail = "Not Found" }))
                .On("DELETE", $"{Route}/{id}", _ => Change(id, g =>
                {
                    g["status"] = "revoked";
                    g["revoked_by"] = g.TryGetValue("revoked_by", out var by) && by is not null ? by : "person";
                }))
                .On("POST", $"{Route}/{id}/pause", _ => Change(id, g => (g["status"], g["paused_at"]) = ("paused", ServerNow)))
                .On("POST", $"{Route}/{id}/resume", _ => Resume(id))
                .On("POST", $"{Route}/{id}/extend", r => Extend(id, r))
                .On("POST", $"{Route}/{id}/report", r => Report(id, r));
        }
        server.On("POST", StepUpRoute, StartStepUp).On("POST", StepUpRoute + "/token", PollStepUp);
    }

    // -- the second sign-in for Bypass (sdk/41) -------------------------------------------------

    private const string StepUpRoute = "/api/agent-tokens/step-ups";
    private readonly HashSet<string> _proofs = new(StringComparer.Ordinal);
    private int _polls;

    /// <summary>How the person answers in the browser: <c>approved</c>, <c>denied</c> or <c>expired</c> (nobody in time).</summary>
    public string StepUpOutcome { get; set; } = "approved";

    /// <summary>How many polls answer <c>authorization_pending</c> before the outcome.</summary>
    public int PendingPolls { get; set; }

    /// <summary>The step-ups the device started.</summary>
    public int StepUpsStarted => Server.Requests.Count(r => r.Method == "POST" && r.Path == StepUpRoute);

    /// <summary>What the device asked the page to show as the longest time.</summary>
    public List<int?> StepUpLongest { get; } = new();

    /// <summary>The server takes no proof it gave out before (they are older than 5 minutes by its clock).</summary>
    public void ProofsGoStale() => _proofs.Clear();

    /// <summary>
    /// The resume of a paused Bypass, as the gateway may refuse it: 403 (no longer allowed: it ends), 404 (the kill switch
    /// ended it after the device read the list) or 503.
    /// </summary>
    public HttpStatusCode? RefuseResume { get; set; }

    private HttpResponseMessage StartStepUp(Recorded r)
    {
        _polls = 0;
        StepUpLongest.Add(r.Json.TryGetProperty("longest_minutes", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32() : null);
        return FakeServer.Respond(HttpStatusCode.Created, new
        {
            step_up_code = $"device-code-{StepUpsStarted}", user_code = "BCDF-GHJK",
            verification_uri = "https://faden.example/step-up",
            verification_uri_complete = "https://faden.example/step-up#code=BCDF-GHJK", expires_in = 600, interval = 1,
        });
    }

    private HttpResponseMessage PollStepUp(Recorded r)
    {
        if (_polls++ < PendingPolls)
        {
            return FakeServer.Respond(HttpStatusCode.BadRequest, new { error = "authorization_pending", error_description = "Waiting." });
        }
        if (StepUpOutcome != "approved")
        {
            var error = StepUpOutcome == "denied" ? "access_denied" : "expired_token";
            return FakeServer.Respond(HttpStatusCode.BadRequest, new { error, error_description = "No." });
        }
        var proof = $"proof-{_proofs.Count + 1}-{r.Json.GetProperty("step_up_code").GetString()}";
        _proofs.Add(proof);
        return FakeServer.Respond(HttpStatusCode.OK, new { proof, expires_in = 290, signed_in_at = ServerNow, method = "password" });
    }

    /// <summary>A Bypass grant needs a proof this server gave out (RFC 9470's challenge otherwise), as the gateway checks.</summary>
    private HttpResponseMessage? Challenge(Recorded r)
    {
        if (r.Header("x-faden-step-up") is { } proof && _proofs.Contains(proof))
        {
            return null;
        }
        var refused = FakeServer.Respond(HttpStatusCode.Unauthorized, new
        {
            error = new { message = "Switching Bypass on needs a fresh sign-in.", type = "authentication_error", code = "bypass_step_up_required" },
            step_up = new { max_age = 300, reason = "missing", start = StepUpRoute },
        });
        refused.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "error=\"insufficient_user_authentication\", max_age=\"300\""));
        return refused;
    }

    private HttpResponseMessage Resume(string id)
    {
        if (RefuseResume is { } status && _grants.FirstOrDefault(g => (string)g["id"]! == id) is { } found && (string)found["kind"]! == "bypass")
        {
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                found["status"] = "revoked";
                found["revoked_by"] = status == HttpStatusCode.Forbidden ? "limit" : "kill_switch";
            }
            var code = status switch
            {
                HttpStatusCode.Forbidden => "bypass_not_allowed",
                HttpStatusCode.NotFound => "grant_not_found",
                _ => "grant_limits_unavailable",
            };
            return FakeServer.Respond(status, new { error = new { message = "Not now.", code } });
        }
        return Change(id, g => (g["status"], g["paused_at"]) = ("active", null));
    }

    public FakeServer Server { get; }

    /// <summary>The server's clock: a minute ahead of the device's unless a test says otherwise.</summary>
    public DateTimeOffset ServerNow { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);

    /// <summary>The server's refusal of the next grants, as its error code (e.g. <c>grant_not_offered</c>); null: it grants.</summary>
    public string? Refuse { get; set; }

    /// <summary>The status of that refusal: 403, or 503 for <c>grant_limits_unavailable</c>.</summary>
    public HttpStatusCode RefuseStatus { get; set; } = HttpStatusCode.Forbidden;

    public int LongestMinutes { get; set; } = 120;

    /// <summary>What new grants record of their calls (an administrator's level); null: core's default for the kind.</summary>
    public string? EvidenceLevel { get; set; }

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

    /// <summary>
    /// The server ends a grant on its own: <c>admin</c> (Active grants), <c>kill_switch</c> (Bypass turned off), <c>limit</c>
    /// (a narrower list), <c>person</c> (another sign-in). The device learns it from the next list.
    /// </summary>
    public void End(string id, string by)
    {
        var grant = _grants.Single(g => (string)g["id"]! == id);
        grant["status"] = "revoked";
        grant["revoked_by"] = by;
    }

    /// <summary>What the next <c>.../grants/{id}</c> reads answer: 404 as from an older server, when false.</summary>
    public bool CanSayWhy { get; set; } = true;

    /// <summary>The value of a stream's <c>faden.grants</c>: the live grants, as the gateway sends them.</summary>
    public object Snapshot() => new
    {
        serverNow = ServerNow,
        grants = _grants.Where(g => (string)g["status"]! is "active" or "paused").Select(Shown).ToList(),
    };

    /// <summary>The server cannot be reached when a grant is made (the device is offline).</summary>
    public bool Offline { get; set; }

    private HttpResponseMessage Create(Recorded r)
    {
        if (Offline)
        {
            throw new HttpRequestException("No route to host.");
        }
        if (Refuse is { } code)
        {
            return FakeServer.Respond(RefuseStatus, new { error = new { message = "Not offered.", type = "permission_error", code } });
        }
        var body = r.Json;
        var kind = body.GetProperty("kind").GetString()!;
        if (kind == "bypass" && Challenge(r) is { } challenge)
        {
            return challenge;
        }
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
            ["evidence_level"] = EvidenceLevel ?? (kind == "time" ? "decisions" : "calls"),
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

    /// <summary>More time within the longest grant from its start; a Bypass grant is refused, as the gateway does.</summary>
    private HttpResponseMessage Extend(string id, Recorded r)
    {
        if (_grants.FirstOrDefault(g => (string)g["id"]! == id) is { } found && (string)found["kind"]! == "bypass")
        {
            return FakeServer.Respond(HttpStatusCode.Conflict, new
            {
                error = new { message = "A Bypass grant is not extended.", type = "invalid_request_error", code = "bypass_not_extendable" },
            });
        }
        return Change(id, grant =>
        {
            var minutes = r.Json.TryGetProperty("minutes", out var m) ? m.GetInt32() : 0;
            var issued = (DateTimeOffset)grant["issued_at"]!;
            var wanted = ((DateTimeOffset)grant["expires_at"]!).AddMinutes(minutes);
            var cap = issued.AddMinutes(LongestMinutes);
            grant["expires_at"] = wanted > cap ? cap : wanted;
            grant["narrowed"] = wanted > cap ? new[] { "minutes" } : Array.Empty<string>();
        });
    }

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

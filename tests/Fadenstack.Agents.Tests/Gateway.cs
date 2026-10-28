using System.Net;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;

namespace Fadenstack.Agents.Tests;

/// <summary>A scripted gateway for one agent: a profile, and one answer per model call, in order.</summary>
public sealed class Gateway
{
    public const string Slug = "docs-assistant";
    public const string Chat = "/v1/agents/docs-assistant/chat/completions";

    /// <summary>The test tools' contract, with the classes they declare (<c>stamp</c> has no risk: unclassified).</summary>
    public const string Contract = """
        [{"name":"read_page","risk":"read"},{"name":"add_comment","risk":"write"},{"name":"delete_page","risk":"destructive"},
         {"name":"stamp"},{"name":"render_page","risk":"read"},{"name":"render_slide","risk":"read"},{"name":"boom","risk":"read"}]
        """;

    private readonly Queue<Func<Recorded, HttpResponseMessage>> _answers = new();
    private int _records;

    /// <param name="vision">Whether the agent's model sees pictures, as JSON (<c>null</c>: not said, as an older server).</param>
    /// <param name="skills">The skills its users can pick (<c>GET .../skills</c>; the profile says <c>skills: true</c>). Null:
    /// the server has no skills module, and the route answers <c>available: false</c>.</param>
    /// <param name="ui">The definition's <c>ui</c> (greeting, suggestions, their languages), as JSON.</param>
    /// <param name="provenancePolicy">The agent's marking policy (<c>GET .../provenance/policy</c>), as JSON; null: the
    /// server has no provenance module, and the routes answer 503.</param>
    public Gateway(string? contract = null, string ceiling = "null", string modules = "[]",
        string localMcp = """{"mode":"disable","developer_mode":false}""", string vision = "null", string? userLanguage = null,
        IReadOnlyList<FakeSkill>? skills = null, string ui = "null", string? provenancePolicy = """{"on_missing":"record","short_text_tokens":200}""")
    {
        UserLanguage = userLanguage;
        var skillsModule = skills is null ? "false" : "true";
        ProvenancePolicy = provenancePolicy;
        Server = new FakeServer()
            .On("GET", "/v1/agents/docs-assistant/provenance/policy", _ => ProvenancePolicy is null
                ? ProvenanceOff()
                : FakeServer.Respond(HttpStatusCode.OK, ProvenancePolicy))
            .On("POST", "/v1/agents/docs-assistant/provenance/records", r => ProvenancePolicy is null || ProvenanceDown
                ? ProvenanceOff()
                : FakeServer.Respond(HttpStatusCode.OK, SignedRecord(r.Json)))
            .On("POST", "/v1/agents/docs-assistant/provenance/placements", _ => ProvenancePolicy is null
                ? ProvenanceOff()
                : FakeServer.Respond(HttpStatusCode.Accepted, new { stored = true }))
            .Json("GET", "/v1/provenance/anchors", new
            {
                contract = "provenance/v1", signing_chains = new[] { "-----BEGIN CERTIFICATE-----\ntest" },
                trust_anchors = new[] { "-----BEGIN CERTIFICATE-----\ntest" }, detector = new { id = "fadenstack-provenance/test", methods = new[] { "record" } },
            })
            // The detector: the agent's route takes its token; the user's route refuses an agent token, as the gateway does.
            .On("POST", "/v1/agents/docs-assistant/provenance/verify", r => ProvenancePolicy is null ? ProvenanceOff() : Verify(r))
            .On("POST", "/v1/provenance/verify", r => ProvenancePolicy is null ? ProvenanceOff()
                : r.Header("authorization") == "Bearer agent-token" ? FakeServer.Respond(HttpStatusCode.Forbidden, new
                {
                    error = new
                    {
                        message = "Agent tokens are accepted only on their agent's routes (/v1/agents/{agent}/...).",
                        type = "permission_error", code = "agent_token_not_allowed",
                    },
                })
                : Verify(r))
            .On("GET", "/v1/agents/docs-assistant/profile", _ => FakeServer.Respond(HttpStatusCode.OK,
                Profile(contract, ceiling, modules, localMcp, vision, skillsModule, ui)))
            .Skills("/v1/agents/docs-assistant/skills", skills)
            .On("PATCH", "/v1/agents/docs-assistant/user", r =>
            {
                if (r.Json.TryGetProperty("language", out var language))
                {
                    UserLanguage = language.ValueKind == System.Text.Json.JsonValueKind.String ? language.GetString() : null;
                }
                return FakeServer.Respond(HttpStatusCode.OK, new { @object = "agent.user", language = UserLanguage });
            })
            .Json("POST", "/v1/agents/docs-assistant/events", new { accepted = 1 }, HttpStatusCode.Accepted)
            .Json("PUT", "/v1/agents/docs-assistant/manifest", new { sha256 = "abc", @new = true })
            .Json("POST", "/v1/agents/docs-assistant/approvals/ap-1", new { ok = true })
            .On("POST", Chat, r => _answers.Count == 0
                ? FakeServer.Respond(HttpStatusCode.InternalServerError, new { error = new { message = "unscripted" } })
                : _answers.Dequeue()(r));
    }

    public FakeServer Server { get; }

    /// <summary>The language the user picked for all their clients, as the server keeps it (<c>PATCH .../user</c>).</summary>
    public string? UserLanguage { get; private set; }

    /// <summary>The agent's marking policy as JSON; null: no provenance module (503).</summary>
    public string? ProvenancePolicy { get; set; }

    /// <summary>The profile's <c>principal</c> as JSON; null: an older gateway that names no user.</summary>
    public string? Principal { get; set; }

    /// <summary>The provenance engine is down: records are not signed (503).</summary>
    public bool ProvenanceDown { get; set; }

    /// <summary>The records the agent had signed (<c>POST .../provenance/records</c>).</summary>
    public List<Recorded> RecordRequests => Server.Requests.Where(r => r.Path.EndsWith("/provenance/records")).ToList();

    /// <summary>The placement reports the agent sent (<c>POST .../provenance/placements</c>).</summary>
    public List<Recorded> Placements => Server.Requests.Where(r => r.Path.EndsWith("/provenance/placements")).ToList();

    private static HttpResponseMessage ProvenanceOff() => FakeServer.Respond(HttpStatusCode.ServiceUnavailable, new
    {
        error = new { message = "Content provenance is not switched on here.", type = "provenance_unavailable", code = "provenance_off" },
    });

    /// <summary>A record as the engine signs it: the request's fields, a new id, a test signature.</summary>
    private object SignedRecord(System.Text.Json.JsonElement request)
    {
        string? Str(string name) => request.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var n = Interlocked.Increment(ref _records);
        var kind = Str("edit_kind") ?? "generated";
        return new
        {
            record = new
            {
                contract = "provenance/v1", id = $"5a0e2b5c-7f3e-4a51-9a40-{n:D12}",
                digital_source_type = "http://cv.iptc.org/newscodes/digitalsourcetype/"
                    + (kind == "generated" ? "trainedAlgorithmicMedia" : "compositeWithTrainedAlgorithmicMedia"),
                edit_kind = kind, system = new { name = "Fadenstack", version = "0.5.0" }, provider = "Example GmbH",
                model = Str("model") is { } model ? new { id = model, version = (string?)null } : null,
                created_at = "2026-10-18T09:30:00Z", receipt_id = Str("receipt_id"), media_type = Str("media_type"),
                content_hash = "c0ffee", hash_key_version = 1, text_hash = Str("text_sha256") is null ? null : "7e47",
            },
            signature = "eyJhbGciOiJFUzI1NiJ9.eyJ0ZXN0Ijp0cnVlfQ.c2ln",
            timestamp = (string?)null,
        };
    }

    /// <summary>
    /// The detector as the fake runs it: the text's hash looked up among the records the agent had signed, as the content's
    /// or as the text a reader sees of HTML and Markdown.
    /// </summary>
    private HttpResponseMessage Verify(Recorded request)
    {
        var body = request.Json;
        var text = body.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
        var file = body.TryGetProperty("file", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String ? f.GetString() : null;
        if ((text is null) == (file is null))
        {
            return FakeServer.Respond(HttpStatusCode.BadRequest, new
            {
                error = new { message = "Send either text or file, not both and not neither.", type = "invalid_request_error", code = "validation_error" },
            });
        }
        var sha = text is not null ? Models.ContentHash.TextSha256(text) : Models.ContentHash.BytesSha256(Convert.FromBase64String(file!));
        var n = RecordRequests.FindIndex(r => r.Json.GetProperty("content_sha256").GetString() == sha
            || (r.Json.TryGetProperty("text_sha256", out var plain) && plain.GetString() == sha));
        var id = n < 0 ? null : $"5a0e2b5c-7f3e-4a51-9a40-{n + 1:D12}";
        return FakeServer.Respond(HttpStatusCode.OK, new
        {
            contract = "provenance/v1", verdict = id is null ? "no-mark-found" : "ai-generated",
            findings = new[]
            {
                new
                {
                    layer = "metadata", method = "record-hash", found = id is not null,
                    detail = id is null ? "No record matches the content." : "A signed record of this installation matches the content.",
                    record_id = id, receipt_id = (string?)null, score = (double?)null,
                },
            },
            content_hash = sha, detector = "fadenstack-provenance/test", checked_at = "2026-10-18T09:30:00Z", signature = (string?)null,
        });
    }

    /// <summary>An answer's receipt (<c>faden.provenance</c>): the model's watermark applied, or none and why.</summary>
    public static string Receipt(string id, string applied = "model", string? reason = null, string? algorithm = "gumbel") => Custom("faden.provenance", new
    {
        receipt = new
        {
            contract = "provenance/v1", id, kind = "text", model = new { id = "qwen3.8-27b", version = (string?)null },
            created_at = "2026-10-18T09:30:00Z",
            watermark = new { applied, algorithm = applied == "model" ? algorithm : null, reason },
            output_hash = "ab12", hash_key_version = 1,
        },
        signature = "eyJhbGciOiJFUzI1NiJ9.e30.c2ln",
        timestamp = (string?)null,
    });

    /// <summary>The event of an answer the engine could not sign.</summary>
    public static string Unsigned => Custom("faden.provenance", new { receipt = (object?)null, status = "unsigned", reason = "server-unavailable" });

    /// <summary>A streamed answer with text, then tool calls, and its receipt before the end.</summary>
    public Gateway SaysAndCalls(string text, string receipt, params (string Id, string Name, string Arguments)[] calls) =>
        Then(Text(text), ToolCalls(calls), Finish("tool_calls"), receipt, FakeServer.Done);

    /// <summary>A streamed answer that calls tools, with a receipt before the end (as for an answer with text).</summary>
    public Gateway CallsWithReceipt(string receipt, params (string Id, string Name, string Arguments)[] calls) =>
        Then(ToolCalls(calls), Finish("tool_calls"), receipt, FakeServer.Done);

    /// <summary>The agent profile, with the language the user picked as it is now.</summary>
    private string Profile(string? contract, string ceiling, string modules, string localMcp, string vision, string skills, string ui)
    {
        var language = System.Text.Json.JsonSerializer.Serialize(UserLanguage);
        return $$"""
                {"object":"agent.profile","id":"a-1","slug":"docs-assistant","name":"Docs","version":3,"draft":false,
                 "model":"qwen3-32b","vision":{{vision}},"images":false,"skills":{{skills}},"ui":{{ui}},
                 "host_tool_contract":{{contract ?? Contract}},"local_mcp":{{localMcp}},
                 "tool_modes":{"default":"ask","ceiling":{{ceiling}},"open_world_enforced":false},
                 "required_client_modules":{{modules}},"edition":"community","features":["agents"],
                 "user":{"language":{{language}} },"principal":{{Principal ?? "null"}} }
                """;
    }

    public List<Recorded> ChatRequests => Server.Requests.Where(r => r.Path == Chat).ToList();

    /// <summary>The times the skills were listed (<c>GET .../skills</c>).</summary>
    public List<Recorded> SkillRequests => Server.Requests.Where(r => r.Path.EndsWith("/skills")).ToList();

    public List<Recorded> Reports => Server.Requests.Where(r => r.Path.EndsWith("/events")).ToList();

    /// <summary>The manifests the app sent (<c>PUT .../manifest</c>).</summary>
    public List<Recorded> Manifests => Server.Requests.Where(r => r.Path.EndsWith("/manifest")).ToList();

    public Gateway Then(Func<Recorded, HttpResponseMessage> answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    public Gateway Then(params string[] sse) => Then(_ => FakeServer.Sse(sse));

    /// <summary>A streamed answer with text.</summary>
    public Gateway Says(string text) => Then(Text(text), Finish("stop"), FakeServer.Done);

    /// <summary>A streamed answer that calls tools.</summary>
    public Gateway Calls(params (string Id, string Name, string Arguments)[] calls) =>
        Then(ToolCalls(calls), Finish("tool_calls"), FakeServer.Done);

    public static string Text(string text) =>
        FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content = text } } } });

    public static string ToolCalls(params (string Id, string Name, string Arguments)[] calls) =>
        FakeServer.Data(new
        {
            id = "c1",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = calls.Select((c, i) => new { index = i, id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Arguments } }),
                    },
                },
            },
        });

    public static string Finish(string reason) =>
        FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } });

    public static string Custom(string name, object value) => FakeServer.Fadenstack(new { type = "CUSTOM", name, value });

    /// <summary>A builder for this gateway; sessions and "Allow always" in memory (never the user's data folder).</summary>
    public AgentBuilder Builder(ISessionStore? store = null, IApprovalStore? approvals = null) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(Server))
        .UseAgent(Slug)
        .UseSessionStore(store ?? new MemorySessionStore())
        .UseApprovalStore(approvals ?? new MemoryApprovalStore());

    /// <summary>The events the agent reported, in order.</summary>
    public List<System.Text.Json.JsonElement> Reported =>
        Reports.SelectMany(r => r.Json.GetProperty("events").EnumerateArray()).ToList();
}

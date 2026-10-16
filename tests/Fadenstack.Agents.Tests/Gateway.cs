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

    /// <param name="vision">Whether the agent's model sees pictures, as JSON (<c>null</c>: not said, as an older server).</param>
    /// <param name="skills">The skills its users can pick (<c>GET .../skills</c>; the profile says <c>skills: true</c>). Null:
    /// the server has no skills module, and the route answers <c>available: false</c>.</param>
    public Gateway(string? contract = null, string ceiling = "null", string modules = "[]",
        string localMcp = """{"mode":"disable","developer_mode":false}""", string vision = "null", string? userLanguage = null,
        IReadOnlyList<FakeSkill>? skills = null)
    {
        UserLanguage = userLanguage;
        var skillsModule = skills is null ? "false" : "true";
        Server = new FakeServer()
            .On("GET", "/v1/agents/docs-assistant/profile", _ => FakeServer.Respond(HttpStatusCode.OK,
                Profile(contract, ceiling, modules, localMcp, vision, skillsModule)))
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

    /// <summary>The agent profile, with the language the user picked as it is now.</summary>
    private string Profile(string? contract, string ceiling, string modules, string localMcp, string vision, string skills)
    {
        var language = System.Text.Json.JsonSerializer.Serialize(UserLanguage);
        return $$"""
                {"object":"agent.profile","id":"a-1","slug":"docs-assistant","name":"Docs","version":3,"draft":false,
                 "model":"qwen3-32b","vision":{{vision}},"images":false,"skills":{{skills}},
                 "host_tool_contract":{{contract ?? Contract}},"local_mcp":{{localMcp}},
                 "tool_modes":{"default":"ask","ceiling":{{ceiling}},"open_world_enforced":false},
                 "required_client_modules":{{modules}},"edition":"community","features":["agents"],
                 "user":{"language":{{language}} } }
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

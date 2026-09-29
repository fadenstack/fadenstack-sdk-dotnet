using System.Net;
using Faden.Agents;
using Faden.Agents.Sessions;
using Faden.Auth;
using Faden.Client.Tests;

namespace Faden.Agents.Tests;

/// <summary>A scripted gateway for one agent: a profile, and one answer per model call, in order.</summary>
public sealed class Gateway
{
    public const string Slug = "eplan-assistant";
    public const string Chat = "/v1/agents/eplan-assistant/chat/completions";
    private readonly Queue<Func<Recorded, HttpResponseMessage>> _answers = new();

    public Gateway(string? contract = null, string ceiling = "null", string modules = "[]",
        string localMcp = """{"mode":"disable","developer_mode":false}""")
    {
        Server = new FakeServer()
            .Json("GET", "/v1/agents/eplan-assistant/profile", $$"""
                {"object":"agent.profile","id":"a-1","slug":"eplan-assistant","name":"EPLAN","version":3,"draft":false,
                 "model":"qwen3-32b","host_tool_contract":{{contract ?? "[]"}},"local_mcp":{{localMcp}},
                 "tool_modes":{"default":"ask","ceiling":{{ceiling}},"open_world_enforced":false},
                 "required_client_modules":{{modules}},"edition":"community","features":["agents"]}
                """)
            .Json("POST", "/v1/agents/eplan-assistant/events", new { accepted = 1 }, HttpStatusCode.Accepted)
            .Json("POST", "/v1/agents/eplan-assistant/approvals/ap-1", new { ok = true })
            .On("POST", Chat, r => _answers.Count == 0
                ? FakeServer.Respond(HttpStatusCode.InternalServerError, new { error = new { message = "unscripted" } })
                : _answers.Dequeue()(r));
    }

    public FakeServer Server { get; }

    public List<Recorded> ChatRequests => Server.Requests.Where(r => r.Path == Chat).ToList();

    public List<Recorded> Reports => Server.Requests.Where(r => r.Path.EndsWith("/events")).ToList();

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

    public static string Custom(string name, object value) => FakeServer.Faden(new { type = "CUSTOM", name, value });

    public AgentBuilder Builder(ISessionStore? store = null) => FadenAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(Server))
        .UseAgent(Slug)
        .UseSessionStore(store ?? new MemorySessionStore());
}

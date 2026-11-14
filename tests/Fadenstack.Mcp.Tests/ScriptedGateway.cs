using System.Net;
using System.Text;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// A gateway in memory for the agent's tests: the profile of one agent, <c>GET .../tools</c> as the test sets it, and one scripted
/// answer per model call (streamed text, in order). It keeps the tool names every model call was offered.
/// </summary>
internal sealed class ScriptedGateway : HttpMessageHandler
{
    public const string Slug = "desktop-assistant";

    private readonly Queue<string> _answers = new();
    private readonly List<string> _paths = [];

    /// <param name="contract">The tool contract as JSON; empty offers everything.</param>
    /// <param name="localMcp">The profile's <c>local_mcp</c> as JSON.</param>
    public ScriptedGateway(string contract = "[]", string localMcp = """{"mode":"disable","developer_mode":false}""")
    {
        Contract = contract;
        LocalMcp = localMcp;
    }

    public string Contract { get; }

    public string LocalMcp { get; }

    /// <summary>What <c>GET .../tools</c> answers: a status and a body (404: an older server without the route).</summary>
    public (HttpStatusCode Status, string Body) Tools { get; set; } = (HttpStatusCode.NotFound, """{"detail":"Not Found"}""");

    /// <summary>The names of the tools every model call was offered, oldest first.</summary>
    public List<IReadOnlyList<string>> Offered { get; } = [];

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_paths)
                return [.. _paths];
        }
    }

    public ScriptedGateway Says(string text)
    {
        _answers.Enqueue(Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content = text } } } })
            + Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }) + "data: [DONE]\n\n");
        return this;
    }

    /// <summary>The next model call asks for tools (id, name, JSON arguments).</summary>
    public ScriptedGateway Calls(params (string Id, string Name, string Arguments)[] calls)
    {
        _answers.Enqueue(Data(new
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
            })
            + Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n");
        return this;
    }

    public AgentBuilder Builder() => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(this))
        .UseAgent(Slug)
        .UseSessionStore(new MemorySessionStore())
        .UseApprovalStore(new MemoryApprovalStore());

    private static string Data(object chunk) => $"data: {JsonSerializer.Serialize(chunk)}\n\n";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        lock (_paths)
            _paths.Add($"{request.Method} {path}");
        var agent = $"/v1/agents/{Slug}";
        if (path == $"{agent}/profile")
            return Json(HttpStatusCode.OK, Profile());
        if (path == $"{agent}/tools")
            return Json(Tools.Status, Tools.Body);
        if (path == $"{agent}/events")
            return Json(HttpStatusCode.Accepted, """{"accepted":1}""");
        if (path == $"{agent}/manifest")
            return Json(HttpStatusCode.OK, """{"sha256":"abc","new":true}""");
        if (path == $"{agent}/chat/completions")
        {
            using var json = JsonDocument.Parse(body);
            var names = json.RootElement.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList()
                : [];
            lock (Offered)
                Offered.Add(names);
            var answer = _answers.Count > 0
                ? _answers.Dequeue()
                : Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content = "Ok." } } } }) + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "text/event-stream") };
        }
        return Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}""");
    }

    private string Profile() => $$"""
        {"object":"agent.profile","id":"a-1","slug":"{{Slug}}","name":"Desktop","version":3,"draft":false,"model":"qwen3","vision":false,
         "images":false,"skills":false,"ui":{"delegates":["word-assistant"]},"host_tool_contract":{{Contract}},"local_mcp":{{LocalMcp}},
         "tool_modes":{"default":"ask","ceiling":null,"open_world_enforced":false},"required_client_modules":[],"edition":"community",
         "features":["agents"],"user":{"language":null},"principal":null}
        """;
}

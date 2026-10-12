using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fadenstack.Samples.Wpf;

/// <summary>
/// A stand-in gateway, so the chat can be tried without a server: it streams Markdown word by word and calls
/// the sample's host tools ("read page 3", "add a comment", "delete page 2", "stamp").
/// </summary>
internal sealed class DemoGateway : HttpMessageHandler
{
    public const string Agent = "demo-assistant";

    private const string Showcase = """
        ## What I can do here

        I work **inside the host application** with its own tools, and I ask before anything that changes something.

        1. Read pages: *"read page 3"*
        2. Change them: *"add a comment to page 2"* (asks in **Ask** mode)
           - Auto mode runs it without asking
           - Read only mode hides it
        3. Delete: *"delete page 2"* -- always asks

        | Tool | Class | In Ask mode |
        |:-----|:-----:|------------:|
        | `read_page` | read | runs |
        | `add_comment` | write | asks |
        | `delete_page` | destructive | asks |

        ```csharp
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(server, tokens)
            .UseAgent("docs-assistant")
            .AddTools(new PageTools())
            .Build();
        ```

        > Sessions stay on this computer; the server keeps usage, never content.
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/profile", StringComparison.Ordinal))
        {
            return Json("""
                {"id":"demo","slug":"demo-assistant","name":"Demo assistant","version":1,
                 "host_tool_contract":[{"name":"read_page","risk":"read"},{"name":"add_comment","risk":"write"},
                                       {"name":"delete_page","risk":"destructive"},{"name":"stamp_page"}],
                 "local_mcp":{"mode":"disable","developer_mode":false},"tool_modes":{"default":"ask","ceiling":null},
                 "ui":{"greeting":"A demo without a server. Try one of these:",
                       "suggestions":["What can you do?","Read page 3","Add a comment to page 2","Delete page 2"]},
                 "required_client_modules":[],"edition":"community","features":[]}
                """);
        }
        if (path.EndsWith("/events", StringComparison.Ordinal) || path.Contains("/approvals/"))
        {
            return Json("{}");
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var messages = body["messages"]!.AsArray();
        var last = messages[messages.Count - 1]!;
        var offered = body["tools"]?.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet() ?? new HashSet<string>();
        if ((string?)last["role"] == "tool")
        {
            return Stream(Words($"Done. The tool said: *{(string?)last["content"]}*"), cancellationToken);
        }
        var text = ((string?)last["content"] ?? "").ToLowerInvariant();
        var page = new string(text.Where(char.IsDigit).ToArray()) is { Length: > 0 } n ? int.Parse(n) : 1;
        string? tool = text.Contains("delete") ? "delete_page" : text.Contains("comment") ? "add_comment"
            : text.Contains("stamp") ? "stamp_page" : text.Contains("read") ? "read_page" : null;
        if (tool is not null && offered.Contains(tool))
        {
            var args = tool == "stamp_page" ? "{}" : JsonSerializer.Serialize(new { page });
            return Stream(new[]
            {
                Chunk(new { content = "Let me do that." }),
                Chunk(new { tool_calls = new[] { new { index = 0, id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8), type = "function", function = new { name = tool, arguments = args } } } }),
                Chunk(new { }, "tool_calls"),
            }, cancellationToken);
        }
        if (tool is not null)
        {
            return Stream(Words($"`{tool}` is not available in this tool mode. Pick another mode at the top."), cancellationToken);
        }
        return Stream(Words(Showcase), cancellationToken);
    }

    private static IEnumerable<string> Words(string text)
    {
        var parts = text.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            yield return Chunk(new { content = (i == 0 ? "" : " ") + parts[i] });
        }
        yield return Chunk(new { }, "stop");
    }

    private static string Chunk(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { id = "demo", choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>Streams the chunks with a small delay, as a model would.</summary>
    private static HttpResponseMessage Stream(IEnumerable<string> chunks, CancellationToken cancellationToken)
    {
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var reader = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        _ = Task.Run(async () =>
        {
            using (pipe)
            {
                foreach (var chunk in chunks.Append("data: [DONE]\n\n"))
                {
                    var bytes = Encoding.UTF8.GetBytes(chunk);
                    await pipe.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    await pipe.FlushAsync(cancellationToken);
                    await Task.Delay(18, cancellationToken);
                }
            }
        }, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(reader) };
    }
}

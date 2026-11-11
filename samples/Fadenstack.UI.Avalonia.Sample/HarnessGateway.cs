using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Samples.Wpf;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
/// The demo gateway plus one thing: a message that starts with <see cref="StreamLong"/> is answered with a long
/// Markdown answer (paragraphs, a list, a table, a code block of 60 lines) streamed word by word, so the harness
/// can watch the chat while an answer grows. Everything else goes to the demo gateway.
/// </summary>
internal sealed class HarnessGateway : HttpMessageHandler
{
    public const string StreamLong = "stream long";

    private readonly HttpMessageInvoker _demo = new(new DemoGateway());

    /// <summary>The pause between two words; a model streams at 30 to 60 tokens a second.</summary>
    public TimeSpan WordDelay { get; set; } = TimeSpan.FromMilliseconds(15);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Content is not null && !path.EndsWith("/profile", StringComparison.Ordinal) && !path.EndsWith("/events", StringComparison.Ordinal)
            && !path.Contains("/approvals/"))
        {
            await request.Content.LoadIntoBufferAsync();
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!;
            var messages = body["messages"]!.AsArray();
            if (((string?)messages[messages.Count - 1]!["content"])?.StartsWith(StreamLong, StringComparison.OrdinalIgnoreCase) == true)
            {
                return Stream(Answer().Split(' '), cancellationToken);
            }
        }
        return await _demo.SendAsync(request, cancellationToken);
    }

    /// <summary>The answer: about 900 words, a code block in the middle.</summary>
    internal static string Answer()
    {
        var random = new Random(3);
        var text = new StringBuilder("## A long answer\n\n");
        for (var p = 0; p < 6; p++)
        {
            text.Append(SyntheticSession.Words(random, 70)).Append("\n\n");
        }
        text.Append("- ").Append(SyntheticSession.Words(random, 9)).Append("\n- ").Append(SyntheticSession.Words(random, 9)).Append("\n- ")
            .Append(SyntheticSession.Words(random, 9)).Append("\n\n");
        text.Append(SyntheticSession.CodeBlock(random, 60)).Append('\n');
        text.Append("| Step | Result |\n|:-----|-------:|\n| one | 1 |\n| two | 2 |\n\n");
        for (var p = 0; p < 4; p++)
        {
            text.Append(SyntheticSession.Words(random, 70)).Append("\n\n");
        }
        return text.ToString();
    }

    private HttpResponseMessage Stream(string[] words, CancellationToken cancellationToken)
    {
        var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        var reader = new AnonymousPipeClientStream(PipeDirection.In, pipe.ClientSafePipeHandle);
        var delay = WordDelay;
        _ = Task.Run(async () =>
        {
            using (pipe)
            {
                for (var i = 0; i < words.Length; i++)
                {
                    await Write(pipe, Chunk(new { content = (i == 0 ? "" : " ") + words[i] }), cancellationToken);
                    await Task.Delay(delay, cancellationToken);
                }
                await Write(pipe, Chunk(new { }, "stop"), cancellationToken);
                await Write(pipe, "data: [DONE]\n\n", cancellationToken);
            }
        }, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(reader) };
    }

    private static async Task Write(Stream to, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await to.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await to.FlushAsync(cancellationToken);
    }

    private static string Chunk(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { id = "harness", choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";
}

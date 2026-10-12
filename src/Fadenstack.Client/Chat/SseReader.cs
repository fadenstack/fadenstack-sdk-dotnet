using System.Text;

namespace Fadenstack.Chat;

/// <summary>One server-sent event: its name (<c>event:</c>, null for the OpenAI chunks) and its data.</summary>
internal readonly struct SseEvent
{
    public SseEvent(string? name, string data)
    {
        Name = name;
        Data = data;
    }

    public string? Name { get; }

    public string Data { get; }
}

/// <summary>Reads <c>text/event-stream</c> event by event.</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(name, data.ToString());
                }
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(name, data.ToString());
                }
                name = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':')
            {
                continue; // a comment, e.g. a keep-alive
            }
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line.Substring(0, colon);
            var value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }
            if (field == "event")
            {
                name = value;
            }
            else if (field == "data")
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
        }
    }
}

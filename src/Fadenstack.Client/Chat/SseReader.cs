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

/// <summary>
/// How much one stream may send (N9): a faulty or hostile server, or a proxy, must not make the client hold unbounded text.
/// Past a limit the stream ends with a <see cref="FadenstackStreamException"/> (code <c>stream_too_large</c>).
/// </summary>
internal sealed record SseLimits
{
    /// <summary>The longest line, in characters.</summary>
    public int MaxLineChars { get; init; } = 4 * 1024 * 1024;

    /// <summary>The most one event's data may hold, in characters.</summary>
    public int MaxEventChars { get; init; } = 8 * 1024 * 1024;

    /// <summary>The most the whole stream may send, in characters; null: no limit (the gateway's own stream).</summary>
    public long? MaxTotalChars { get; init; }

    /// <summary>The gateway's limits: lines and events only.</summary>
    public static SseLimits Gateway { get; } = new();

    /// <summary>A person's own endpoint, which may be anything: the whole answer is limited too.</summary>
    public static SseLimits Endpoint { get; } = new() { MaxTotalChars = 64L * 1024 * 1024 };
}

/// <summary>Reads <c>text/event-stream</c> event by event, within <see cref="SseLimits"/>.</summary>
internal static class SseReader
{
    public static IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, SseLimits.Gateway, cancellationToken);

    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, SseLimits limits,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new LineReader(reader, limits);
        string? name = null;
        var data = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await lines.ReadAsync().ConfigureAwait(false);
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
                if (data.Length + value.Length + 1 > limits.MaxEventChars)
                {
                    throw TooLarge("an event");
                }
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
        }
    }

    internal static FadenstackStreamException TooLarge(string what) =>
        new($"The answer broke off: {what} was larger than the SDK accepts.", "stream_too_large");

    /// <summary>Lines ended by LF, CRLF or CR (the SSE rule), each at most <see cref="SseLimits.MaxLineChars"/> long.</summary>
    private sealed class LineReader
    {
        private readonly StreamReader _reader;
        private readonly SseLimits _limits;
        private readonly char[] _buffer = new char[8192];
        private int _start;
        private int _end;
        private bool _skipLf;
        private long _total;

        public LineReader(StreamReader reader, SseLimits limits)
        {
            _reader = reader;
            _limits = limits;
        }

        public async Task<string?> ReadAsync()
        {
            StringBuilder? line = null;
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await _reader.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
                    if (_end == 0)
                    {
                        return line?.ToString();
                    }
                    _total += _end;
                    if (_limits.MaxTotalChars is { } max && _total > max)
                    {
                        throw TooLarge("the whole answer");
                    }
                }
                if (_skipLf)
                {
                    _skipLf = false;
                    if (_buffer[_start] == '\n')
                    {
                        _start++;
                        continue;
                    }
                }
                var i = _start;
                while (i < _end && _buffer[i] != '\n' && _buffer[i] != '\r')
                {
                    i++;
                }
                var length = (line?.Length ?? 0) + (i - _start);
                if (length > _limits.MaxLineChars)
                {
                    throw TooLarge("a line");
                }
                if (i == _end)
                {
                    (line ??= new StringBuilder()).Append(_buffer, _start, i - _start);
                    _start = _end;
                    continue;
                }
                var text = line is null ? new string(_buffer, _start, i - _start) : line.Append(_buffer, _start, i - _start).ToString();
                _skipLf = _buffer[i] == '\r';
                _start = i + 1;
                return text;
            }
        }
    }
}

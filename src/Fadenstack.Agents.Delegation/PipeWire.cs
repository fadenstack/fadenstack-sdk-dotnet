using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fadenstack.Agents.Delegation;

// The local pipe's wire: one JSON object per line (newline-delimited JSON), UTF-8, at most MaxBytes a line.
//
//   caller -> agent   hello {version, secret}            agent -> caller   ready {slug}
//   caller -> agent   task/start {taskId, message{text, references}, from{slug, name}}
//   agent -> caller   task/status {taskId, state, text?}
//   agent -> caller   task/approval {taskId, approvalId, tool, toolClass, summary, argumentsPreview}   (state input-required)
//   caller -> agent   task/approve {taskId, approvalId, decision: once | chat | deny}
//   agent -> caller   task/artifact {taskId, text}       (then the final task/status)
//   caller -> agent   task/cancel {taskId}

/// <summary>The message types of the pipe wire.</summary>
internal static class FrameTypes
{
    public const string Hello = "hello";
    public const string Ready = "ready";
    public const string TaskStart = "task/start";
    public const string TaskStatus = "task/status";
    public const string TaskApproval = "task/approval";
    public const string TaskApprove = "task/approve";
    public const string TaskArtifact = "task/artifact";
    public const string TaskCancel = "task/cancel";
}

/// <summary>One wire message; the fields a type does not use stay out of the JSON.</summary>
internal sealed class Frame
{
    public string Type { get; set; } = "";
    public int? Version { get; set; }
    public string? Secret { get; set; }
    public string? Slug { get; set; }
    public string? TaskId { get; set; }
    public FrameMessage? Message { get; set; }
    public FrameParty? From { get; set; }
    public string? State { get; set; }
    public string? Text { get; set; }
    public string? ApprovalId { get; set; }
    public string? Tool { get; set; }
    public string? ToolClass { get; set; }
    public string? Summary { get; set; }
    public string? ArgumentsPreview { get; set; }
    public string? Decision { get; set; }
}

internal sealed class FrameMessage
{
    public string Text { get; set; } = "";
    public List<string> References { get; set; } = new();
}

internal sealed class FrameParty
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A message that is too large, not JSON, or not one this side accepts: the connection ends.</summary>
internal sealed class WireException : Exception
{
    public WireException(string message) : base(message)
    {
    }
}

/// <summary>Frames over a stream: bounded lines in, serialised lines out (one writer at a time).</summary>
internal sealed class FrameStream
{
    internal const int DefaultMaxBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;
    private readonly int _maxBytes;
    private readonly byte[] _chunk = new byte[8192];
    private readonly MemoryStream _line = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private int _chunkStart, _chunkEnd;

    public FrameStream(Stream stream, int maxBytes = DefaultMaxBytes)
    {
        _stream = stream;
        _maxBytes = maxBytes;
    }

    /// <summary>The next message; null when the other side closed the connection.</summary>
    public async Task<Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        _line.SetLength(0);
        while (true)
        {
            if (_chunkStart == _chunkEnd)
            {
                _chunkEnd = await _stream.ReadAsync(_chunk, 0, _chunk.Length, cancellationToken).ConfigureAwait(false);
                _chunkStart = 0;
                if (_chunkEnd == 0)
                {
                    return null;
                }
            }
            var newline = Array.IndexOf(_chunk, (byte)'\n', _chunkStart, _chunkEnd - _chunkStart);
            var count = (newline < 0 ? _chunkEnd : newline) - _chunkStart;
            if (_line.Length + count > _maxBytes)
            {
                throw new WireException($"A message is larger than {_maxBytes} bytes.");
            }
            _line.Write(_chunk, _chunkStart, count);
            if (newline < 0)
            {
                _chunkStart = _chunkEnd;
                continue;
            }
            _chunkStart = newline + 1;
            if (_line.Length == 0)
            {
                continue; // an empty line is nothing
            }
            try
            {
                var frame = JsonSerializer.Deserialize<Frame>(Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length), Json);
                return frame is { Type.Length: > 0 } ? frame : throw new WireException("A message has no type.");
            }
            catch (JsonException)
            {
                throw new WireException("A message is not valid JSON.");
            }
        }
    }

    public async Task WriteAsync(Frame frame, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, Json) + "\n");
        if (bytes.Length > _maxBytes + 1)
        {
            throw new WireException($"A message is larger than {_maxBytes} bytes.");
        }
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }
}

internal static class SecretCompare
{
    /// <summary>Equal in constant time for equal lengths (the length of a launch's secret is not secret).</summary>
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        var (x, y) = (Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
        var diff = x.Length ^ y.Length;
        for (var i = 0; i < x.Length && i < y.Length; i++)
        {
            diff |= x[i] ^ y[i];
        }
        return diff == 0;
    }
}

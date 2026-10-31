using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fadenstack.Agents.Delegation;

// The local pipe's wire: one JSON object per line (newline-delimited JSON), UTF-8, at most MaxBytes a line.
//
//   caller -> agent   {"type":"hello","version":1,"secret":"..."}     agent -> caller   {"type":"ready","version":1,"slug":"..."}
//
// After that handshake (transport security, ours) every line is a JSON-RPC 2.0 request from the caller or a response
// from the agent in A2A 1.0 form (A2A.JsonRpcRequest / A2A.JsonRpcResponse, A2AJsonUtilities options): the methods
// SendStreamingMessage, CancelTask, GetTask and GetExtendedAgentCard (A2AMethods). A streaming request is answered by one
// response line per StreamResponse, all with the request's id.

/// <summary>The message types of the pipe wire.</summary>
internal static class FrameTypes
{
    public const string Hello = "hello";
    public const string Ready = "ready";
}

/// <summary>The handshake message: <c>hello</c> with the launch's secret, answered by <c>ready</c>.</summary>
internal sealed class Frame
{
    public string Type { get; set; } = "";
    public int? Version { get; set; }
    public string? Secret { get; set; }
    public string? Slug { get; set; }
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

    /// <summary>The next handshake message; null when the other side closed the connection.</summary>
    public async Task<Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }
        try
        {
            var frame = JsonSerializer.Deserialize<Frame>(line, Json);
            return frame is { Type.Length: > 0 } ? frame : throw new WireException("A message has no type.");
        }
        catch (JsonException)
        {
            throw new WireException("A message is not valid JSON.");
        }
    }

    public Task WriteAsync(Frame frame, CancellationToken cancellationToken) =>
        WriteLineAsync(JsonSerializer.Serialize(frame, Json), cancellationToken);

    /// <summary>The next line (at most the limit); null when the other side closed the connection.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
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
            return Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
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

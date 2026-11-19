using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fadenstack.Client.Tests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// A body that sends its first chunk and then waits; when the reader closes it (a Stop closes the stream under the read), the read throws
/// the <see cref="IOException"/> a socket gives ("... aborted because of either a thread exit or an application request") and not
/// an <see cref="OperationCanceledException"/>. For the tests of a stop that the transport reports as an I/O error.
/// </summary>
public sealed class AbortedStream : Stream
{
    private readonly byte[] _first;
    private readonly TaskCompletionSource<int> _wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _sent;

    public AbortedStream(string firstChunk)
    {
        _first = Encoding.UTF8.GetBytes(firstChunk);
    }

    /// <summary>The first chunk was read: the turn is on its way.</summary>
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The reader asked for more after the first chunk and is parked on that read: a stop now is a stop under the read.</summary>
    public TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The answer of a gateway whose stream behaves this way.</summary>
    public static Func<Recorded, HttpResponseMessage> Answer(string firstChunk, out AbortedStream stream)
    {
        var body = stream = new AbortedStream(firstChunk);
        return _ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (!_sent)
        {
            _sent = true;
            var n = Math.Min(count, _first.Length);
            Array.Copy(_first, 0, buffer, offset, n);
            Started.TrySetResult(true);
            return n;
        }
        Waiting.TrySetResult(true);
        return await _wait.Task.ConfigureAwait(false); // the cancellation token is ignored on purpose: a dead read cannot be cancelled
    }

    protected override void Dispose(bool disposing)
    {
        _wait.TrySetException(new IOException("The I/O operation has been aborted because of either a thread exit or an application request."));
        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

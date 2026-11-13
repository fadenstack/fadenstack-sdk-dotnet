namespace Fadenstack.Http;

/// <summary>
/// A response stream that is given up when nothing arrives for a while: a watchdog disposes the stream underneath (a read
/// on a dead connection cannot be cancelled otherwise), and every read from then on throws a
/// <see cref="FadenstackException"/>, so a disposed stream never passes for the end of the answer.
/// </summary>
internal sealed class IdleTimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idle;
    private readonly Timer? _watchdog;
    private volatile bool _timedOut;

    public IdleTimeoutStream(Stream inner, TimeSpan idle)
    {
        _inner = inner;
        _idle = idle;
        if (idle != Timeout.InfiniteTimeSpan)
        {
            _watchdog = new Timer(_ => GiveUp(), null, idle, Timeout.InfiniteTimeSpan);
        }
    }

    private void GiveUp()
    {
        _timedOut = true;
        _inner.Dispose();
    }

    /// <summary>Data arrived: the wait starts again.</summary>
    private void Alive()
    {
        try
        {
            _watchdog?.Change(_idle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // disposed meanwhile
        }
    }

    private FadenstackException TimedOut(Exception? inner = null) =>
        new($"The server sent nothing for {_idle.TotalSeconds:0} s; the answer was given up.", inner);

    private int After(int read)
    {
        if (_timedOut)
        {
            throw TimedOut(); // a disposed stream may report its end
        }
        if (read > 0)
        {
            Alive();
        }
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_timedOut)
        {
            throw TimedOut();
        }
        try
        {
            return After(_inner.Read(buffer, offset, count));
        }
        catch (Exception e) when (_timedOut && e is not FadenstackException)
        {
            throw TimedOut(e);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_timedOut)
        {
            throw TimedOut();
        }
        try
        {
            return After(await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (_timedOut && e is not FadenstackException)
        {
            throw TimedOut(e);
        }
    }

#if NET
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_timedOut)
        {
            throw TimedOut();
        }
        try
        {
            return After(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (_timedOut && e is not FadenstackException)
        {
            throw TimedOut(e);
        }
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog?.Dispose();
            _inner.Dispose();
        }
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

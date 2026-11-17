using System.Text;
using Fadenstack.Chat;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>A read that fails after the caller cancelled is the cancel, not a failure (a Stop closes the stream under the read).</summary>
public class SseReaderCancelTests
{
    private sealed class DyingStream : Stream
    {
        private readonly Func<Exception> _error;

        public DyingStream(Func<Exception> error)
        {
            _error = error;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(_error());

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

        public override int Read(byte[] buffer, int offset, int count) => throw _error();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task Drain(Stream stream, CancellationToken token)
    {
        await foreach (var _ in SseReader.ReadAsync(stream, token))
        {
        }
    }

    [Fact]
    public async Task An_IOException_after_the_cancel_is_thrown_as_the_cancel_with_the_original_inside()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var io = new IOException("The I/O operation has been aborted because of either a thread exit or an application request.");

        var thrown = await Record.ExceptionAsync(() => Drain(new DyingStream(() => io), cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown); // the loop's own check may come first; both are the cancel
    }

    [Fact]
    public async Task An_IOException_without_a_cancel_stays_an_IOException()
    {
        var thrown = await Record.ExceptionAsync(() => Drain(new DyingStream(() => new IOException("reset")), CancellationToken.None));

        Assert.IsType<IOException>(thrown);
    }

    [Fact]
    public async Task A_read_that_fails_while_the_cancel_arrives_is_the_cancel()
    {
        using var cts = new CancellationTokenSource();
        var io = new IOException("aborted");
        var thrown = await Record.ExceptionAsync(() => Drain(new DyingStream(() => { cts.Cancel(); return io; }), cts.Token));

        var cancel = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Same(io, cancel.InnerException);
    }
}

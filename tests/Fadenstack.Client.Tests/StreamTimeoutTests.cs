using System.Net;
using System.Text;
using Fadenstack.Auth;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A streamed answer cannot hang for ever: the wait for its headers has the request timeout, and a stream that goes
/// silent is given up after the idle timeout (sdk/51 I00 #6, I-8). A keep-alive comment counts as life.
/// </summary>
public class StreamTimeoutTests
{
    private const string Route = "/v1/agents/docs-assistant/chat/completions";
    private static readonly ChatMessage[] Hi = [new ChatMessage(ChatRole.User, "hi")];

    private static FadenstackClient Make(HttpMessageHandler server, TimeSpan timeout, TimeSpan idle) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"),
        Agent = "docs-assistant",
        Credentials = new StaticTokenProvider("agent-token"),
        // The SDK's own client has no timeout of its own; neither has this one.
        HttpClient = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan },
        Timeout = timeout,
        StreamIdleTimeout = idle,
    });

    private static async Task<string> ReadAllAsync(FadenstackClient client, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var update in client.Chat.GetStreamingResponseAsync(Hi, cancellationToken: cancellationToken))
        {
            text.Append(update.Text);
        }
        return text.ToString();
    }

    private static string Chunk(string content, string? finish = null) =>
        FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content }, finish_reason = finish } } });

    [Fact]
    public async Task A_server_that_never_sends_the_headers_ends_with_a_timeout()
    {
        using var client = Make(new Handler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), timeout: TimeSpan.FromMilliseconds(300), idle: TimeSpan.FromMinutes(5));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // the test's own limit, not the SDK's

        var error = await Assert.ThrowsAsync<FadenstackException>(() => ReadAllAsync(client, guard.Token));

        Assert.Contains("did not answer within", error.Message);
        Assert.False(guard.IsCancellationRequested);
    }

    [Fact]
    public async Task A_stream_that_goes_silent_ends_with_an_idle_timeout()
    {
        using var client = Make(new Handler(_ => Task.FromResult(Streaming(Stalling.After(Chunk("Hel"))))),
            timeout: TimeSpan.FromMinutes(5), idle: TimeSpan.FromMilliseconds(400));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var error = await Assert.ThrowsAsync<FadenstackException>(() => ReadAllAsync(client, guard.Token));

        Assert.Contains("sent nothing for", error.Message);
        Assert.False(guard.IsCancellationRequested);
    }

    [Fact]
    public async Task Keep_alive_comments_keep_a_slow_stream_alive_and_the_headers_timeout_does_not_cut_it()
    {
        // 8 keep-alives 150 ms apart: 1.2 s in all, longer than both limits, never silent for as long as the idle one.
        var events = Enumerable.Repeat(": keep-alive\n\n", 8).Concat(new[] { Chunk("Done."), Chunk("", "stop"), FakeServer.Done });
        using var client = Make(new Handler(_ => Task.FromResult(Streaming(new Trickling(events, TimeSpan.FromMilliseconds(150))))),
            timeout: TimeSpan.FromMilliseconds(500), idle: TimeSpan.FromMilliseconds(500));

        Assert.Equal("Done.", await ReadAllAsync(client, CancellationToken.None));
    }

    private static HttpResponseMessage Streaming(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } },
    };

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(cancellationToken);
    }

    /// <summary>Sends its bytes, then nothing, until it is disposed (as a dead connection would).</summary>
    private sealed class Stalling : Stream
    {
        private readonly MemoryStream _first;
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Stalling(byte[] first) => _first = new MemoryStream(first);

        public static Stalling After(string text) => new(Encoding.UTF8.GetBytes(text));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = _first.Read(buffer, offset, count);
            if (read > 0)
            {
                return read;
            }
            using (cancellationToken.Register(() => _closed.TrySetCanceled()))
            {
                return await _closed.Task;
            }
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = new byte[buffer.Length];
            var read = await ReadAsync(bytes, 0, bytes.Length, cancellationToken);
            bytes.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
#endif

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetException(new ObjectDisposedException(nameof(Stalling)));
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends one event at a time, with a pause before each.</summary>
    private sealed class Trickling(IEnumerable<string> events, TimeSpan pause) : Stream
    {
        private readonly Queue<byte[]> _events = new(events.Select(e => Encoding.UTF8.GetBytes(e)));
        private byte[]? _current;
        private int _at;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_current is null || _at == _current.Length)
            {
                if (_events.Count == 0)
                {
                    return 0;
                }
                _current = _events.Dequeue();
                _at = 0;
                await Task.Delay(pause, cancellationToken);
            }
            var n = Math.Min(count, _current.Length - _at);
            Array.Copy(_current, _at, buffer, offset, n);
            _at += n;
            return n;
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = new byte[buffer.Length];
            var n = await ReadAsync(bytes, 0, bytes.Length, cancellationToken);
            bytes.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }
#endif

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

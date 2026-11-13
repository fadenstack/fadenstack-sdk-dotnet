using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A real HTTP/1.1 server on 127.0.0.1, one request per connection, for what only a real socket shows: what the SDK's
/// own handler does with a redirect. It answers every request with <see cref="Answer"/> and keeps what it was sent.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Method, string Path, string Body)> _requests = new();

    public LoopbackServer(Func<string, string, string> answer)
    {
        Answer = answer;
        _listener.Start();
        _ = AcceptAsync();
    }

    /// <summary>The raw response for a request, from its method and path (status line, headers, blank line, body).</summary>
    public Func<string, string, string> Answer { get; }

    public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    public IReadOnlyList<(string Method, string Path, string Body)> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>A redirect to <paramref name="location"/> with <paramref name="status"/> (307 keeps the method and body).</summary>
    public static string Redirect(int status, Uri location) =>
        $"HTTP/1.1 {status} Redirect\r\nLocation: {location.AbsoluteUri}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public static string Json(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return;
                }
                head.Append((char)one[0]);
            }
            var lines = head.ToString().Split("\r\n");
            var parts = lines[0].Split(' ');
            var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
            var body = new byte[length];
            for (var read = 0; read < length;)
            {
                read += await stream.ReadAsync(body.AsMemory(read), _stop.Token);
            }
            lock (_requests)
            {
                _requests.Add((parts[0], parts[1], Encoding.UTF8.GetString(body)));
            }
            var answer = Encoding.UTF8.GetBytes(Answer(parts[0], parts[1]));
            await stream.WriteAsync(answer, _stop.Token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

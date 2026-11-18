using System.Net;
using System.Net.Sockets;
using System.Text;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A keyed plain-http endpoint on this computer is pinned to one resolved address. "localhost" resolves to ::1 and 127.0.0.1
/// (the OS decides the order), and the server may listen on only one of them: the connection tries each in order until one
/// connects, and every one of them has passed the loopback rule (OWASP ASVS 5.0 V12). Real listeners, no timing.
/// </summary>
public class EndpointPinnedConnectionTests
{
    private static ModelEndpoint Endpoint(int port, Func<string, Task<IPAddress[]>>? resolve) => new()
    {
        BaseUrl = new Uri($"http://localhost:{port}/v1"), Model = "m", Key = new StaticTokenProvider("k-9"), Resolve = resolve,
    };

    private static async Task<HttpStatusCode> GetAsync(EndpointTransport transport)
    {
        using var response = await transport.SendAsync(EndpointUrl.Models(transport.Base), u => new HttpRequestMessage(HttpMethod.Get, u),
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_server_on_127_0_0_1_only_is_reached_through_localhost_with_a_key()
    {
        using var server = new OneLineServer(IPAddress.Loopback);
        using var transport = new EndpointTransport(Endpoint(server.Port, null)); // the OS's own resolution of localhost

        Assert.Equal(HttpStatusCode.OK, await GetAsync(transport));

        Assert.Contains("Bearer k-9", server.Requests.Single());
        Assert.Contains($"Host: localhost:{server.Port}", server.Requests.Single());
    }

    [Fact]
    public async Task A_server_on_127_0_0_1_only_is_reached_when_the_name_gives_the_IPv6_address_first()
    {
        using var server = new OneLineServer(IPAddress.Loopback);
        using var transport = new EndpointTransport(Endpoint(server.Port, _ => Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback })));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(transport));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_server_on_the_IPv6_loopback_only_is_reached_when_the_name_gives_127_0_0_1_first()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }
        using var server = new OneLineServer(IPAddress.IPv6Loopback);
        using var transport = new EndpointTransport(Endpoint(server.Port, _ => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(transport));
        Assert.Contains("Bearer k-9", server.Requests.Single());
    }

    [Fact]
    public async Task A_name_with_a_loopback_and_a_network_address_is_still_refused_before_anything_connects()
    {
        using var server = new OneLineServer(IPAddress.Loopback);
        using var transport = new EndpointTransport(Endpoint(server.Port,
            _ => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("192.168.1.20") })));

        var e = await Assert.ThrowsAsync<EndpointPlainHttpRefusedException>(() => GetAsync(transport));

        Assert.Equal(PlainHttpRefusal.KeyOverPlainHttp, e.Reason);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Where_none_of_the_addresses_answers_the_endpoint_is_unreachable()
    {
        int free;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var transport = new EndpointTransport(Endpoint(free, _ => Task.FromResult(new[] { IPAddress.Loopback })));

        await Assert.ThrowsAsync<EndpointUnreachableException>(() => GetAsync(transport));
    }

    /// <summary>A listener that answers every request with an empty 200 and keeps the request's text.</summary>
    private sealed class OneLineServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<string> _requests = [];

        public OneLineServer(IPAddress address)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // disposed
                }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var text = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!text.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (n == 0)
                        {
                            return; // a connection that sent nothing (a probe of the pin)
                        }
                        text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    lock (_requests) { _requests.Add(text.ToString()); }
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                    await stream.WriteAsync(reply, 0, reply.Length);
                }
                catch (IOException)
                {
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Mcp;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// Where an MCP server over HTTP may be reached (sdk/51 F05; OWASP ASVS 5.0 V12): https anywhere; plain http to this computer,
/// and to the local network only with the person's switch, where the agent's profile and this computer's policy allow it, and
/// never with a header value. A name is resolved once, every address must pass, and the requests go to a checked address only,
/// to the server's own origin, following no redirect. A server that reached the local network stops when that is no longer
/// allowed.
/// </summary>
public sealed class McpPlainHttpTests
{
    private const string Allow = """{"mode":"allow","developer_mode":false,"plain_http":"allow"}""";
    private const string AllowWithoutPlainHttp = """{"mode":"allow","developer_mode":false}""";

    private static IPAddress[] Ips(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();

    // ---- the rule --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("127.0.0.1", "::1")]
    public void This_computer_always_passes_even_with_a_header_and_without_the_switch(params string[] addresses) =>
        Assert.False(McpHttp.Check(Ips(addresses), hasSecrets: true, optedIn: false, permitted: false));

    [Theory]
    [InlineData("192.168.1.5")]
    [InlineData("10.0.0.7")]
    [InlineData("172.16.4.2")]
    [InlineData("169.254.10.1")]
    [InlineData("fd00::7")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("192.168.1.5", "127.0.0.1")]
    public void The_local_network_passes_with_the_switch_where_it_is_allowed(params string[] addresses) =>
        Assert.True(McpHttp.Check(Ips(addresses), hasSecrets: false, optedIn: true, permitted: true));

    [Theory]
    [InlineData(true, true, true, McpPlainHttpRefusal.Secret)]
    [InlineData(false, false, true, McpPlainHttpRefusal.NoOptIn)]
    [InlineData(false, true, false, McpPlainHttpRefusal.NotAllowed)]
    public void The_local_network_is_refused_with_a_header_without_the_switch_or_where_it_is_not_allowed(
        bool secrets, bool optedIn, bool permitted, McpPlainHttpRefusal reason)
    {
        var refused = Assert.Throws<McpPlainHttpRefusedException>(() => McpHttp.Check(Ips("192.168.1.5"), secrets, optedIn, permitted));
        Assert.Equal(reason, refused.Reason);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("100.64.0.1")] // CGNAT is not the local network
    [InlineData("2001:db8::1")]
    [InlineData("192.168.1.5", "8.8.8.8")] // one public address is enough to refuse
    [InlineData()] // does not resolve
    public void An_address_outside_the_local_network_is_refused_with_the_switch_too(params string[] addresses)
    {
        var refused = Assert.Throws<McpPlainHttpRefusedException>(() => McpHttp.Check(Ips(addresses), false, true, true));
        Assert.Equal(McpPlainHttpRefusal.NotLocal, refused.Reason);
    }

    // ---- the connection --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_name_is_resolved_once_and_every_request_goes_to_the_checked_address_with_its_own_host_header()
    {
        using var listener = new RawHttp();
        var resolved = 0;
        var connection = new McpConnection(false, name =>
        {
            Interlocked.Increment(ref resolved);
            Assert.Equal("mcp.test", name);
            return Task.FromResult(Ips("127.0.0.1"));
        });
        using var http = await McpHttp.CreateClientAsync(new Uri($"http://mcp.test:{listener.Port}/mcp"), false, false, connection, default);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://mcp.test:{listener.Port}/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://mcp.test:{listener.Port}/mcp")).StatusCode);

        Assert.Equal(1, resolved);
        Assert.False(connection.UsedLocalNetwork);
        Assert.All(listener.Hosts, h => Assert.Equal($"mcp.test:{listener.Port}", h));
        Assert.Equal(2, listener.Hosts.Count);
    }

    [Fact]
    public async Task A_checked_address_that_refuses_the_connection_gives_way_to_the_next()
    {
        using var listener = new RawHttp(); // on 127.0.0.1 only: 127.0.0.2 refuses
        var connection = new McpConnection(false, _ => Task.FromResult(Ips("127.0.0.2", "127.0.0.1")));
        using var http = await McpHttp.CreateClientAsync(new Uri($"http://localhost:{listener.Port}/"), false, false, connection, default);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{listener.Port}/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{listener.Port}/")).StatusCode);
        Assert.Equal(2, listener.Hosts.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_to_another_origin_and_no_redirect_is_followed()
    {
        using var listener = new RawHttp { Redirect = "http://127.0.0.1:9/elsewhere" };
        var connection = new McpConnection(false, _ => Task.FromResult(Ips("127.0.0.1")));
        using var http = await McpHttp.CreateClientAsync(new Uri($"http://mcp.test:{listener.Port}/mcp"), false, false, connection, default);

        var redirected = await http.GetAsync($"http://mcp.test:{listener.Port}/mcp");
        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        var other = await Assert.ThrowsAsync<McpPlainHttpRefusedException>(() => http.GetAsync($"http://other.test:{listener.Port}/mcp"));
        Assert.Equal(McpPlainHttpRefusal.NotLocal, other.Reason);
        await Assert.ThrowsAsync<McpPlainHttpRefusedException>(() => http.GetAsync($"http://mcp.test:{listener.Port + 1}/mcp"));
        Assert.Single(listener.Hosts);
    }

    [Fact]
    public async Task A_name_on_the_local_network_is_refused_before_anything_is_sent_unless_switched_on_and_allowed()
    {
        var connection = new McpConnection(false, _ => Task.FromResult(Ips("192.168.1.5")));
        var refused = await Assert.ThrowsAsync<McpPlainHttpRefusedException>(
            () => McpHttp.CreateClientAsync(new Uri("http://nas.test:8080/mcp"), false, optedIn: true, connection, default));
        Assert.Equal(McpPlainHttpRefusal.NotAllowed, refused.Reason);

        var allowed = new McpConnection(true, _ => Task.FromResult(Ips("192.168.1.5")));
        using var http = await McpHttp.CreateClientAsync(new Uri("http://nas.test:8080/mcp"), false, optedIn: true, allowed, default);
        Assert.True(allowed.UsedLocalNetwork);
    }

    [Fact]
    public async Task Https_goes_anywhere_and_resolves_nothing_itself()
    {
        var connection = new McpConnection(false, _ => throw new InvalidOperationException("https resolves through the platform"));
        using var http = await McpHttp.CreateClientAsync(new Uri("https://mcp.example.com/mcp"), true, false, connection, default);
        Assert.False(connection.UsedLocalNetwork);
    }

    [Fact]
    public async Task With_the_apps_own_http_client_plain_http_goes_only_to_this_computer()
    {
        using var own = new HttpClient();
        var server = LocalMcpServer.Http("nas", new Uri("http://192.168.1.5:8080/mcp"), own);
        server.AllowPlainHttpInLan = true;

        var refused = await Assert.ThrowsAsync<McpPlainHttpRefusedException>(() => server.Connect(new McpConnection(true), default));
        Assert.Equal(McpPlainHttpRefusal.NoOptIn, refused.Reason);
        Assert.Throws<ArgumentException>(() => LocalMcpServer.Http("x", new Uri("ftp://example.com/")));
        Assert.Throws<ArgumentException>(() => LocalMcpServer.Http("x", new Uri("https://user:pw@example.com/")));
    }

    // ---- the entry, the store, the import, the test --------------------------------------------------------------------

    private static McpServerEntry Address(string url, bool lan = false, bool header = false) => new()
    {
        Label = "nas",
        Url = url,
        PlainHttpInLan = lan,
        Headers = header ? [new McpSecretPair("Authorization", "Bearer secret-value")] : [],
    };

    [Theory]
    [InlineData("https://mcp.example.com/mcp", false, false, McpProblem.None)]
    [InlineData("https://mcp.example.com/mcp", false, true, McpProblem.None)]
    [InlineData("http://127.0.0.1:3000/mcp", false, true, McpProblem.None)]
    [InlineData("http://192.168.1.5:8080/mcp", false, false, McpProblem.AddressNotLoopback)]
    [InlineData("http://nas.local:8080/mcp", false, false, McpProblem.AddressNotLoopback)]
    [InlineData("http://192.168.1.5:8080/mcp", true, false, McpProblem.None)]
    [InlineData("http://nas.local:8080/mcp", true, false, McpProblem.None)]
    [InlineData("http://192.168.1.5:8080/mcp", true, true, McpProblem.PlainHttpWithHeaders)]
    [InlineData("http://8.8.8.8:8080/mcp", true, false, McpProblem.PlainHttpNotLocal)]
    public void The_entry_rules_take_https_anywhere_and_plain_http_beyond_this_computer_only_with_the_switch_and_no_header(
        string url, bool lan, bool header, McpProblem problem) =>
        Assert.Equal(problem, McpServerValidator.Validate(Address(url, lan, header), []).Problem);

    [Fact]
    public void The_switch_is_kept_and_a_file_without_it_stays_as_the_desktop_wrote_it()
    {
        using var folder = new TempFolder();
        var store = new McpServerStore(folder.Path, new Reversing());
        Assert.True(store.Add(Address("http://192.168.1.5:8080/mcp", lan: true)).IsValid);
        Assert.True(store.Add(Address("https://mcp.example.com/mcp") with { Label = "remote" }).IsValid);

        var again = new McpServerStore(folder.Path, new Reversing());
        Assert.True(again.Find("nas")!.PlainHttpInLan);
        Assert.False(again.Find("remote")!.PlainHttpInLan);
        var json = Encoding.UTF8.GetString(new Reversing().Unprotect(File.ReadAllBytes(Path.Combine(folder.Path, McpServerStore.FileName))));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "plainHttpInLan").Cast<System.Text.RegularExpressions.Match>());
    }

    [Fact]
    public void An_import_leaves_a_server_elsewhere_out_and_never_sets_the_switch()
    {
        var json = """
            {"mcpServers": {
              "remote": {"type": "http", "url": "https://mcp.example.com/mcp"},
              "nas": {"type": "http", "url": "http://192.168.1.5:8080/mcp"}
            }}
            """;
        var result = McpImporter.Parse(McpImportSource.ClaudeDesktop, "x.json", json, []);

        Assert.Empty(result.Servers);
        Assert.Equal([McpSkipReason.NetworkServer, McpSkipReason.NetworkServer], result.Skipped.Select(s => s.Reason));
    }

    [Fact]
    public async Task A_test_of_a_server_on_the_local_network_says_why_it_was_refused_and_sends_nothing()
    {
        using var host = new McpHost();
        var result = await host.TestAsync(Address("http://192.168.1.5:9/mcp", lan: true), allowed: true, plainHttpPermitted: false);

        Assert.False(result.Started);
        Assert.Equal(McpPlainHttpRefusal.NotAllowed, result.PlainHttp);
    }

    // ---- the agent ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AllowWithoutPlainHttp, null)] // absent: an older core, deny
    [InlineData("""{"mode":"allow","developer_mode":true,"plain_http":"deny"}""", null)] // developer mode does not override it
    [InlineData("""{"mode":"allow","developer_mode":false,"plain_http":"ALLOW"}""", null)] // only exactly "allow"
    [InlineData(Allow, """{"plainHttp":"forbidden"}""")] // this computer's policy wins
    public async Task A_server_on_the_local_network_is_not_allowed_unless_the_profile_and_this_computers_policy_allow_it(
        string localMcp, string? policy)
    {
        var server = LocalMcpServer.Http("nas", new Uri("http://192.168.1.5:9/mcp"));
        server.AllowPlainHttpInLan = true;
        using var module = new LocalMcpModule([server]);
        var builder = new ScriptedGateway(localMcp: localMcp).Builder().AddLocalMcp(module);
        if (policy is not null)
            builder.UseMachinePolicy(MachinePolicy.FromJson(policy));
        using var agent = builder.Build();

        await agent.StartAsync();

        var status = module.GetStatus("nas")!;
        Assert.Equal(McpServerState.NotAllowed, status.State);
        Assert.Equal(McpPlainHttpRefusal.NotAllowed, status.PlainHttp);
    }

    [Fact]
    public async Task A_server_without_the_switch_is_refused_on_the_local_network_where_it_would_be_allowed()
    {
        var server = LocalMcpServer.Http("nas", new Uri("http://192.168.1.5:9/mcp"));
        using var module = new LocalMcpModule([server]);
        using var agent = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(module).Build();

        await agent.StartAsync();

        Assert.Equal(McpPlainHttpRefusal.NoOptIn, module.GetStatus("nas")!.PlainHttp);
    }

    [Fact]
    public async Task A_server_that_reached_the_local_network_stops_when_that_is_no_longer_allowed_and_says_why()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("look", "Looks.", () => "seen") } };
        var server = LocalMcpServer.FromConnection("nas", (connection, _) =>
        {
            if (!connection.PlainHttpPermitted)
                throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NotAllowed, "Plain http is not allowed here.");
            connection.UsedLocalNetwork = true;
            return Task.FromResult(rig.Transport());
        });
        using var module = new LocalMcpModule([server]);
        using var allowing = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(module).Build();
        await allowing.StartAsync();
        Assert.Equal(McpServerState.Running, module.GetStatus("nas")!.State);

        using var forbidding = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(module)
            .UseMachinePolicy(MachinePolicy.FromJson("""{"plainHttp":"forbidden"}""")).Build();
        await forbidding.StartAsync();

        var status = module.GetStatus("nas")!;
        Assert.Equal(McpServerState.NotAllowed, status.State);
        Assert.Equal(McpPlainHttpRefusal.NotAllowed, status.PlainHttp);
        Assert.Empty(status.Tools);
        Assert.Equal(1, rig.Connects); // not started again
    }

    /// <summary>A tiny HTTP/1.1 responder on 127.0.0.1 that keeps each request's Host header; answers 200, or a redirect.</summary>
    private sealed class RawHttp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public RawHttp()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public string? Redirect { get; init; }

        public List<string> Hosts { get; } = [];

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0)
                        return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var host = head.ToString().Split(["\r\n"], StringSplitOptions.None)
                    .FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?.Substring(5).Trim() ?? "";
                lock (Hosts)
                    Hosts.Add(host);
                var answer = Redirect is null
                    ? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"
                    : $"HTTP/1.1 302 Found\r\nLocation: {Redirect}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(answer);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>Reverses the bytes: an "encryption" the test can read back.</summary>
    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "faden-mcp-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // a leftover temp folder is harmless
            }
        }
    }
}

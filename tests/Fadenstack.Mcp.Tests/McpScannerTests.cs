using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Fadenstack.Mcp.Tests;

/// <summary>The scanner (desktop-agent/32 §3): other apps' files from fixtures, loopback ports against in-process servers, no network beyond loopback.</summary>
public sealed class McpScannerTests : IDisposable
{
    private const string Secret = "SUPERSECRET-VALUE-123";
    private const string HeaderSecret = "Bearer HEADERSECRET-456";
    private const string ArgumentSecret = "ARGSECRET-789";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "faden-mcpscan-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private static readonly McpScanTimings Fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), 64);

    private McpScanner Scanner(McpScanTimings? timings = null, ILogger? log = null, McpOs os = McpOs.Linux,
        Func<IReadOnlyCollection<IPEndPoint>?>? listeners = null) =>
        new(new McpImportLocations(McpOs.Linux, _ => null, _home), os, timings ?? Fast, log, listeners);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static McpScanOptions AppsOnly => new(OtherApps: true, ThisComputer: false);

    private static McpScanOptions Ports(params int[] ports) => new(OtherApps: false, ThisComputer: true, [.. ports.Select(p => new McpPortRange(p, p))]);

    // ---- where each program keeps its list

    [Fact]
    public void The_further_apps_are_looked_for_where_their_vendors_keep_the_list_on_each_os()
    {
        var windows = new McpImportLocations(McpOs.Windows, _ => null, @"C:\Users\a");
        var mac = new McpImportLocations(McpOs.MacOs, _ => null, "/Users/a");
        var linux = new McpImportLocations(McpOs.Linux, _ => null, "/home/a");
        var xdg = new McpImportLocations(McpOs.Linux, n => n == "XDG_CONFIG_HOME" ? "/cfg" : null, "/home/a");

        static string P(string path) => path.Replace('\\', '/');
        foreach (var (locations, home) in new[] { (windows, "C:/Users/a"), (mac, "/Users/a"), (linux, "/home/a"), (xdg, "/home/a") })
        {
            Assert.Equal([home + "/.codeium/windsurf/mcp_config.json", home + "/.codeium/mcp_config.json"], locations.PathsOf(McpImportSource.Windsurf).Select(P));
            Assert.Equal(home + "/.claude.json", P(Assert.Single(locations.PathsOf(McpImportSource.ClaudeCode))));
            Assert.Equal(home + "/.gemini/settings.json", P(Assert.Single(locations.PathsOf(McpImportSource.GeminiCli))));
            Assert.Equal(home + "/.lmstudio/mcp.json", P(Assert.Single(locations.PathsOf(McpImportSource.LmStudio))));
        }
    }

    // ---- parsing of the further apps

    [Fact]
    public void Gemini_cli_reads_httpUrl_and_url_and_keeps_network_addresses_out()
    {
        const string text = """
            {"theme":"x","mcpServers":{
              "stream":{"httpUrl":"http://localhost:3100/mcp","headers":{"X-Key":"k1"}},
              "events":{"url":"http://127.0.0.1:3200/sse"},
              "run":{"command":"node","args":["server.js"],"cwd":"/srv/a","env":{"A":"b"},"timeout":3000,"trust":true},
              "far":{"httpUrl":"https://example.com/mcp?token=LEAKME"}}}
            """;
        var result = McpImporter.Parse(McpImportSource.GeminiCli, "s.json", text, []);

        Assert.Equal(["stream", "events", "run"], result.Servers.Select(s => s.Entry.Label));
        Assert.Equal("http://localhost:3100/mcp", result.Servers[0].Entry.Url);
        Assert.Equal(["X-Key"], result.Servers[0].Entry.Headers.Select(h => h.Name));
        Assert.Equal("http://127.0.0.1:3200/sse", result.Servers[1].Entry.Url);
        Assert.Equal("Gemini CLI", result.Servers[0].Entry.Origin);
        Assert.Equal("timeout, trust", result.Servers[2].Detail);
        var far = Assert.Single(result.Skipped);
        Assert.Equal(McpSkipReason.NetworkServer, far.Reason);
        Assert.Equal("https://example.com/mcp", far.Detail);
    }

    [Fact]
    public void Windsurf_reads_serverUrl_and_claude_code_reads_type()
    {
        var windsurf = McpImporter.Parse(McpImportSource.Windsurf, "w.json",
            """{"mcpServers":{"local":{"serverUrl":"http://localhost:5000/mcp","headers":{"Authorization":"Bearer x"}},"gh":{"command":"npx","args":["-y","gh-mcp"],"env":{"GITHUB_TOKEN":"t"}}}}""", []);
        Assert.Equal("http://localhost:5000/mcp", windsurf.Servers[0].Entry.Url);
        Assert.Equal("npx", windsurf.Servers[1].Entry.Command);

        var code = McpImporter.Parse(McpImportSource.ClaudeCode, "c.json", """
            {"numStartups":3,"projects":{"/p":{"mcpServers":{"hidden":{"command":"x"}}}},
             "mcpServers":{
               "a":{"type":"stdio","command":"uvx","args":["tool"]},
               "b":{"type":"http","url":"http://localhost:6000/mcp","headers":{"X":"y"}},
               "c":{"type":"sse","url":"http://[::1]:6001/sse"},
               "d":{"type":"http","url":"https://api.example.com/mcp"},
               "e":{"type":"ws","url":"ws://localhost:6002"}}}
            """, []);
        Assert.Equal(["a", "b", "c"], code.Servers.Select(s => s.Entry.Label));
        Assert.Equal("Claude Code", code.Servers[0].Entry.Origin);
        Assert.Equal([McpSkipReason.NetworkServer, McpSkipReason.NotUnderstood], code.Skipped.Select(s => s.Reason));
    }

    // ---- other apps

    [Fact]
    public async Task Other_apps_give_findings_with_secret_names_only_and_not_installed_apps_are_named()
    {
        Write(".cursor/mcp.json", """
            {"mcpServers":{
              "gh":{"command":"npx","args":["-y","@x/github","--token","@ArgumentSecret@","--api-key=@ArgumentSecret@"],"env":{"GITHUB_TOKEN":"@Secret@"}},
              "local":{"url":"http://localhost:4000/mcp","headers":{"Authorization":"@HeaderSecret@"}},
              "cloud":{"url":"https://x.example.com/mcp?key=@Secret@"}}}
            """.Replace("@Secret@", Secret).Replace("@HeaderSecret@", HeaderSecret).Replace("@ArgumentSecret@", ArgumentSecret));
        Write(".claude.json", """{"mcpServers":{"docs":{"type":"stdio","command":"node","args":["docs.js"]}},"history":"%PAD%"}""".Replace("%PAD%", new string('x', 1_200_000)));

        var result = await Scanner().ScanAsync(AppsOnly, [], null, CancellationToken.None);

        Assert.Equal(7, result.Apps.Count);
        Assert.Equal((true, 3), (result.Apps.Single(a => a.Source == McpImportSource.Cursor).Installed, result.Apps.Single(a => a.Source == McpImportSource.Cursor).Found));
        Assert.Equal((true, 1), (result.Apps.Single(a => a.Source == McpImportSource.ClaudeCode).Installed, result.Apps.Single(a => a.Source == McpImportSource.ClaudeCode).Found));
        Assert.All(result.Apps.Where(a => a.Source is McpImportSource.Windsurf or McpImportSource.LmStudio or McpImportSource.GeminiCli), a => Assert.Equal((false, 0), (a.Installed, a.Found)));

        var gh = result.Findings.Single(f => f.Label == "gh");
        Assert.Equal(("Cursor", McpFindingState.New), (gh.FoundIn, gh.State));
        Assert.Equal(["GITHUB_TOKEN"], gh.SecretNames);
        Assert.Equal(Secret, gh.Entry!.Environment.Single().Value);
        Assert.Equal("npx -y @x/github --token … --api-key=…", gh.Where);
        var local = result.Findings.Single(f => f.Label == "local");
        Assert.Equal(["Authorization"], local.SecretNames);
        Assert.Equal("http://localhost:4000/mcp", local.Where);
        var cloud = result.Findings.Single(f => f.FoundIn == "Cursor" && f.State == McpFindingState.NetworkLater);
        Assert.Equal("https://x.example.com/mcp", cloud.Where);
        Assert.Null(cloud.Entry);
        Assert.Equal(McpFindingState.New, result.Findings.Single(f => f.Label == "docs").State);
    }

    [Fact]
    public async Task A_server_the_person_has_is_already_added_by_program_and_arguments_or_by_address()
    {
        Write(".cursor/mcp.json", """
            {"mcpServers":{
              "fs":{"command":"C:\\Tools\\NPX.cmd","args":["-y","fs","C:\\a"]},
              "fs-other-args":{"command":"npx","args":["-y","fs","D:\\b"]},
              "api":{"url":"http://LOCALHOST:7000/mcp/"},
              "api-other-port":{"url":"http://localhost:7001/mcp"}}}
            """);
        McpServerEntry[] mine =
        [
            new() { Label = "mine", Command = "c:/tools/npx", Arguments = ["-y", "fs", "C:\\a"] },
            new() { Label = "web", Url = "http://127.0.0.1:7000/mcp" },
        ];

        var windows = await Scanner(os: McpOs.Windows).ScanAsync(AppsOnly, mine, null, CancellationToken.None);
        Assert.Equal(
            [("fs", McpFindingState.AlreadyAdded), ("fs-other-args", McpFindingState.New), ("api", McpFindingState.AlreadyAdded), ("api-other-port", McpFindingState.New)],
            windows.Findings.Select(f => (f.Label, f.State)));

        // on Linux a different case is a different file
        var linux = await Scanner(os: McpOs.Linux).ScanAsync(AppsOnly, mine, null, CancellationToken.None);
        Assert.Equal(McpFindingState.New, linux.Findings.Single(f => f.Label == "fs").State);
    }

    [Fact]
    public async Task Labels_are_free_among_the_persons_servers_and_the_other_findings()
    {
        Write(".cursor/mcp.json", """{"mcpServers":{"github":{"command":"a"}}}""");
        Write(".lmstudio/mcp.json", """{"mcpServers":{"github":{"command":"b"}}}""");

        var result = await Scanner().ScanAsync(AppsOnly, [new McpServerEntry { Label = "github", Command = "z" }], null, CancellationToken.None);

        Assert.Equal(["github-2", "github-3"], result.Findings.Select(f => f.Label));
        Assert.All(result.Findings, f => Assert.True(McpServerValidator.Validate(f.Entry!, ["github"]).IsValid || f.Entry!.Label.StartsWith("github-")));
    }

    [Fact]
    public async Task A_vs_code_value_to_ask_for_is_a_needs_value_finding_and_a_broken_file_a_not_understood_one()
    {
        Write(".gemini/settings.json", "{ not json");
        Write(".cursor/mcp.json", """{"mcpServers":{"empty":{}}}""");

        var result = await Scanner().ScanAsync(AppsOnly, [], null, CancellationToken.None);

        var broken = result.Findings.Single(f => f.FoundIn == "Gemini CLI");
        Assert.Equal((McpFindingState.NotUnderstood, "settings.json", "Unreadable"), (broken.State, broken.Where, broken.Detail));
        Assert.Equal("NothingToStart", result.Findings.Single(f => f.FoundIn == "Cursor").Detail);

        var needs = McpImporter.Parse(McpImportSource.VsCode, "m.json", """{"inputs":[],"servers":{"s":{"command":"x","env":{"K":"${input:k}"}}}}""", []);
        Assert.Contains(McpImportNote.NeedsValue, Assert.Single(needs.Servers).Notes);
    }

    // ---- ports

    [Fact]
    public void At_most_2000_ports_in_total_and_only_valid_ones()
    {
        Assert.Equal(2000, McpScanner.ExpandPorts([new(3000, 3999), new(8000, 8999)]).Count);
        Assert.Equal(2000, McpScanner.ExpandPorts([new(1, 2000), new(100, 200)]).Count);
        Assert.Throws<ArgumentException>(() => McpScanner.ExpandPorts([new(1, 2001)]));
        Assert.Throws<ArgumentException>(() => McpScanner.ExpandPorts([new(3000, 3999), new(8000, 9000)]));
        Assert.Throws<ArgumentException>(() => McpScanner.ExpandPorts([new(0, 10)]));
        Assert.Throws<ArgumentException>(() => McpScanner.ExpandPorts([new(65000, 65536)]));
        Assert.Throws<ArgumentException>(() => McpScanner.ExpandPorts([new(20, 10)]));
    }

    [Fact]
    public async Task Too_many_ports_are_refused_before_anything_is_read_or_probed()
    {
        Write(".cursor/mcp.json", """{"mcpServers":{"a":{"command":"x"}}}""");
        var progress = new List<McpScanProgress>();

        await Assert.ThrowsAsync<ArgumentException>(() => Scanner().ScanAsync(new McpScanOptions(true, true, [new McpPortRange(1000, 3500)]), [], new Progress<McpScanProgress>(progress.Add), CancellationToken.None));

        Assert.Empty(progress);
    }

    [Fact]
    public async Task An_mcp_server_on_a_loopback_port_is_found_with_its_name_and_tool_count_and_no_credentials_are_sent()
    {
        var mcp = Serve("/mcp", "My Demo Server!", 3);
        var plain = Serve("/other", "x", 1);
        var closed = FreePort();
        var progress = new ConcurrentBag<McpScanProgress>();

        var result = await Scanner().ScanAsync(Ports(mcp.Port, plain.Port, closed), [], new Progress<McpScanProgress>(progress.Add), CancellationToken.None);

        var found = Assert.Single(result.Findings);
        Assert.Equal(("my-demo-server", "This computer", McpFindingState.New, "My Demo Server! 1.2.3", 3), (found.Label, found.FoundIn, found.State, found.ServerName, found.ToolCount));
        Assert.Null(found.Source);
        Assert.Equal($"http://127.0.0.1:{mcp.Port}/mcp", found.Entry!.Url);
        Assert.Equal(found.Entry.Url, found.Where);
        Assert.False(found.Entry.Enabled); // a found server arrives off, like an import
        Assert.Equal(3, result.PortsChecked);
        Assert.False(result.CutShort);
        Assert.DoesNotContain(mcp.Requests, r => r.Authorization is not null || r.Cookie);
        Assert.Contains(mcp.Requests, r => r.Method == "tools/list");
        Assert.Equal(3, progress.Max(p => p.Done));
        Assert.All(progress, p => Assert.Equal(3, p.Total));
    }

    [Fact]
    public void A_listener_is_reached_at_the_loopback_address_its_socket_takes()
    {
        IPEndPoint[] table =
        [
            new(IPAddress.Loopback, 5000), new(IPAddress.Any, 5001), new(IPAddress.IPv6Loopback, 5002), new(IPAddress.IPv6Any, 5003),
            new(IPAddress.Parse("10.0.0.5"), 5004), new(IPAddress.Parse("127.0.0.5"), 5005), new(IPAddress.Loopback, 5007), new(IPAddress.IPv6Loopback, 5007),
        ];

        Assert.Equal([IPAddress.Loopback], McpScanner.Reachable(table, 5000));
        Assert.Equal([IPAddress.Loopback], McpScanner.Reachable(table, 5001));
        Assert.Equal([IPAddress.IPv6Loopback, IPAddress.Loopback], McpScanner.Reachable(table, 5002));
        Assert.Equal([IPAddress.IPv6Loopback, IPAddress.Loopback], McpScanner.Reachable(table, 5003));
        Assert.Empty(McpScanner.Reachable(table, 5004)); // another interface only
        Assert.Empty(McpScanner.Reachable(table, 5005)); // another loopback address: not 127.0.0.1
        Assert.Empty(McpScanner.Reachable(table, 5006)); // nothing listens
        Assert.Equal([IPAddress.Loopback, IPAddress.IPv6Loopback], McpScanner.Reachable(table, 5007));
    }

    [Fact]
    public async Task With_the_systems_listener_table_a_port_it_does_not_list_is_not_connected_to_and_without_one_every_port_is_tried()
    {
        var server = Serve("/mcp", "Table", 1);

        var notListed = await Scanner(listeners: () => []).ScanAsync(Ports(server.Port), [], null, CancellationToken.None);
        Assert.Empty(notListed.Findings);
        Assert.Equal(1, notListed.PortsChecked);
        Assert.Empty(server.Requests); // the table said nothing listens: not even a connect reached the server

        var listed = await Scanner(listeners: () => [new IPEndPoint(IPAddress.Loopback, server.Port)]).ScanAsync(Ports(server.Port), [], null, CancellationToken.None);
        var noTable = await Scanner(listeners: () => null).ScanAsync(Ports(server.Port), [], null, CancellationToken.None);

        Assert.Equal("table", Assert.Single(listed.Findings).Label);
        Assert.Equal("table", Assert.Single(noTable.Findings).Label);
    }

    [Fact]
    public void This_computers_listener_table_is_read_where_the_platform_has_one()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var table = McpScanner.ListeningEndpoints();

            if (table is null)
                return; // no table, or a filtered one (a sandboxed or packaged process): the scan connects instead (covered above)
            Assert.Contains(IPAddress.Loopback, McpScanner.Reachable(table, port)); // a table that is used is a full one
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_handshake_tries_mcp_then_mcp_slash_and_the_found_server_is_already_added_when_the_person_has_the_address()
    {
        var server = Serve("/mcp/", "slash", 0);

        var fresh = await Scanner().ScanAsync(Ports(server.Port), [], null, CancellationToken.None);
        Assert.Equal($"http://127.0.0.1:{server.Port}/mcp/", Assert.Single(fresh.Findings).Entry!.Url);
        Assert.Equal(0, fresh.Findings[0].ToolCount);

        var mine = new McpServerEntry { Label = "slash", Url = $"http://localhost:{server.Port}/mcp" };
        var again = await Scanner().ScanAsync(Ports(server.Port), [mine], null, CancellationToken.None);
        Assert.Equal(McpFindingState.AlreadyAdded, Assert.Single(again.Findings).State);
    }

    [Fact]
    public async Task A_port_that_is_open_but_says_nothing_or_is_not_mcp_gives_no_finding()
    {
        var silent = Silent();
        var closing = Silent(close: true);
        var timings = Fast with { Handshake = TimeSpan.FromMilliseconds(300) };

        var watch = Stopwatch.StartNew();
        var result = await Scanner(timings).ScanAsync(Ports(silent, closing), [], null, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.False(result.CutShort);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_sse_server_that_points_to_an_outside_address_is_never_followed_there()
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _servers.Add(new Stop(listener));
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    if (context.Request.Url!.AbsolutePath == "/sse")
                    {
                        context.Response.ContentType = "text/event-stream";
                        var bytes = Encoding.UTF8.GetBytes("event: endpoint\ndata: http://203.0.113.7:9/message\n\n");
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        await context.Response.OutputStream.FlushAsync();
                        await Task.Delay(1500);
                    }
                    context.Response.Abort();
                }
                catch (Exception)
                {
                    return;
                }
            }
        });

        var result = await Scanner(Fast with { Handshake = TimeSpan.FromSeconds(1) }).ScanAsync(Ports(port), [], null, CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Cancel_stops_the_scan_at_once()
    {
        var silent = Silent();
        using var cts = new CancellationTokenSource();
        var task = Scanner(Fast with { Handshake = TimeSpan.FromSeconds(30), Total = TimeSpan.FromMinutes(5) }).ScanAsync(Ports(silent), [], null, cts.Token);
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_scan_is_cut_at_the_time_limit_and_returns_what_it_found_so_far()
    {
        Write(".cursor/mcp.json", """{"mcpServers":{"a":{"command":"x"}}}""");
        var silent = Silent();
        // The scan reads the files first and then probes the ports, all inside one clock (Total). The clock is the SDK's own and cannot
        // be gated from here, so the test makes it irrelevant for the file part: a scanner with a long limit reads the files once beforehand (the
        // code and the files are warm, and that run is checked to have found the file), and the cut then comes seconds after a file
        // part that takes milliseconds, not within the same few hundred milliseconds. The silent port holds the handshake (30 s), so
        // the scan can only end at the cut.
        var timings = new McpScanTimings(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(4), 64);
        var warm = await Scanner().ScanAsync(AppsOnly, [], null, CancellationToken.None);
        Assert.False(warm.CutShort);
        Assert.Equal("a", Assert.Single(warm.Findings).Label);

        var watch = Stopwatch.StartNew();
        var result = await Scanner(timings).ScanAsync(new McpScanOptions(true, true, [new McpPortRange(silent, silent)]), [], null, CancellationToken.None);

        Assert.True(result.CutShort);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25));
        Assert.Equal("a", Assert.Single(result.Findings).Label);
    }

    [Fact]
    public async Task No_secret_value_is_in_a_finding_s_text_or_in_the_log()
    {
        Write(".cursor/mcp.json", """
            {"mcpServers":{
              "gh":{"command":"npx","args":["-y","pkg","--token","@ArgumentSecret@","postgres://user:@ArgumentSecret@@db/x","--label=@Secret@"],"env":{"GITHUB_TOKEN":"@Secret@"}},
              "local":{"url":"http://localhost:4000/mcp?key=@Secret@","headers":{"Authorization":"@HeaderSecret@"}},
              "cloud":{"url":"https://u:@Secret@@x.example.com/@Secret@-@Secret@/mcp?k=@Secret@"}}}
            """.Replace("@Secret@", Secret).Replace("@HeaderSecret@", HeaderSecret).Replace("@ArgumentSecret@", ArgumentSecret));
        var log = new CapturingLogger();
        var server = Serve("/mcp", "srv", 1);

        var result = await Scanner(log: log).ScanAsync(new McpScanOptions(true, true, [new McpPortRange(server.Port, server.Port)]), [], null, CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        var text = new StringBuilder();
        foreach (var finding in result.Findings)
        {
            text.AppendLine(string.Join("|", finding.Label, finding.FoundIn, finding.Where, finding.ServerName, finding.Detail, string.Join(",", finding.SecretNames)));
            text.AppendLine(finding.ToString());
            text.AppendLine(finding.Entry?.ToString());
            text.AppendLine(string.Join(",", finding.Entry?.Environment ?? []));
            text.AppendLine(string.Join(",", finding.Entry?.Headers ?? []));
        }
        text.AppendLine(string.Join(Environment.NewLine, result.Apps));
        foreach (var value in new[] { Secret, HeaderSecret, ArgumentSecret, "HEADERSECRET", "ARGSECRET" })
        {
            Assert.DoesNotContain(value, text.ToString());
            Assert.DoesNotContain(value, string.Join(Environment.NewLine, log.Lines));
        }
        Assert.NotEmpty(log.Lines);
    }

    [Fact]
    public async Task Nothing_is_read_or_probed_when_neither_is_asked_for()
    {
        Write(".cursor/mcp.json", """{"mcpServers":{"a":{"command":"x"}}}""");

        var result = await Scanner().ScanAsync(new McpScanOptions(false, false), [], null, CancellationToken.None);

        Assert.Empty(result.Apps);
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.PortsChecked);
    }

    // ---- fakes

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Stop(IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                inner.Dispose();
            }
            catch (Exception)
            {
                // already closed
            }
        }
    }

    /// <summary>A port that accepts connections and never answers (or closes at once).</summary>
    private int Silent(bool close = false)
    {
        // The system picks the port and the listener keeps it: no gap between finding a free port and binding it, in which a test
        // running beside this one could take the same port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _servers.Add(new Stop(new Disposer(() =>
        {
            listener.Stop();
            foreach (var client in held)
                client.Dispose();
        })));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    if (close)
                        client.Dispose();
                    else
                        lock (held)
                            held.Add(client);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });
        return port;
    }

    private sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed record Seen(string Method, string? Authorization, bool Cookie);

    private sealed class FakeServer(int port, HttpListener listener)
    {
        public int Port { get; } = port;

        public ConcurrentQueue<Seen> Requests { get; } = new();

        public HttpListener Listener { get; } = listener;
    }

    /// <summary>A streamable HTTP MCP server at <paramref name="path"/> that answers initialize and tools/list, and nothing at other paths.</summary>
    private FakeServer Serve(string path, string name, int tools)
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _servers.Add(new Stop(listener));
        var server = new FakeServer(port, listener);
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => Handle(context, path, name, tools, server));
            }
        });
        return server;
    }

    private static async Task Handle(HttpListenerContext context, string path, string name, int tools, FakeServer server)
    {
        var response = context.Response;
        try
        {
            if (context.Request.Url!.AbsolutePath != path || context.Request.HttpMethod != "POST")
            {
                response.StatusCode = context.Request.Url.AbsolutePath != path ? 404 : 405;
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = body.RootElement;
            var method = root.GetProperty("method").GetString()!;
            server.Requests.Enqueue(new Seen(method, context.Request.Headers["Authorization"], context.Request.Headers["Cookie"] is not null));
            if (!root.TryGetProperty("id", out var id))
            {
                response.StatusCode = 202;
                return;
            }
            string? result = method switch
            {
                "initialize" => "{\"protocolVersion\":" + JsonSerializer.Serialize(root.GetProperty("params").GetProperty("protocolVersion").GetString()) + ",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":" + JsonSerializer.Serialize(name) + ",\"version\":\"1.2.3\"}}",
                "tools/list" => "{\"tools\":[" + string.Join(",", Enumerable.Range(1, tools).Select(n => "{\"name\":\"tool" + n + "\",\"inputSchema\":{\"type\":\"object\"}}")) + "]}",
                _ => null,
            };
            var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + (result is null ? ",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}" : ",\"result\":" + result + "}"));
            response.ContentType = "application/json";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception)
        {
            // the scanner hung up
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // gone
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + exception);
    }
}

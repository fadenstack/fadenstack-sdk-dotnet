using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fadenstack.Mcp;

/// <summary>The times a scan keeps to (desktop-agent/32 §3); tests give short ones.</summary>
/// <param name="Connect">The longest a TCP connect to a loopback port may take.</param>
/// <param name="Handshake">The longest one MCP handshake (initialize and tools/list) at one path may take.</param>
/// <param name="Total">The longest the whole scan may take; what it found by then is returned.</param>
/// <param name="Concurrency">How many ports are checked at once.</param>
public sealed record McpScanTimings(TimeSpan Connect, TimeSpan Handshake, TimeSpan Total, int Concurrency)
{
    public static McpScanTimings Default { get; } = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), 128);
}

/// <summary>
/// The scanner (desktop-agent/32 §3). Other apps: each one's file is read with the <see cref="McpImporter"/>. This computer: the
/// ports the operating system lists as listening on loopback or on every address (where it cannot list them, a TCP connect to each
/// port, 127.0.0.1 and ::1), and only such a port gets an MCP handshake at <c>/mcp</c>, <c>/mcp/</c>,
/// <c>/sse</c> with the MCP client of the SDK, without credentials and without following redirects; a request to any address that is
/// not loopback is refused before it is sent. It never starts a program and never writes a secret's value into a finding's text or the log
/// (findings carry the names; the values travel only inside <see cref="McpFinding.Entry"/>, which does not print them).
/// </summary>
public sealed class McpScanner : IMcpScanner
{
    /// <summary>The most ports one scan checks, over all ranges together.</summary>
    public const int MaxPorts = 2000;

    /// <summary>The origin of an entry made from a port.</summary>
    public const string ThisComputerName = "This computer";

    private static readonly string[] Paths = ["/mcp", "/mcp/", "/sse"];

    private static readonly McpImportSource[] AppList =
    [
        McpImportSource.ClaudeDesktop, McpImportSource.VsCode, McpImportSource.Cursor, McpImportSource.Windsurf,
        McpImportSource.ClaudeCode, McpImportSource.GeminiCli, McpImportSource.LmStudio,
    ];

    private readonly McpImporter _importer;
    private readonly IMcpImportLocations _locations;
    private readonly McpOs _os;
    private readonly McpScanTimings _timings;
    private readonly ILogger? _log;
    private readonly Func<IReadOnlyCollection<IPEndPoint>?> _listeners;

    /// <param name="locations">Where each app keeps its server list.</param>
    /// <param name="os">This computer's operating system (how program paths compare).</param>
    /// <param name="timings">The scan's time limits; <see cref="McpScanTimings.Default"/> if not given.</param>
    /// <param name="log">Counts, app names and port numbers only.</param>
    /// <param name="listeners">The TCP endpoints this computer listens on (<see cref="ListeningEndpoints"/>; tests give their own);
    /// null from it: each port is tried with a connect instead.</param>
    public McpScanner(IMcpImportLocations locations, McpOs os, McpScanTimings? timings = null, ILogger? log = null,
        Func<IReadOnlyCollection<IPEndPoint>?>? listeners = null)
    {
        _locations = locations;
        _importer = new McpImporter(locations);
        _os = os;
        _timings = timings ?? McpScanTimings.Default;
        _log = log;
        _listeners = listeners ?? ListeningEndpoints;
    }

    /// <summary>
    /// The TCP endpoints this computer listens on, from the operating system's own table (as <c>netstat</c> shows it), or null where
    /// the platform cannot give it or gives only part of it. With the table a scan needs no connect per port: a closed loopback port on
    /// Windows answers a connect only after its timeout, so 2,000 ports took about 20 s by connecting. The table can be filtered (a
    /// sandboxed or packaged process sees only some listeners, without an error), so a listener of our own is opened first and the
    /// table is used only when it shows that one too.
    /// </summary>
    public static IReadOnlyCollection<IPEndPoint>? ListeningEndpoints()
    {
        TcpListener? canary = null;
        try
        {
            canary = new TcpListener(IPAddress.Loopback, 0);
            canary.Start();
            var mine = (IPEndPoint)canary.LocalEndpoint;
            var table = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return table.Any(e => e.Port == mine.Port && (e.Address.Equals(IPAddress.Loopback) || e.Address.Equals(IPAddress.Any))) ? table : null;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NetworkInformationException or InvalidOperationException or SocketException)
        {
            return null;
        }
        finally
        {
            canary?.Stop();
        }
    }

    /// <summary>
    /// The loopback addresses a listener on <paramref name="port"/> can be reached at: 127.0.0.1 for one on 127.0.0.1 or 0.0.0.0, ::1 for
    /// one on ::1 or :: (a dual-stack socket on :: may take 127.0.0.1 too; the handshake tries both). Empty: nothing listens there.
    /// </summary>
    public static IReadOnlyList<IPAddress> Reachable(IEnumerable<IPEndPoint> listeners, int port)
    {
        bool v4 = false, v6 = false;
        foreach (var at in listeners)
        {
            if (at.Port != port)
                continue;
            v4 |= at.Address.Equals(IPAddress.Loopback) || at.Address.Equals(IPAddress.Any);
            v6 |= at.Address.Equals(IPAddress.IPv6Loopback) || at.Address.Equals(IPAddress.IPv6Any);
        }
        return v4 && v6 ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : v4 ? [IPAddress.Loopback] : v6 ? [IPAddress.IPv6Loopback, IPAddress.Loopback] : [];
    }

    public static McpScanner ForThisComputer(ILogger? log = null, McpOs? os = null, Func<string, string?>? env = null)
    {
        var thisOs = os ?? McpOsExtensions.Current();
        return new McpScanner(McpImportLocations.ForThisComputer(thisOs, env), thisOs, null, log);
    }

    public IReadOnlyList<McpImportSource> Apps => AppList;

    /// <summary>The ports of <paramref name="ranges"/>, each once, ascending.</summary>
    /// <exception cref="ArgumentException">A range outside 1-65535 or reversed, or more than <see cref="MaxPorts"/> ports.</exception>
    public static IReadOnlyList<int> ExpandPorts(IReadOnlyList<McpPortRange> ranges)
    {
        var wanted = new bool[65536];
        foreach (var range in ranges)
        {
            if (range.From < 1 || range.To > 65535 || range.From > range.To)
                throw new ArgumentException($"A port range must lie within 1 and 65535, from low to high (got {range.From}-{range.To}).", nameof(ranges));
            for (var port = range.From; port <= range.To; port++)
                wanted[port] = true;
        }
        var ports = new List<int>();
        for (var port = 1; port < wanted.Length; port++)
            if (wanted[port])
                ports.Add(port);
        if (ports.Count > MaxPorts)
            throw new ArgumentException($"A scan looks at {MaxPorts} ports at most (got {ports.Count}).", nameof(ranges));
        return ports;
    }

    /// <exception cref="ArgumentException">The port ranges break the limits (nothing is read or probed then).</exception>
    /// <exception cref="OperationCanceledException">The person cancelled.</exception>
    public async Task<McpScanResult> ScanAsync(McpScanOptions options, IReadOnlyList<McpServerEntry> current, IProgress<McpScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }
        var ports = options.ThisComputer ? ExpandPorts(options.Ports ?? McpScanOptions.DefaultPorts) : [];
        var apps = options.OtherApps ? AppList : [];
        var total = apps.Length + ports.Count;
        var watch = Stopwatch.StartNew();
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(_timings.Total);

        var done = 0;
        var used = new HashSet<string>(current.Select(e => e.Label), StringComparer.Ordinal);
        var scanApps = new List<McpScanApp>();
        var findings = new List<McpFinding>();
        var timedOut = false;
        var checkedPorts = 0;
        var probed = new ConcurrentBag<Probe>();
        try
        {
            foreach (var source in apps)
            {
                cap.Token.ThrowIfCancellationRequested();
                scanApps.Add(await ReadAppAsync(source, current, used, findings, cap.Token).ConfigureAwait(false));
                progress?.Report(new McpScanProgress(++done, total));
            }

            // The operating system's list of listeners, when it has one: closed ports are then counted without a connect.
            var listening = ports.Count == 0 ? null : _listeners()?.ToList();
            _log?.LogInformation("MCP scan: ports checked {How}", listening is null ? "by connecting" : "from the system's listener table");
            await ForEachAsync(ports, Math.Max(1, _timings.Concurrency), cap.Token,
                async (port, token) =>
                {
                    var at = listening is null ? null : Reachable(listening, port);
                    if ((at is null || at.Count > 0) && await ProbePortAsync(port, at, token).ConfigureAwait(false) is { } probe)
                        probed.Add(probe);
                    var n = Interlocked.Increment(ref checkedPorts);
                    if (n % 8 == 0 || n == ports.Count)
                        progress?.Report(new McpScanProgress(apps.Length + n, total));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // the time limit: what was found by then is returned
            timedOut = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        AddProbeFindings(probed, current, used, findings);
        _log?.LogInformation("MCP scan: {Apps} apps read, {Ports} ports checked, {Findings} findings{Cut}", scanApps.Count, checkedPorts, findings.Count, timedOut ? ", cut at the time limit" : "");
        return new McpScanResult(scanApps, checkedPorts, findings, watch.Elapsed, timedOut);
    }

    private async Task<McpScanApp> ReadAppAsync(McpImportSource source, IReadOnlyList<McpServerEntry> current, HashSet<string> used, List<McpFinding> findings,
        CancellationToken cancellationToken)
    {
        var name = McpImporter.Name(source);
        if (!_locations.PathsOf(source).Any(File.Exists))
            return new McpScanApp(source, name, false, 0);
        // labels are made free here, against the person's servers and the other findings, not by the importer
        var result = await _importer.ReadAsync(source, null, [], cancellationToken).ConfigureAwait(false);
        if (result.Problem is McpImportProblem.Unreadable or McpImportProblem.TooLarge)
        {
            findings.Add(new McpFinding("", name, source, null, McpFindingState.NotUnderstood, result.Path is null ? "" : Path.GetFileName(result.Path), [], Detail: result.Problem.ToString()));
            return new McpScanApp(source, name, true, 0);
        }
        var found = 0;
        foreach (var candidate in result.Servers)
        {
            found++;
            var entry = candidate.Entry;
            var already = current.Any(c => Same(c, entry));
            var state = already ? McpFindingState.AlreadyAdded : candidate.Notes.Contains(McpImportNote.NeedsValue) ? McpFindingState.NeedsValue : McpFindingState.New;
            if (!already)
            {
                var label = McpServerValidator.FreeLabel(entry.Label, used);
                used.Add(label);
                entry = entry with { Label = label };
            }
            var twin = findings.FirstOrDefault(f => f.Entry is { } other && f.State != McpFindingState.AlreadyAdded && Same(other, entry));
            findings.Add(new McpFinding(entry.Label, name, source, entry, state, McpAddresses.Where(entry), SecretNamesOf(entry),
                Detail: twin is null ? candidate.Detail : "Same as " + twin.FoundIn));
        }
        foreach (var skip in result.Skipped)
        {
            found++;
            var network = skip.Reason == McpSkipReason.NetworkServer;
            findings.Add(new McpFinding(McpServerValidator.LabelFrom(skip.Name), name, source, null,
                network ? McpFindingState.NetworkLater : McpFindingState.NotUnderstood, network ? skip.Detail ?? "" : skip.Name, [],
                Detail: network ? null : skip.Reason + (skip.Detail is null ? "" : ":" + skip.Detail)));
        }
        _log?.LogInformation("MCP scan: {App} has {Count} servers", name, found);
        return new McpScanApp(source, name, true, found);
    }

    private bool Same(McpServerEntry a, McpServerEntry b) =>
        a.IsProgram ? McpAddresses.SameProgram(a, b, _os == McpOs.Windows) : !b.IsProgram && McpAddresses.SameAddress(a.Url, b.Url);

    private static IReadOnlyList<string> SecretNamesOf(McpServerEntry entry) => [.. entry.Environment.Concat(entry.Headers).Select(p => p.Name).Distinct(StringComparer.Ordinal)];

    // ---- this computer

    private sealed record Probe(int Port, IPAddress Address, string Path, string Name, string? Version, int? Tools);

    // listening: the addresses the system's table says a listener takes on this port; null: no table, each address is tried with a
    // connect first.
    private async Task<Probe?> ProbePortAsync(int port, IReadOnlyList<IPAddress>? listening, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> open = listening ?? [];
        if (listening is null)
        {
            // Without the table: both loopback addresses at once, so a closed port costs one connect timeout, not two.
            var v4 = IsOpenAsync(IPAddress.Loopback, port, cancellationToken);
            var v6 = IsOpenAsync(IPAddress.IPv6Loopback, port, cancellationToken);
            await Task.WhenAll(v4, v6).ConfigureAwait(false);
            open = [.. new[] { (IPAddress.Loopback, v4.Result), (IPAddress.IPv6Loopback, v6.Result) }.Where(a => a.Item2).Select(a => a.Item1)];
        }
        foreach (var address in open)
        {
            _log?.LogInformation("MCP scan: port {Port} is open", port);
            foreach (var path in Paths)
                if (await HandshakeAsync(address, port, path, cancellationToken).ConfigureAwait(false) is { } probe)
                    return probe;
        }
        return null;
    }

    /// <summary>Runs <paramref name="body"/> for each item, at most <paramref name="parallelism"/> at a time (netstandard2.0 has no <c>Parallel.ForEachAsync</c>).</summary>
    private static async Task ForEachAsync<T>(IEnumerable<T> items, int parallelism, CancellationToken cancellationToken, Func<T, CancellationToken, Task> body)
    {
        using var gate = new SemaphoreSlim(parallelism);
        var running = new List<Task>();
        Exception? failure = null;
        try
        {
            foreach (var item in items)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.Add(RunAsync(item));
            }
        }
        catch (Exception e)
        {
            failure = e;
        }
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            failure ??= e;
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        async Task RunAsync(T item)
        {
            try
            {
                await body(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<bool> IsOpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timings.Connect);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
#if NET
            await socket.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
#else
            using (limit.Token.Register(() => socket.Dispose()))
            {
                try
                {
                    await Task.Factory.FromAsync(socket.BeginConnect, socket.EndConnect, address, port, null).ConfigureAwait(false);
                }
                catch (Exception e) when (limit.IsCancellationRequested && e is ObjectDisposedException or SocketException)
                {
                    throw new OperationCanceledException(limit.Token);
                }
            }
#endif
            return true;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task<Probe?> HandshakeAsync(IPAddress address, int port, string path, CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder("http", address.ToString(), port, path).Uri;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timings.Handshake);
        using var http = new HttpClient(new LoopbackOnly(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "scan",
            TransportMode = path == "/sse" ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
            ConnectionTimeout = _timings.Handshake,
        }, http, null, ownsHttpClient: false);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "Fadenstack Desktop scan", Version = "1.0" } },
                cancellationToken: limit.Token).ConfigureAwait(false);
            var info = client.ServerInfo;
            int? tools = null;
            try
            {
                tools = (await client.ListToolsAsync(cancellationToken: limit.Token).ConfigureAwait(false)).Count;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a server without tools still answered the handshake
            }
            return new Probe(port, address, path, Clean(info?.Name) is { Length: > 0 } n ? n : "server", Clean(info?.Version) is { Length: > 0 } v ? v : null, tools);
        }
        catch (OperationCanceledException)
        {
            // the person's Cancel or the scan's time limit goes on; this attempt's own time is just "not MCP here"
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // closing a session that is gone
                }
            }
        }
    }

    // What a server calls itself is its own say: no control, format or line-break characters, and not long.
    private static string Clean(string? text)
    {
        if (text is null)
            return "";
        var kept = new string(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)).ToArray()).Trim();
        return kept.Length > 64 ? kept[..64] : kept;
    }

    private static void AddProbeFindings(ConcurrentBag<Probe> probed, IReadOnlyList<McpServerEntry> current, HashSet<string> used, List<McpFinding> findings)
    {
        foreach (var probe in probed.OrderBy(p => p.Port))
        {
            var host = probe.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1";
            var url = $"http://{host}:{probe.Port}{probe.Path}";
            var already = current.Any(c => !c.IsProgram && McpAddresses.SameAddress(c.Url, url));
            var label = McpServerValidator.LabelFrom(probe.Name);
            if (!already)
            {
                label = McpServerValidator.FreeLabel(label, used);
                used.Add(label);
            }
            var entry = new McpServerEntry { Label = label, Url = url, Origin = ThisComputerName, Enabled = false }; // arrives off, like an import
            findings.Add(new McpFinding(label, ThisComputerName, null, entry, already ? McpFindingState.AlreadyAdded : McpFindingState.New, McpAddresses.Display(url), [],
                probe.Version is null ? probe.Name : probe.Name + " " + probe.Version, probe.Tools));
        }
    }

    /// <summary>Refuses a request to anything but a loopback address, whatever a server's reply tells the client to fetch next.</summary>
    private sealed class LoopbackOnly(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host.Trim('[', ']');
            if (host is null || !IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))
                throw new HttpRequestException("The scan talks to this computer only.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

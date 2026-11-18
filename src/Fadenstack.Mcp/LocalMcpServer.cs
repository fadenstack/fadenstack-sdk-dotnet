using Fadenstack.Tools;
using ModelContextProtocol.Client;

namespace Fadenstack.Mcp;

/// <summary>
/// A local MCP server the agent may attach: a program started over stdio, an HTTP endpoint, or any transport.
/// Whether it is attached is the agent's policy, not this configuration's.
/// </summary>
public sealed class LocalMcpServer
{
    private LocalMcpServer(string label, Func<McpConnection, CancellationToken, Task<IClientTransport>> connect)
    {
        Label = string.IsNullOrWhiteSpace(label) ? throw new ArgumentException("A label is required.", nameof(label)) : label;
        Connect = connect;
    }

    /// <summary>What the user and the server see it as.</summary>
    public string Label { get; }

    /// <summary>The program, for a stdio server. Its arguments are never reported: they can hold secrets.</summary>
    public string? Command { get; private set; }

    public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The absolute file <see cref="Command"/> resolved to at the last start (through <c>PATH</c> and, on Windows, <c>PATHEXT</c>; a
    /// bare name is never looked for in the working folder); null when it was not found or nothing was started yet. The module sets
    /// it before the policy decides, so a policy may judge the file that will really start. The pin hashes it, and it is what starts.
    /// </summary>
    public string? ResolvedProgram { get; internal set; }

    /// <summary>The folder a stdio program starts in; null: the app's.</summary>
    internal string? WorkingDirectory { get; private set; }

    /// <summary>The <c>PATH</c> the program is started with, when its environment sets one; null: the app's.</summary>
    internal string? PathVariable { get; private set; }

    /// <summary>
    /// The program as the person added it or last confirmed it (sdk/51 5.5). Before every start the module hashes the program again
    /// and does not start it when it differs (<see cref="McpServerState.ProgramChanged"/>). Null: nothing is checked, and the module
    /// pins the program at its first successful start (an entry kept before pins). TS: none, the browser starts no program.
    /// </summary>
    public McpProgramPin? ProgramPin { get; set; }

    public Uri? Url { get; private set; }

    /// <summary>Opens the transport; the connection says what this host allows (plain http on the local network) and learns what was used.</summary>
    internal Func<McpConnection, CancellationToken, Task<IClientTransport>> Connect { get; }

    /// <summary>
    /// The person's switch "Allow plain http on the local network" for this HTTP server (sdk/51 F05; off by default). With it,
    /// plain http may go to an address on the local network (RFC 1918, link-local, fc00::/7) when the agent's profile
    /// (<c>local_mcp.plain_http: allow</c>) and this computer's policy allow it, and never with a header value. Without it, plain
    /// http goes only to this computer. https goes anywhere either way.
    /// </summary>
    public bool AllowPlainHttpInLan { get; set; }

    /// <summary>Whether the server is sent header values (they are secrets: never over plain http beyond this computer).</summary>
    internal bool HasHeaders { get; private set; }

    /// <summary>
    /// Whether the host trusts this server's annotations (sdk/51 F05, H4): the person in standalone mode, the administrator in
    /// governed mode, never the server itself. Only a trusted server's <c>readOnlyHint</c> raises a tool to <see cref="ToolClass.Read"/>;
    /// a <c>destructiveHint</c> from any server makes the tool destructive. See <see cref="McpToolClasses"/>. Changes take effect
    /// at the next turn once <see cref="LocalMcpModule.ClassesChanged"/> is called.
    /// </summary>
    public bool Trusted { get; set; }

    /// <summary>
    /// The tools the person (standalone) or the administrator (governed) raised, by the server's own tool name: only
    /// <see cref="ToolClass.Read"/> and <see cref="ToolClass.Write"/> count. A destructive hint is a floor they cannot lower.
    /// </summary>
    public IReadOnlyDictionary<string, ToolClass> ToolClasses { get; set; } = new Dictionary<string, ToolClass>();

    /// <summary>The longest a call may take when the server does not say otherwise.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The most a call may be given.</summary>
    public static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMinutes(10);

    private TimeSpan _callTimeout = DefaultCallTimeout;

    /// <summary>
    /// How long one call to this server may take before it is cancelled (default 2 minutes, at most 10; a value above is cut to
    /// the most, zero or less means the default).
    /// </summary>
    public TimeSpan CallTimeout
    {
        get => _callTimeout;
        set => _callTimeout = value <= TimeSpan.Zero ? DefaultCallTimeout : value > MaxCallTimeout ? MaxCallTimeout : value;
    }

    /// <summary>How it connects, for logs: <c>stdio</c>, <c>http</c> or <c>custom</c>. Never the program, its arguments or the URL.</summary>
    internal string Transport => Command is not null ? "stdio" : Url is not null ? "http" : "custom";

    public static LocalMcpServer Stdio(string label, string command, IEnumerable<string>? arguments = null,
        IDictionary<string, string?>? environment = null, string? workingDirectory = null)
    {
        var args = arguments?.ToList() ?? new List<string>();
        var env = environment is null ? null : new Dictionary<string, string?>(environment);
        // The command is resolved once, to an absolute file, and that file is what starts, with the arguments as a list and no shell
        // (see McpStart). The module resolves it before the policy and the pin and hands it on; any other start resolves it here.
        return new LocalMcpServer(label, (connection, _) =>
        {
            var program = connection.ProgramResolved
                ? connection.Program
                : McpProgramPinner.Resolve(command, workingDirectory, McpProgramPinner.PathOf(env));
            return Task.FromResult(McpStart.Open(label, program, args, env, workingDirectory, McpProgramPinner.IsWindows));
        })
        {
            Command = command,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            PathVariable = McpProgramPinner.PathOf(environment),
        };
    }

    /// <summary>
    /// An HTTP endpoint; the <paramref name="headers"/> are sent with every request, and their values are never logged or
    /// reported. https goes anywhere; plain http only to this computer, or to the local network with
    /// <see cref="AllowPlainHttpInLan"/> (see there). Each connection checks the address it goes to and sends only there, to the
    /// server's own origin, following no redirect. With an app's own <paramref name="httpClient"/>, which the SDK cannot hold to
    /// a checked address, plain http goes only to an address literal on this computer or <c>localhost</c>.
    /// </summary>
    public static LocalMcpServer Http(string label, Uri url, HttpClient? httpClient = null, IDictionary<string, string>? headers = null)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || url.UserInfo.Length > 0)
        {
            throw new ArgumentException("An MCP server's address is an absolute http or https URL without a user name.", nameof(url));
        }
        var extra = headers is null || headers.Count == 0 ? null : new Dictionary<string, string>(headers);
        LocalMcpServer? made = null;
        made = new(label, async (connection, cancellationToken) =>
        {
            var options = new HttpClientTransportOptions { Name = label, Endpoint = url, AdditionalHeaders = extra };
            if (httpClient is not null)
            {
                if (url.Scheme == Uri.UriSchemeHttp && !IsLoopback(url))
                {
                    throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NoOptIn,
                        "With the app's own HTTP client, plain http goes only to this computer.");
                }
                return new HttpClientTransport(options, httpClient);
            }
            var client = await McpHttp.CreateClientAsync(url, extra is not null, made!.AllowPlainHttpInLan, connection, cancellationToken)
                .ConfigureAwait(false);
            return new HttpClientTransport(options, client, ownsHttpClient: true);
        })
        {
            Url = url,
            HasHeaders = extra is not null,
        };
        return made;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is on this computer: http or https to <c>localhost</c>, 127.0.0.0/8 or ::1. A host that
    /// refuses network servers checks the address with this before it adds a server.
    /// </summary>
    public static bool IsLoopback(Uri url)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }
        var host = url.IdnHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>A program server whose transport is the caller's (tests: the check and the pin of a program, without a child process).</summary>
    internal static LocalMcpServer ProgramWith(string label, string command, IEnumerable<string>? arguments, string? workingDirectory,
        Func<CancellationToken, Task<IClientTransport>> transport) =>
        new(label, (_, cancellationToken) => transport(cancellationToken))
        {
            Command = command,
            Arguments = arguments?.ToList() ?? new List<string>(),
            WorkingDirectory = workingDirectory,
        };

    /// <summary>A transport that sees the host's connection rules (tests: a server that says it used the local network).</summary>
    internal static LocalMcpServer FromConnection(string label, Func<McpConnection, CancellationToken, Task<IClientTransport>> connect) =>
        new(label, connect ?? throw new ArgumentNullException(nameof(connect)));

    /// <summary>Any transport: a named pipe, an in-process stream.</summary>
    public static LocalMcpServer FromTransport(string label, Func<CancellationToken, Task<IClientTransport>> transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }
        return new(label, (_, cancellationToken) => transport(cancellationToken));
    }
}

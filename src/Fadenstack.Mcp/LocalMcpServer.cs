using ModelContextProtocol.Client;

namespace Fadenstack.Mcp;

/// <summary>
/// A local MCP server the agent may attach: a program started over stdio, an HTTP endpoint, or any transport.
/// Whether it is attached is the agent's policy, not this configuration's.
/// </summary>
public sealed class LocalMcpServer
{
    private LocalMcpServer(string label, Func<CancellationToken, Task<IClientTransport>> connect)
    {
        Label = string.IsNullOrWhiteSpace(label) ? throw new ArgumentException("A label is required.", nameof(label)) : label;
        Connect = connect;
    }

    /// <summary>What the user and the server see it as.</summary>
    public string Label { get; }

    /// <summary>The program, for a stdio server. Its arguments are never reported: they can hold secrets.</summary>
    public string? Command { get; private set; }

    public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

    public Uri? Url { get; private set; }

    internal Func<CancellationToken, Task<IClientTransport>> Connect { get; }

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
        return new LocalMcpServer(label, _ => Task.FromResult<IClientTransport>(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = label,
            Command = command,
            Arguments = args,
            EnvironmentVariables = environment is null ? null : new Dictionary<string, string?>(environment),
            WorkingDirectory = workingDirectory,
        })))
        {
            Command = command,
            Arguments = args,
        };
    }

    /// <summary>An HTTP endpoint; the <paramref name="headers"/> are sent with every request, and their values are never logged or reported.</summary>
    public static LocalMcpServer Http(string label, Uri url, HttpClient? httpClient = null, IDictionary<string, string>? headers = null)
    {
        var extra = headers is null ? null : new Dictionary<string, string>(headers);
        return new(label, _ =>
        {
            var options = new HttpClientTransportOptions { Name = label, Endpoint = url, AdditionalHeaders = extra };
            return Task.FromResult<IClientTransport>(httpClient is null ? new HttpClientTransport(options) : new HttpClientTransport(options, httpClient));
        })
        {
            Url = url,
        };
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

    /// <summary>Any transport: a named pipe, an in-process stream.</summary>
    public static LocalMcpServer FromTransport(string label, Func<CancellationToken, Task<IClientTransport>> transport) =>
        new(label, transport ?? throw new ArgumentNullException(nameof(transport)));
}

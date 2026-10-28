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

    public static LocalMcpServer Http(string label, Uri url, HttpClient? httpClient = null) =>
        new(label, _ => Task.FromResult<IClientTransport>(httpClient is null
            ? new HttpClientTransport(new HttpClientTransportOptions { Name = label, Endpoint = url })
            : new HttpClientTransport(new HttpClientTransportOptions { Name = label, Endpoint = url }, httpClient)))
        {
            Url = url,
        };

    /// <summary>Any transport: a named pipe, an in-process stream.</summary>
    public static LocalMcpServer FromTransport(string label, Func<CancellationToken, Task<IClientTransport>> transport) =>
        new(label, transport ?? throw new ArgumentNullException(nameof(transport)));
}

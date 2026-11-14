using System.IO.Pipelines;
using Fadenstack.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// MCP servers in this process, over pipes, made with the MCP SDK's own server types: a new server for every connection. No
/// network and no child process, so no program (npx, uvx) is ever started by a test.
/// </summary>
internal sealed class McpRig
{
    private readonly List<McpServer> _servers = [];

    /// <summary>The tools every new connection serves.</summary>
    public List<McpServerTool> Tools { get; } = [];

    public string? Instructions { get; set; }

    public int Connects { get; private set; }

    /// <summary>Called with every new server before it runs: register handlers here.</summary>
    public Action<McpServer>? OnServer { get; set; }

    public McpServer Current
    {
        get
        {
            lock (_servers)
                return _servers[^1];
        }
    }

    public LocalMcpServer Server(string label) => LocalMcpServer.FromTransport(label, _ => Task.FromResult(Transport()));

    public IClientTransport Transport()
    {
        Connects++;
        var toServer = new Pipe();
        var toClient = new Pipe();
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in Tools)
            collection.Add(tool);
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = collection, ServerInstructions = Instructions });
        lock (_servers)
            _servers.Add(server);
        OnServer?.Invoke(server);
        _ = server.RunAsync();
        return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
    }

    public static McpServerTool Tool(string name, string description, Delegate body, bool? readOnly = null, bool? destructive = null) =>
        McpServerTool.Create(body, new McpServerToolCreateOptions { Name = name, Description = description, ReadOnly = readOnly, Destructive = destructive });

    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after 15 s.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Did not happen in time: " + what);
            await Task.Delay(10);
        }
    }
}

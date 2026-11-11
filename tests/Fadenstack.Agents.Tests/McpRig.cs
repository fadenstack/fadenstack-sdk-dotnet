using System.ComponentModel;
using System.IO.Pipelines;
using Fadenstack.Client.Tests;
using Fadenstack.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// MCP servers in this process, over pipes, made with the MCP SDK's own server types: a new server for every connection, so a
/// crash and a restart can be tested. No network, no child process.
/// </summary>
public sealed class McpRig
{
    private readonly List<McpServer> _servers = new();
    private Pipe? _toClient;

    /// <summary>The tools every new connection serves.</summary>
    public List<McpServerTool> Tools { get; } = new();

    public string? Instructions { get; set; }

    /// <summary>How often the transport was asked to connect.</summary>
    public int Connects { get; private set; }

    /// <summary>From this connect on, the transport fails (a server that no longer starts).</summary>
    public int FailFrom { get; set; } = int.MaxValue;

    /// <summary>Called with every new server before it runs: register handlers here.</summary>
    public Action<McpServer>? OnServer { get; set; }

    /// <summary>The tool collection of the newest server: change it, then call <see cref="ToolsChangedAsync"/>.</summary>
    public McpServerPrimitiveCollection<McpServerTool>? Collection { get; private set; }

    public McpServer Current
    {
        get
        {
            lock (_servers)
            {
                return _servers[^1];
            }
        }
    }

    public LocalMcpServer Server(string label) => LocalMcpServer.FromTransport(label, ct =>
    {
        Connects++;
        if (Connects >= FailFrom)
        {
            throw new IOException("the program does not start");
        }
        var toServer = new Pipe();
        var toClient = new Pipe();
        _toClient = toClient;
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in Tools)
        {
            collection.Add(tool);
        }
        Collection = collection;
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = collection, ServerInstructions = Instructions });
        lock (_servers)
        {
            _servers.Add(server);
        }
        OnServer?.Invoke(server);
        _ = server.RunAsync();
        return Task.FromResult<IClientTransport>(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
    });

    /// <summary>The newest server tells its client the tool list changed (<c>notifications/tools/list_changed</c>).</summary>
    public Task ToolsChangedAsync() => Current.SendNotificationAsync(NotificationMethods.ToolListChangedNotification);

    /// <summary>The server's process ends: its side of the pipes closes.</summary>
    public async Task CrashAsync()
    {
        _toClient?.Writer.Complete(); // the client sees the end of the stream, as when a child process exits
        await Current.DisposeAsync();
    }

    public static McpServerTool Tool(string name, string description, Delegate body, bool? readOnly = null, bool? destructive = null) =>
        McpServerTool.Create(body, new McpServerToolCreateOptions
        {
            Name = name, Description = description, ReadOnly = readOnly, Destructive = destructive,
        });

    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after 15 s.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Did not happen in time: " + what);
            }
            await Task.Delay(10);
        }
    }

    /// <summary>The names of the tools in a model request.</summary>
    public static List<string> ToolNames(Recorded request) =>
        request.Json.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList()
            : new List<string>();

    /// <summary>A model request's tool by name: its <c>function</c> object.</summary>
    public static System.Text.Json.JsonElement ToolOf(Recorded request, string name) =>
        request.Json.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).Single(f => f.GetProperty("name").GetString() == name);

    /// <summary>The newest tool message of a model request: what the last tool call answered.</summary>
    public static string LastToolResult(Recorded request) =>
        request.Json.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
}

/// <summary>The tools the tests' servers have.</summary>
internal static class McpTools
{
    public static McpServerTool Count(List<string>? calls = null) => McpRig.Tool("count_terminals", "Counts the terminals on a page.",
        ([Description("The page.")] int page) =>
        {
            calls?.Add($"count {page}");
            return $"page {page}: 12 terminals";
        }, readOnly: true);
}

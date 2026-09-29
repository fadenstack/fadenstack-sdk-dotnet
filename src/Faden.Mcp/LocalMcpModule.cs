using Faden.Agents;
using Faden.Models;
using Faden.Tools;
using ModelContextProtocol.Client;

namespace Faden.Mcp;

/// <summary>A local server that was attached, and the tools it brought.</summary>
public sealed record AttachedMcpServer(string Label, IReadOnlyList<string> Tools);

/// <summary>A local server that was not attached, and why.</summary>
public sealed record SkippedMcpServer(string Label, string Reason, Exception? Error = null);

/// <summary>
/// Local MCP servers as agent tools (01-design §7.1). They are attached when the agent starts, if its policy
/// allows; their tools are <see cref="ToolClass.Unknown"/> -- MCP servers describe themselves, and their word
/// is not trusted -- so every call asks unless the user approved the tool for the session. Each attached
/// server is reported (label, program or URL, tool names).
/// </summary>
public sealed class LocalMcpModule : IFadenModule, IToolSource, IDisposable
{
    private readonly List<LocalMcpServer> _servers;
    private readonly List<McpClient> _clients = new();
    private readonly List<AttachedMcpServer> _attached = new();
    private readonly List<SkippedMcpServer> _skipped = new();

    public LocalMcpModule(IEnumerable<LocalMcpServer> servers, ILocalMcpPolicy? policy = null)
    {
        _servers = servers.ToList();
        Policy = policy ?? CoreLocalMcpPolicy.Instance;
    }

    public string? Name => "mcp.local";

    public ILocalMcpPolicy Policy { get; set; }

    /// <summary>Tool names as <c>label_tool</c>, for servers whose tool names clash. Off by default.</summary>
    public bool PrefixToolNames { get; set; }

    /// <summary>How long a server gets to start and list its tools (default 30 s).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<AttachedMcpServer> Attached => _attached;

    public IReadOnlyList<SkippedMcpServer> Skipped => _skipped;

    public void Configure(AgentBuilder builder) => builder.AddToolSource(this);

    public async Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var tools = new List<AgentTool>();
        foreach (var server in _servers)
        {
            var decision = Policy.Decide(server, context.Profile);
            if (!decision.Allowed)
            {
                _skipped.Add(new SkippedMcpServer(server.Label, decision.Reason)); // never started
                continue;
            }
            McpClient? client = null;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(ConnectTimeout);
                var transport = await server.Connect(limit.Token).ConfigureAwait(false);
                client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = "faden", Version = "0.2.0" },
                }, cancellationToken: limit.Token).ConfigureAwait(false);
                var listed = await client.ListToolsAsync(cancellationToken: limit.Token).ConfigureAwait(false);
                var names = new List<string>();
                foreach (var tool in listed)
                {
                    var function = PrefixToolNames ? tool.WithName($"{server.Label}_{tool.Name}") : tool;
                    if (tools.Any(t => t.Name == function.Name))
                    {
                        continue; // the first server's tool wins; PrefixToolNames keeps both
                    }
                    tools.Add(new AgentTool(function, ToolClass.Unknown, "mcp", server.Label,
                        tool.ProtocolTool.Annotations?.OpenWorldHint));
                    names.Add(function.Name);
                }
                _clients.Add(client);
                _attached.Add(new AttachedMcpServer(server.Label, names));
                await context.ReportAsync(new LocalMcpAttachedEvent
                {
                    Label = server.Label, Command = server.Command, Url = server.Url?.ToString(), Tools = names,
                }).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                _skipped.Add(new SkippedMcpServer(server.Label, $"Could not attach: {e.Message}", e)); // the agent runs without it
            }
        }
        return tools;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _clients.Clear();
    }
}

public static class LocalMcpAgentBuilderExtensions
{
    /// <summary>Local MCP servers, attached at start if the agent's policy allows (default: not).</summary>
    public static AgentBuilder AddLocalMcp(this AgentBuilder builder, params LocalMcpServer[] servers) =>
        builder.Use(new LocalMcpModule(servers));

    public static AgentBuilder AddLocalMcp(this AgentBuilder builder, LocalMcpModule module) => builder.Use(module);
}

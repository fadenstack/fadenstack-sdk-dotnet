using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// The roots feature is deprecated in the 2026-07-28 revision of MCP, but the design (doc 31, decision 2) tells servers the
// readable folders; the SDK still carries it. Legacy enum schemas are what older servers send.
#pragma warning disable MCP9001, MCP9005

namespace Fadenstack.Mcp;

/// <summary>A local server that was attached, and the tools it brought.</summary>
public sealed record AttachedMcpServer(string Label, IReadOnlyList<string> Tools);

/// <summary>A local server that was not attached, and why.</summary>
public sealed record SkippedMcpServer(string Label, string Reason, Exception? Error = null);

/// <summary>
/// Local MCP servers as agent tools, with one live client per server for the agent's lifetime. A server is attached when the
/// agent starts, if its policy allows; servers can be added, removed and switched on or off while the agent runs, and a
/// server that crashes is started again (at most <see cref="MaxReconnects"/> times).
/// <para>
/// The tools are not offered by default. While a server is attached the model gets one tool, <c>find_tool</c>, whose
/// description lists the servers; a search switches the best matches on for the rest of that conversation (at most
/// <see cref="MaxToolsInScope"/> at a time), and a tool called by name is switched on as well. Their tools are
/// <see cref="ToolClass.Unknown"/> -- MCP servers describe themselves, and their word (annotations) is not trusted -- so every
/// call asks unless the user approved the tool for the session. Results are fenced as untrusted data. Each attached server is
/// reported (label, program or URL, tool names), never its arguments, environment or headers.
/// </para>
/// <para>
/// The module declares roots (the host's folders), form-mode elicitation (when the host gives <see cref="Ask"/>) and never
/// sampling: a server cannot use the model.
/// </para>
/// </summary>
public sealed class LocalMcpModule : IFadenstackModule, IChangingToolSource, IDisposable
{
    /// <summary>The name of the tool that finds the other tools.</summary>
    public const string FindToolName = "find_tool";

    /// <summary>The most local tools in scope in one conversation; a new pick pushes out the least recently used.</summary>
    public const int MaxToolsInScope = 10;

    /// <summary>The most tools one search switches on.</summary>
    public const int MaxFound = 5;

    /// <summary>How often a crashed server is started again.</summary>
    public const int MaxReconnects = 3;

    /// <summary>A server that stayed up this long starts counting its crashes from zero again.</summary>
    private static readonly TimeSpan StableFor = TimeSpan.FromMinutes(5);

    private const int MaxFields = 20;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<ServerEntry> _entries = new();
    private readonly ConditionalWeakTable<AgentSession, ConversationScope> _scopes = new();
    private readonly Scope _scope;
    private AgentContext? _context;
    private string _announced = "";
    private int _epoch;
    private int _calls;
    private bool _disposed;

    public LocalMcpModule(IEnumerable<LocalMcpServer> servers, ILocalMcpPolicy? policy = null)
    {
        _scope = new Scope(this);
        foreach (var server in servers)
        {
            if (_entries.Any(e => e.Server.Label == server.Label))
            {
                throw new ArgumentException($"Two servers are labelled '{server.Label}'.", nameof(servers));
            }
            _entries.Add(new ServerEntry(server, enabled: true));
        }
        Policy = policy ?? CoreLocalMcpPolicy.Instance;
    }

    public string? Name => "mcp.local";

    public ILocalMcpPolicy Policy { get; set; }

    /// <summary>Tool names as <c>label_tool</c>, for servers whose tool names clash. Off by default.</summary>
    public bool PrefixToolNames { get; set; }

    /// <summary>How long a server gets to start and list its tools (default 30 s).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait before a crashed server is started again (default 1 s).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest a question waits for the person; then it counts as cancelled (default 10 minutes).</summary>
    public TimeSpan QuestionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The folders servers are told they may work in (<c>roots/list</c>). Null or empty: none. A hint, not a fence.</summary>
    public Func<IReadOnlyList<McpRoot>>? Roots { get; set; }

    /// <summary>
    /// Shows a server's question (form mode) to the person and returns the answer. Servers that start while it is set are told
    /// the client can be asked; without it a question is never declared supported.
    /// </summary>
    public Func<McpQuestion, CancellationToken, Task<McpAnswer>>? Ask { get; set; }

    /// <summary>Progress of a running call, for the turn's step line; called on a background thread, in order.</summary>
    public Action<McpProgress>? Progress { get; set; }

    /// <summary>Called with the server's label when a server asked to open a web page (URL-mode elicitation) and was declined.</summary>
    public Action<string>? UrlElicitationDeclined { get; set; }

    /// <summary>Raised when the tools this module offers changed; the agent asks again at the start of the next turn.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a server's status changed, or it was added or removed.</summary>
    public event EventHandler<McpServerStatusEventArgs>? StatusChanged;

    /// <summary>The servers that are running now.</summary>
    public IReadOnlyList<AttachedMcpServer> Attached
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => e.State == McpServerState.Running)
                    .Select(e => new AttachedMcpServer(e.Server.Label, e.Tools.Select(t => t.Name).ToList())).ToList();
            }
        }
    }

    /// <summary>The servers that are not running because the policy refused them or they could not start.</summary>
    public IReadOnlyList<SkippedMcpServer> Skipped
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => e.State is McpServerState.NotAllowed or McpServerState.Failed)
                    .Select(e => new SkippedMcpServer(e.Server.Label, e.Detail ?? "", e.Error)).ToList();
            }
        }
    }

    /// <summary>Every server, as a host shows it.</summary>
    public IReadOnlyList<McpServerStatus> Servers
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(Status).ToList();
            }
        }
    }

    public McpServerStatus? GetStatus(string label)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Server.Label == label).Select(Status).FirstOrDefault();
        }
    }

    public void Configure(AgentBuilder builder) => builder.AddToolSource(this);

    public async Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _context = context;
            List<ServerEntry> pending;
            lock (_gate)
            {
                pending = _entries.Where(e => e.Enabled && e.State == McpServerState.Starting && e.Client is null).ToList();
            }
            foreach (var entry in pending)
            {
                await ConnectAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            _announced = Signature();
            return BuildTools();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Adds a server and, when the agent runs and the policy allows, starts it. The tools change for the next turn. Its
    /// status says how it went; this only throws for a label already in use.
    /// </summary>
    public async Task AddAsync(LocalMcpServer server, bool enabled = true, CancellationToken cancellationToken = default)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        ServerEntry entry;
        try
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_entries.Any(e => e.Server.Label == server.Label))
                {
                    throw new ArgumentException($"A server labelled '{server.Label}' exists already.", nameof(server));
                }
                entry = new ServerEntry(server, enabled);
                _entries.Add(entry);
            }
            RaiseStatus(entry);
            if (enabled && _context is not null)
            {
                await ConnectAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            AnnounceIfChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops a server and forgets it. Its tools leave every conversation at the next turn.</summary>
    public async Task RemoveAsync(string label, CancellationToken cancellationToken = default)
    {
        var entry = Find(label);
        if (entry is null)
        {
            return;
        }
        entry.Stop.Cancel(); // a start or restart in progress gives up
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _entries.Remove(entry);
            }
            await CloseAsync(entry).ConfigureAwait(false);
            StatusChanged?.Invoke(this, new McpServerStatusEventArgs(label, null));
            AnnounceIfChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Switches a server on or off. On starts it again with a fresh count of restarts; off stops it.</summary>
    public async Task SetEnabledAsync(string label, bool enabled, CancellationToken cancellationToken = default)
    {
        var entry = Find(label) ?? throw new ArgumentException($"No server labelled '{label}'.", nameof(label));
        if (!enabled)
        {
            entry.Stop.Cancel();
        }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (entry.Enabled == enabled && (!enabled || entry.State is McpServerState.Running or McpServerState.Starting))
            {
                return;
            }
            if (enabled)
            {
                lock (_gate)
                {
                    entry.Enabled = true;
                    entry.Reconnects = 0;
                    entry.Stop = new CancellationTokenSource();
                    entry.State = McpServerState.Starting;
                    entry.Detail = null;
                    entry.Error = null;
                }
                RaiseStatus(entry);
                if (_context is not null)
                {
                    await ConnectAsync(entry, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await CloseAsync(entry).ConfigureAwait(false);
                lock (_gate)
                {
                    entry.Enabled = false;
                    entry.State = McpServerState.Off;
                    entry.Detail = null;
                    entry.Error = null;
                    entry.Stop = new CancellationTokenSource();
                }
                RaiseStatus(entry);
            }
            AnnounceIfChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Tells every running server that the roots changed (<c>notifications/roots/list_changed</c>); it asks again.</summary>
    public void NotifyRootsChanged() => _ = NotifyRootsChangedAsync();

    /// <summary>Like <see cref="NotifyRootsChanged"/>, and returns when the notices are sent. Never throws for a server that is gone.</summary>
    public async Task NotifyRootsChangedAsync()
    {
        List<McpClient> clients;
        lock (_gate)
        {
            clients = _entries.Where(e => e.Client is not null).Select(e => e.Client!).ToList();
        }
        foreach (var client in clients)
        {
            try
            {
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a server that went away has no roots to update
            }
        }
    }

    public void Dispose()
    {
        List<ServerEntry> entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            entries = _entries.ToList();
        }
        foreach (var entry in entries)
        {
            entry.Stop.Cancel();
        }
        foreach (var entry in entries)
        {
            McpClient? client;
            lock (_gate)
            {
                client = entry.Client;
                entry.Client = null;
            }
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LocalMcpModule));
        }
    }

    private ServerEntry? Find(string label)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => e.Server.Label == label);
        }
    }

    private static McpServerStatus Status(ServerEntry e) => new(e.Server.Label, e.State)
    {
        Detail = e.Detail,
        Instructions = e.Instructions,
        Tools = e.Tools.Select(t => t.Info).ToList(),
        ToolsChanged = e.ToolsChanged,
        Reconnects = e.Reconnects,
    };

    private void RaiseStatus(ServerEntry entry)
    {
        McpServerStatus status;
        lock (_gate)
        {
            status = Status(entry);
        }
        StatusChanged?.Invoke(this, new McpServerStatusEventArgs(status.Label, status));
    }

    // ---- start, stop, restart -------------------------------------------------------------------------------------------

    /// <summary>Starts a server under the policy and lists its tools. Never throws for the server's trouble: its status says.</summary>
    private async Task<bool> ConnectAsync(ServerEntry entry, CancellationToken cancellationToken)
    {
        var context = _context!;
        var log = context.LoggerFactory.CreateLogger(Log.Category);
        var server = entry.Server;
        var decision = Policy.Decide(server, context.Profile);
        if (!decision.Allowed)
        {
            lock (_gate)
            {
                entry.State = McpServerState.NotAllowed; // never started
                entry.Detail = decision.Reason;
            }
            Log.Refused(log, server.Label, server.Transport);
            RaiseStatus(entry);
            return false;
        }
        lock (_gate)
        {
            entry.State = McpServerState.Starting;
        }
        RaiseStatus(entry);
        McpClient? client = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Stop.Token);
            limit.CancelAfter(ConnectTimeout);
            var transport = await server.Connect(limit.Token).ConfigureAwait(false);
            client = await McpClient.CreateAsync(transport, Options(entry), cancellationToken: limit.Token).ConfigureAwait(false);
            var listed = await client.ListToolsAsync(cancellationToken: limit.Token).ConfigureAwait(false);
            var first = entry.Tools.Count == 0 && !entry.EverListed;
            lock (_gate)
            {
                var differs = ApplyTools(entry, listed);
                entry.ToolsChanged |= differs && !first;
                entry.EverListed = true;
                entry.Client = client;
                entry.State = McpServerState.Running;
                entry.Detail = null;
                entry.Error = null;
                entry.Instructions = McpText.FirstLine(client.ServerInstructions, McpText.MaxIndexChars);
                entry.ConnectedAt = DateTime.UtcNow;
            }
            _ = WatchAsync(entry, client);
            Log.Attached(log, server.Label, server.Transport, listed.Count);
            RaiseStatus(entry);
            await ReportAttachedAsync(entry).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                entry.State = McpServerState.Failed;
                entry.Detail = entry.Stop.IsCancellationRequested ? "Stopped." : $"Could not attach: {McpText.OneLine(e.Message, 300)}";
                entry.Error = e; // the agent runs without it
            }
            Log.AttachFailed(log, server.Label, server.Transport, e.GetType().Name);
            RaiseStatus(entry);
            return false;
        }
    }

    private async Task CloseAsync(ServerEntry entry)
    {
        McpClient? client;
        lock (_gate)
        {
            client = entry.Client;
            entry.Client = null; // before the dispose: the watcher then knows it was no crash
            entry.Tools = Array.Empty<ToolEntry>();
            entry.Instructions = null;
            entry.ToolsChanged = false;
        }
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReportAttachedAsync(ServerEntry entry)
    {
        try
        {
            List<string> names;
            lock (_gate)
            {
                names = entry.Tools.Select(t => t.Name).ToList();
            }
            await _context!.ReportAsync(new LocalMcpAttachedEvent
            {
                Label = entry.Server.Label, Command = entry.Server.Command, Url = entry.Server.Url?.ToString(), Tools = names,
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // a report that fails never fails a server
        }
    }

    /// <summary>Waits for the connection to end; if nobody closed it on purpose, it crashed.</summary>
    private async Task WatchAsync(ServerEntry entry, McpClient client)
    {
        try
        {
            await client.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the end is what matters, not how
        }
        try
        {
            await RestartAsync(entry, client).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
        {
            // the module ended
        }
    }

    private async Task RestartAsync(ServerEntry entry, McpClient crashed)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(entry.Client, crashed) || !_entries.Contains(entry))
                {
                    return; // closed on purpose, or replaced
                }
                entry.Client = null;
                if (DateTime.UtcNow - entry.ConnectedAt >= StableFor)
                {
                    entry.Reconnects = 0;
                }
                entry.State = McpServerState.Starting;
            }
            RaiseStatus(entry);
            AnnounceIfChanged(); // its tools leave the list at the next turn
            var log = _context!.LoggerFactory.CreateLogger(Log.Category);
            while (true)
            {
                int attempt;
                lock (_gate)
                {
                    if (entry.Reconnects >= MaxReconnects || !entry.Enabled || entry.Stop.IsCancellationRequested)
                    {
                        if (entry.State != McpServerState.Off)
                        {
                            entry.State = McpServerState.Failed;
                            entry.Detail = entry.Stop.IsCancellationRequested
                                ? "Stopped."
                                : $"Stopped after {entry.Reconnects} restarts. {entry.Detail}".Trim();
                        }
                        break;
                    }
                    attempt = ++entry.Reconnects;
                    entry.Detail = "The server stopped.";
                }
                Log.Restarting(log, entry.Server.Label, entry.Server.Transport, attempt, MaxReconnects);
                RaiseStatus(entry);
                if (ReconnectDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(ReconnectDelay, entry.Stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                }
                if (await ConnectAsync(entry, CancellationToken.None).ConfigureAwait(false))
                {
                    break;
                }
                lock (_gate)
                {
                    entry.State = McpServerState.Starting;
                    // the reason of the last failed try stays in Detail for the final status
                }
            }
            RaiseStatus(entry);
            AnnounceIfChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private McpClientOptions Options(ServerEntry entry)
    {
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "fadenstack", Version = SdkInfo.Version },
            Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
            Handlers = new McpClientHandlers
            {
                RootsHandler = (_, _) => new ValueTask<ListRootsResult>(RootsResult()),
                NotificationHandlers = new[]
                {
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification, (_, _) =>
                        {
                            _ = RefreshToolsAsync(entry);
                            return default;
                        }),
                },
            },
        };
        // Sampling is never declared: a server cannot use the model. Form questions only when the host can show them.
        if (Ask is not null)
        {
            options.Capabilities.Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() };
            options.Handlers.ElicitationHandler = (request, ct) => AnswerAsync(entry, request!, ct);
        }
        return options;
    }

    private ListRootsResult RootsResult()
    {
        var roots = new List<Root>();
        try
        {
            foreach (var root in Roots?.Invoke() ?? Array.Empty<McpRoot>())
            {
                if (!string.IsNullOrWhiteSpace(root.Uri))
                {
                    roots.Add(new Root { Uri = root.Uri, Name = root.Name is null ? null : McpText.OneLine(root.Name, 200) });
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            roots.Clear(); // a host that fails tells the server no folders
        }
        return new ListRootsResult { Roots = roots };
    }

    // ---- tools ----------------------------------------------------------------------------------------------------------

    /// <summary>Lists the server's tools again after <c>notifications/tools/list_changed</c>.</summary>
    private async Task RefreshToolsAsync(ServerEntry entry)
    {
        try
        {
            McpClient? client;
            lock (_gate)
            {
                client = entry.Client;
            }
            if (client is null)
            {
                return;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token);
            limit.CancelAfter(ConnectTimeout);
            var listed = await client.ListToolsAsync(cancellationToken: limit.Token).ConfigureAwait(false);
            bool differs;
            lock (_gate)
            {
                if (!ReferenceEquals(entry.Client, client))
                {
                    return;
                }
                differs = ApplyTools(entry, listed);
                entry.ToolsChanged |= differs;
            }
            if (differs)
            {
                if (_context is not null)
                {
                    Log.ToolsChanged(_context.LoggerFactory.CreateLogger(Log.Category), entry.Server.Label, listed.Count);
                }
                RaiseStatus(entry);
                AnnounceIfChanged();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the old list stays; the server may send another notice
        }
    }

    /// <summary>
    /// Sets the entry's tools from a fresh list: a tool whose definition is the same keeps its version; a new or changed one gets
    /// a new version, which switches it off in every conversation. Returns whether anything differs. Call under the lock.
    /// </summary>
    private bool ApplyTools(ServerEntry entry, IList<McpClientTool> listed)
    {
        var old = entry.Tools.ToDictionary(t => t.ServerName, StringComparer.Ordinal);
        var tools = new List<ToolEntry>();
        var differs = old.Count != listed.Count;
        foreach (var tool in listed)
        {
            if (tools.Any(t => t.ServerName == tool.Name))
            {
                continue; // a server that lists a name twice
            }
            var fingerprint = Fingerprint(tool);
            if (old.TryGetValue(tool.Name, out var same) && same.Fingerprint == fingerprint)
            {
                tools.Add(same);
                continue;
            }
            differs = true;
            tools.Add(new ToolEntry(this, entry, tool, PrefixToolNames ? $"{entry.Server.Label}_{tool.Name}" : tool.Name, fingerprint,
                Interlocked.Increment(ref _epoch)));
        }
        entry.Tools = tools;
        return differs || tools.Count != old.Count;
    }

    private static string Fingerprint(McpClientTool tool)
    {
        var protocol = tool.ProtocolTool;
        var schema = tool.JsonSchema.ValueKind == JsonValueKind.Undefined ? "" : tool.JsonSchema.GetRawText();
        var a = protocol.Annotations;
        return string.Join("\u0001", protocol.Description, protocol.Title, schema, a?.ReadOnlyHint, a?.DestructiveHint, a?.IdempotentHint, a?.OpenWorldHint);
    }

    /// <summary>What the module offers now: the running servers' index and tool names with versions. A change means a new list.</summary>
    private string Signature()
    {
        var builder = new StringBuilder();
        lock (_gate)
        {
            foreach (var e in _entries.Where(e => e.State == McpServerState.Running))
            {
                builder.Append(e.Server.Label).Append('\u0001').Append(e.Instructions).Append('\u0001');
                foreach (var t in e.Tools)
                {
                    builder.Append(t.Name).Append(':').Append(t.Version).Append(',');
                }
                builder.Append('\n');
            }
        }
        return builder.ToString();
    }

    private void AnnounceIfChanged()
    {
        var signature = Signature();
        bool changed;
        lock (_gate)
        {
            changed = signature != _announced;
            _announced = signature;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The agent's tools: <c>find_tool</c> and, held back until found, every running server's tools. The first server's tool wins a name.</summary>
    private IReadOnlyList<AgentTool> BuildTools()
    {
        var tools = new List<AgentTool>();
        var index = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { FindToolName };
        lock (_gate)
        {
            foreach (var entry in _entries.Where(e => e.State == McpServerState.Running))
            {
                index.Add(entry.Instructions is { Length: > 0 } line
                    ? $"{McpText.OneLine(entry.Server.Label, 40)}: {line}"
                    : McpText.OneLine(entry.Server.Label, 40));
                foreach (var tool in entry.Tools)
                {
                    if (seen.Add(tool.Name))
                    {
                        tools.Add(new AgentTool(tool.Function, ToolClass.Unknown, "mcp", entry.Server.Label, tool.OpenWorld)
                        {
                            Scope = _scope, ReadOnlyHint = tool.Info.Hints.ReadOnly, DestructiveHint = tool.Info.Hints.Destructive,
                        });
                    }
                }
            }
        }
        if (index.Count > 0)
        {
            tools.Insert(0, new AgentTool(new FindToolFunction(this, index), ToolClass.Read, "mcp") { Scope = _scope });
        }
        return tools;
    }

    /// <summary>The live tool of this name, or null when none is attached.</summary>
    private ToolEntry? Current(string exposedName)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.State == McpServerState.Running).SelectMany(e => e.Tools).FirstOrDefault(t => t.Name == exposedName);
        }
    }

    // ---- find_tool and the conversation's scope ---------------------------------------------------------------------------

    /// <summary>Runs a search for <c>find_tool</c>: switches the best matches on in the conversation and lists them.</summary>
    private string Find(AgentSession? session, string query)
    {
        if (session is null)
        {
            return "Error: tools can only be switched on inside a conversation.";
        }
        List<ToolEntry> candidates;
        lock (_gate)
        {
            candidates = _entries.Where(e => e.State == McpServerState.Running).SelectMany(e => e.Tools).ToList();
        }
        var ranked = McpText.Rank(candidates.Select(t => (t.Name, t.Description)).ToList(), query ?? "", MaxFound);
        if (ranked.Count == 0)
        {
            return "No tool on this computer matched that search. Try other words.";
        }
        var scope = _scopes.GetOrCreateValue(session);
        var found = ranked.Select(r => candidates[r.Index]).ToList();
        scope.SwitchOn(found);
        var lines = string.Join("\n", found.Select(t => $"{t.Name}: {McpText.OneLine(t.Description, 120)}"));
        var fenced = McpText.Fence("tool search", "find_tool", lines);
        return $"Switched on {found.Count} tool{(found.Count == 1 ? "" : "s")} for this conversation. Call them with their parameters.\n{fenced}";
    }

    private sealed class Scope : IToolScope
    {
        private readonly LocalMcpModule _module;

        public Scope(LocalMcpModule module) => _module = module;

        public bool Offers(AgentTool tool, AgentSession session)
        {
            if (tool.Function is FindToolFunction)
            {
                return true;
            }
            if (tool.Function is not McpFunction function || _module.Current(function.Tool.Name) is not { } current || current != function.Tool)
            {
                return false; // gone, or changed since this list was made
            }
            return _module._scopes.TryGetValue(session, out var scope) && scope.IsOn(current);
        }

        public string? BeforeCall(AgentTool tool, AgentSession session)
        {
            if (tool.Function is not McpFunction function)
            {
                return null;
            }
            var current = _module.Current(function.Tool.Name);
            if (current is null)
            {
                return $"{tool.Name} is no longer available.";
            }
            var scope = _module._scopes.GetOrCreateValue(session);
            if (current != function.Tool)
            {
                scope.SwitchOff(current.Name);
                return $"{tool.Name} changed its definition while you worked. Search for it again with {FindToolName}.";
            }
            if (scope.IsOn(current))
            {
                scope.Touch(current);
                return null;
            }
            scope.SwitchOn(new[] { current });
            return $"{tool.Name} is now switched on. Call it again with its parameters.";
        }
    }

    /// <summary>The local tools one conversation has switched on: at most <see cref="MaxToolsInScope"/>, the least recently used out first.</summary>
    private sealed class ConversationScope
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (int Version, long Used)> _on = new(StringComparer.Ordinal);
        private long _clock;

        public bool IsOn(ToolEntry tool)
        {
            lock (_lock)
            {
                return _on.TryGetValue(tool.Name, out var on) && on.Version == tool.Version;
            }
        }

        public void Touch(ToolEntry tool)
        {
            lock (_lock)
            {
                _on[tool.Name] = (tool.Version, ++_clock);
            }
        }

        public void SwitchOff(string name)
        {
            lock (_lock)
            {
                _on.Remove(name);
            }
        }

        /// <summary>Switches the tools on, the first of them as the most recently used; the cap pushes the oldest out.</summary>
        public void SwitchOn(IReadOnlyList<ToolEntry> tools)
        {
            lock (_lock)
            {
                for (var i = tools.Count - 1; i >= 0; i--)
                {
                    _on[tools[i].Name] = (tools[i].Version, ++_clock);
                }
                while (_on.Count > MaxToolsInScope)
                {
                    var oldest = _on.OrderBy(p => p.Value.Used).First().Key;
                    _on.Remove(oldest);
                }
            }
        }
    }

    // ---- calls ----------------------------------------------------------------------------------------------------------

    private async Task<object?> CallAsync(ToolEntry tool, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var entry = tool.Server;
        var invocation = arguments.Services?.GetService(typeof(ToolInvocation)) as ToolInvocation;
        McpClient? client;
        lock (_gate)
        {
            client = entry.Client;
        }
        if (client is null)
        {
            return $"Error: the MCP server {McpText.Plain(entry.Server.Label, 40)} is not running.";
        }
        var call = new ActiveCall(invocation?.SessionId ?? "", invocation?.TurnId ?? "", tool.ServerName);
        lock (_gate)
        {
            entry.Calls.Add(call);
        }
        var label = entry.Server.Label;
        var timeout = entry.Server.CallTimeout;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        RequestId? id = null;
        try
        {
            var args = new JsonObject();
            foreach (var pair in arguments)
            {
                args[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, McpJsonUtilities.DefaultOptions);
            }
            // The request carries our own id and progress token: the SDK's call helper offers no way to send the cancel notice
            // (notifications/cancelled) a stopped call needs, and a server only stops its work when it gets it.
            var number = Interlocked.Increment(ref _calls);
            var requestId = new RequestId("fadenstack-" + number);
            id = requestId;
            var progressToken = "fadenstack-progress-" + number;
            var request = new JsonRpcRequest
            {
                Id = requestId,
                Method = RequestMethods.ToolsCall,
                Params = new JsonObject
                {
                    ["name"] = tool.ServerName, ["arguments"] = args, ["_meta"] = new JsonObject { ["progressToken"] = progressToken },
                },
            };
            await using var listening = client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (notice, _) =>
            {
                try
                {
                    var value = notice.Params.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions);
                    if (value is not null && value.ProgressToken.ToString() == progressToken)
                    {
                        Progress?.Invoke(new McpProgress(call.SessionId, call.TurnId, label, tool.ServerName,
                            value.Progress.Message is null ? null : McpText.OneLine(value.Progress.Message, 200), value.Progress.Progress, value.Progress.Total));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // a host that fails, or a notice we cannot read, does not fail the call
                }
                return default;
            });
            var response = await client.SendRequestAsync(request, limit.Token).ConfigureAwait(false);
            var result = response.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions) ?? new CallToolResult();
            return McpText.Fence(label, tool.ServerName, Describe(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(client, id, "timeout").ConfigureAwait(false);
            if (_context is not null)
            {
                Log.CallTimedOut(_context.LoggerFactory.CreateLogger(Log.Category), label, (int)Math.Ceiling(timeout.TotalSeconds));
            }
            return $"Error: the MCP server {McpText.Plain(label, 40)} did not answer within {(int)Math.Ceiling(timeout.TotalSeconds)} seconds; the call was cancelled.";
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(client, id, "stopped").ConfigureAwait(false); // the person stopped it: the server stops its work too
            throw;
        }
        catch (McpException e)
        {
            return McpText.Fence(label, tool.ServerName, "Error: " + e.Message);
        }
        catch (InvalidOperationException e) when (e.Message.IndexOf("Sampling", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return $"Error: the MCP server {McpText.Plain(label, 40)} asked to use the model, which this app never allows; the call was stopped.";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or HttpRequestException)
        {
            return $"Error: the MCP server {McpText.Plain(label, 40)} stopped answering.";
        }
        finally
        {
            lock (_gate)
            {
                entry.Calls.Remove(call);
            }
        }
    }

    /// <summary>Tells the server a call was cancelled (<c>notifications/cancelled</c>), so it stops the work. Best effort.</summary>
    private static async Task CancelAsync(McpClient client, RequestId? id, string reason)
    {
        if (id is null)
        {
            return;
        }
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = id.Value, Reason = reason }, cancellationToken: limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the server is gone, or slow: nothing more to do
        }
    }

    private static string Describe(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (var block in result.Content ?? new List<ContentBlock>())
        {
            switch (block)
            {
                case TextContentBlock text:
                    parts.Add(text.Text);
                    break;
                case ImageContentBlock:
                    parts.Add("[A picture from the server was left out.]");
                    break;
                case AudioContentBlock:
                    parts.Add("[Audio from the server was left out.]");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add("[Content of a kind this app does not read was left out.]");
                    break;
            }
        }
        if (parts.Count == 0 && result.StructuredContent is { } structured)
        {
            parts.Add(structured.GetRawText());
        }
        var body = string.Join("\n", parts);
        return result.IsError == true ? "The tool reported an error:\n" + body : body;
    }

    // ---- questions ------------------------------------------------------------------------------------------------------

    /// <summary>A server's question: form mode goes to the host's card; URL mode and anything this app cannot show are declined.</summary>
    private async ValueTask<ElicitResult> AnswerAsync(ServerEntry entry, ElicitRequestParams request, CancellationToken cancellationToken)
    {
        var log = _context?.LoggerFactory.CreateLogger(Log.Category);
        var label = entry.Server.Label;
        if (!string.Equals(request.Mode, "form", StringComparison.Ordinal))
        {
            // Opening a server's web page belongs to a later step (the network step); nothing is opened.
            try
            {
                UrlElicitationDeclined?.Invoke(label);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a host that fails changes nothing: the answer is no
            }
            if (log is not null)
            {
                Log.Question(log, label, "url_declined");
            }
            return new ElicitResult { Action = "decline" };
        }
        var ask = Ask;
        var fields = ask is null ? null : Fields(request);
        if (ask is null || fields is null)
        {
            if (log is not null)
            {
                Log.Question(log, label, "declined");
            }
            return new ElicitResult { Action = "decline" };
        }
        ActiveCall? call;
        lock (_gate)
        {
            call = entry.Calls.LastOrDefault();
        }
        var question = new McpQuestion(call?.SessionId ?? "", call?.TurnId ?? "", label, Message(request.Message), fields);
        McpAnswer answer;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Stop.Token);
            limit.CancelAfter(QuestionTimeout);
            answer = await ask(question, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            answer = McpAnswer.Cancel(); // the person did not answer, or the card failed
        }
        var result = Result(answer, fields);
        if (log is not null)
        {
            Log.Question(log, label, result.Action + "ed");
        }
        return result;
    }

    /// <summary>A server's message for the person: hidden characters out, at most 1,000 characters.</summary>
    private static string Message(string? text)
    {
        var clean = McpText.CleanResult(text ?? "").Trim();
        return clean.Length > 1000 ? clean.Substring(0, char.IsHighSurrogate(clean[999]) ? 999 : 1000) + "…" : clean;
    }

    /// <summary>The form's fields in this app's own types; null when the form has a field this app cannot show.</summary>
    private static IReadOnlyList<McpQuestionField>? Fields(ElicitRequestParams request)
    {
        var properties = request.RequestedSchema?.Properties;
        if (properties is null || properties.Count > MaxFields)
        {
            return properties is null ? Array.Empty<McpQuestionField>() : null;
        }
        var required = new HashSet<string>(request.RequestedSchema!.Required ?? new List<string>(), StringComparer.Ordinal);
        var fields = new List<McpQuestionField>();
        foreach (var pair in properties)
        {
            var (kind, choices) = pair.Value switch
            {
                ElicitRequestParams.StringSchema => (McpFieldKind.Text, Array.Empty<McpChoice>()),
                ElicitRequestParams.NumberSchema n => (n.Type == "integer" ? McpFieldKind.Integer : McpFieldKind.Number, Array.Empty<McpChoice>()),
                ElicitRequestParams.BooleanSchema => (McpFieldKind.Boolean, Array.Empty<McpChoice>()),
                ElicitRequestParams.UntitledSingleSelectEnumSchema u => (McpFieldKind.Choice,
                    u.Enum.Select(v => new McpChoice(v, McpText.OneLine(v, 120))).ToArray()),
                ElicitRequestParams.TitledSingleSelectEnumSchema t => (McpFieldKind.Choice,
                    t.OneOf.Select(o => new McpChoice(o.Const, McpText.OneLine(o.Title, 120))).ToArray()),
                ElicitRequestParams.LegacyTitledEnumSchema l => (McpFieldKind.Choice,
                    l.Enum.Select((v, i) => new McpChoice(v, McpText.OneLine(l.EnumNames is { } names && i < names.Count ? names[i] : v, 120))).ToArray()),
                _ => (McpFieldKind.Text, (McpChoice[]?)null),
            };
            if (choices is null)
            {
                return null; // a kind of field this app cannot show (several choices at once)
            }
            if (McpText.OneLine(pair.Key, 80) != pair.Key)
            {
                return null; // a field name with hidden characters or too long: the answer could not be matched to it
            }
            fields.Add(new McpQuestionField(pair.Key, Clean(pair.Value.Title, 200), Clean(pair.Value.Description, 500), kind,
                required.Contains(pair.Key), choices));
        }
        return fields;
    }

    private static string? Clean(string? text, int max) => text is null ? null : McpText.OneLine(text, max) is { Length: > 0 } one ? one : null;

    /// <summary>The person's answer in the server's terms. Values for unknown fields are dropped; a missing required one cancels.</summary>
    private static ElicitResult Result(McpAnswer answer, IReadOnlyList<McpQuestionField> fields)
    {
        switch (answer.Kind)
        {
            case McpAnswerKind.Decline:
                return new ElicitResult { Action = "decline" };
            case McpAnswerKind.Accept:
                break;
            default:
                return new ElicitResult { Action = "cancel" };
        }
        var content = new Dictionary<string, JsonElement>();
        foreach (var field in fields)
        {
            if (answer.Values is null || !answer.Values.TryGetValue(field.Name, out var value) || value is null || ToJson(field, value) is not { } element)
            {
                if (field.Required)
                {
                    return new ElicitResult { Action = "cancel" };
                }
                continue;
            }
            content[field.Name] = element;
        }
        return new ElicitResult { Action = "accept", Content = content };
    }

    private static JsonElement? ToJson(McpQuestionField field, object value)
    {
        if (value is JsonElement json)
        {
            value = json.ValueKind switch
            {
                JsonValueKind.String => json.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => json.GetDouble(),
                _ => value,
            };
        }
        switch (field.Kind)
        {
            case McpFieldKind.Text when value is string text:
                return JsonSerializer.SerializeToElement(text);
            case McpFieldKind.Boolean when value is bool flag:
                return JsonSerializer.SerializeToElement(flag);
            case McpFieldKind.Choice when value is string pick && field.Choices.Any(c => c.Value == pick):
                return JsonSerializer.SerializeToElement(pick);
            case McpFieldKind.Integer or McpFieldKind.Number:
                double number;
                switch (value)
                {
                    case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                        number = parsed;
                        break;
                    case IConvertible when value is not string and not bool:
                        number = System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        return null;
                }
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    return null;
                }
                return field.Kind == McpFieldKind.Integer && Math.Abs(number) < 9e15
                    ? JsonSerializer.SerializeToElement((long)Math.Round(number))
                    : JsonSerializer.SerializeToElement(number);
            default:
                return null;
        }
    }

    // ---- the pieces -------------------------------------------------------------------------------------------------------

    private sealed record ActiveCall(string SessionId, string TurnId, string Tool);

    private sealed class ServerEntry
    {
        public ServerEntry(LocalMcpServer server, bool enabled)
        {
            Server = server;
            Enabled = enabled;
            State = enabled ? McpServerState.Starting : McpServerState.Off;
        }

        public LocalMcpServer Server { get; }

        public bool Enabled { get; set; }

        public McpServerState State { get; set; }

        public string? Detail { get; set; }

        public Exception? Error { get; set; }

        public string? Instructions { get; set; }

        public McpClient? Client { get; set; }

        public IReadOnlyList<ToolEntry> Tools { get; set; } = Array.Empty<ToolEntry>();

        public bool ToolsChanged { get; set; }

        public bool EverListed { get; set; }

        public int Reconnects { get; set; }

        public DateTime ConnectedAt { get; set; }

        public List<ActiveCall> Calls { get; } = new();

        /// <summary>Cancelled when the server is removed or switched off, so a start in progress gives up.</summary>
        public CancellationTokenSource Stop { get; set; } = new();
    }

    private sealed class ToolEntry
    {
        public ToolEntry(LocalMcpModule module, ServerEntry server, McpClientTool tool, string name, string fingerprint, int version)
        {
            Server = server;
            ServerName = tool.Name;
            Name = name;
            Fingerprint = fingerprint;
            Version = version;
            Description = McpText.CleanResult(tool.Description ?? "").Trim();
            if (Description.Length > 1024)
            {
                Description = Description.Substring(0, 1023) + "…";
            }
            var hints = tool.ProtocolTool.Annotations;
            Info = new McpToolInfo(name, McpText.OneLine(Description, 300), new McpToolHints(
                hints?.Title is null ? null : McpText.OneLine(hints.Title, 120), hints?.ReadOnlyHint, hints?.DestructiveHint, hints?.IdempotentHint,
                hints?.OpenWorldHint));
            OpenWorld = hints?.OpenWorldHint;
            Function = new McpFunction(module, this, tool.JsonSchema);
        }

        public ServerEntry Server { get; }

        /// <summary>The name the server knows the tool by.</summary>
        public string ServerName { get; }

        /// <summary>The name the model calls it by (<see cref="LocalMcpModule.PrefixToolNames"/>).</summary>
        public string Name { get; }

        public string Description { get; }

        public string Fingerprint { get; }

        public int Version { get; }

        public McpToolInfo Info { get; }

        public bool? OpenWorld { get; }

        public AIFunction Function { get; }
    }

    /// <summary>A server's tool as the agent runs it: the call goes to the live client of the server, whatever client that is now.</summary>
    private sealed class McpFunction : AIFunction
    {
        private readonly LocalMcpModule _module;
        private readonly JsonElement _schema;

        public McpFunction(LocalMcpModule module, ToolEntry tool, JsonElement schema)
        {
            _module = module;
            Tool = tool;
            _schema = schema;
        }

        public ToolEntry Tool { get; }

        public override string Name => Tool.Name;

        public override string Description => Tool.Description;

        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await _module.CallAsync(Tool, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>find_tool(query)</c>. The description is one fixed sentence and the index of the attached servers, so its bytes stay
    /// the same from turn to turn until a server comes or goes.
    /// </summary>
    private sealed class FindToolFunction : AIFunction
    {
        private const string Sentence =
            "Finds tools from MCP servers on this computer and switches the best matches on for this conversation. "
            + "Call it with a few words about what you need; the tools it names can then be called with their parameters.";

        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{"type":"object","properties":{"query":{"type":"string","description":"A few words about what you need."}},"required":["query"]}""")
            .RootElement.Clone();

        private readonly LocalMcpModule _module;

        public FindToolFunction(LocalMcpModule module, IReadOnlyList<string> index)
        {
            _module = module;
            Description = Sentence + "\nServers on this computer:\n" + string.Join("\n", index);
        }

        public override string Name => FindToolName;

        public override string Description { get; }

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var invocation = arguments.Services?.GetService(typeof(ToolInvocation)) as ToolInvocation;
            string? query = null;
            if (arguments.TryGetValue("query", out var value))
            {
                query = value switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    _ => null,
                };
            }
            return new ValueTask<object?>(string.IsNullOrWhiteSpace(query)
                ? "Error: give a few words about the tool you need as 'query'."
                : _module.Find(invocation?.Session, query!.Length > 500 ? query.Substring(0, 500) : query));
        }
    }
}

public static class LocalMcpAgentBuilderExtensions
{
    /// <summary>Local MCP servers, attached at start if the agent's policy allows (default: not).</summary>
    public static AgentBuilder AddLocalMcp(this AgentBuilder builder, params LocalMcpServer[] servers) =>
        builder.Use(new LocalMcpModule(servers));

    public static AgentBuilder AddLocalMcp(this AgentBuilder builder, LocalMcpModule module) => builder.Use(module);
}

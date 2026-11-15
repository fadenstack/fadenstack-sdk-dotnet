using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Tools;
using ModelContextProtocol.Client;

namespace Fadenstack.Mcp;

/// <summary>A server of the person with where it stands now: the entry as kept, and the live module's status (none while it does not run).</summary>
public sealed record McpServerRow(McpServerEntry Entry, McpServerStatus? Status)
{
    public string Label => Entry.Label;

    /// <summary>The state to show: the module's, else Off or Starting by the switch.</summary>
    public McpServerState State => Status?.State ?? (Entry.Enabled ? McpServerState.Starting : McpServerState.Off);
}

/// <summary>How a test of a server went; <see cref="Error"/> is a short cleaned line, never a secret.</summary>
public sealed record McpTestResult(bool Started, IReadOnlyList<string> Tools, string? Error = null, bool NotAllowed = false)
{
    /// <summary>Why plain http to it was refused (sdk/51 F05), when it was: nothing was sent.</summary>
    public McpPlainHttpRefusal? PlainHttp { get; init; }
}

/// <summary>
/// The person's local MCP servers in the running app (desktop-agent/31 §4.2 to §4.4, §4.7; sdk/51 F05, D14): the store of the
/// signed-in user (or of the standalone principal: <see cref="UseStandaloneAsync"/>), and the SDK's <see cref="LocalMcpModule"/> of
/// the agent that runs now, kept in step. The agent is made again after a sign-out, so
/// the host makes a new module for each agent (<see cref="NewModule"/>, which stops the old one and its programs) and loads the
/// signed-in user's servers into it (<see cref="UseUserAsync"/>); the Tools page talks to the host, which outlives both. The
/// host's folders (the desktop's readable folders) are the servers' roots through the <see cref="McpRootsSource"/>, and a change
/// of them tells every running server.
/// <para>
/// Whose word counts for trust (H4): by default the person's, from the entry (<see cref="McpServerEntry.Trusted"/>,
/// <see cref="McpServerEntry.ToolClasses"/>). A governed host passes <c>marks</c> instead, which says what the administrator marked.
/// </para>
/// </summary>
public sealed class McpHost : IDisposable
{
    private readonly Func<string, McpServerStore> _openStore;
    private readonly McpRootsSource? _roots;
    private readonly Func<McpServerEntry, McpServerMarks?> _marks;
    private readonly Func<McpServerEntry, McpConnection, CancellationToken, Task<IClientTransport>> _transport;
    private readonly Func<McpServerEntry, LocalMcpServer> _server;
    private readonly object _gate = new();
    private LocalMcpModule? _module;
    private McpServerStore? _store;
    private string? _userId;
    private McpAuditLog? _audit;
    private Task _loading = Task.CompletedTask;
    private bool _disposed;

    /// <param name="openStore">Opens the store of a Fadenstack user; <see cref="McpServerStore.ForUser"/> if not given.</param>
    /// <param name="roots">The folders the host tells servers they may work in, followed live; none if not given.</param>
    /// <param name="transport">How a test of a server connects; the real transports if not given (tests pass an in-process one).</param>
    /// <param name="server">The SDK's server for an entry; <see cref="ToServer(McpServerEntry)"/> if not given (tests pass an in-process one).</param>
    /// <param name="marks">Whose trust marks and tool raises count: the entry's own (the person's) if not given; a governed host gives the administrator's.</param>
    public McpHost(Func<string, McpServerStore>? openStore = null, McpRootsSource? roots = null,
        Func<McpServerEntry, IClientTransport>? transport = null, Func<McpServerEntry, LocalMcpServer>? server = null,
        Func<McpServerEntry, McpServerMarks?>? marks = null)
    {
        MarksArePersons = marks is null;
        _marks = marks ?? (e => new McpServerMarks(e.Trusted, e.ToolClasses));
        var make = server ?? (e => ToServer(e, null));
        _server = e =>
        {
            var made = make(e);
            if (_marks(e) is { } own)
            {
                made.Trusted = own.Trusted;
                made.ToolClasses = own.ToolClasses;
            }
            return made;
        };
        _openStore = openStore ?? (id => McpServerStore.ForUser(id));
        _roots = roots;
        // A test of an entry connects as the agent would: the same address rules (sdk/51 F05) through the SDK's server.
        _transport = transport is null
            ? (e, connection, cancellationToken) => ToServer(e, null).Connect(connection, cancellationToken)
            : (e, _, _) => Task.FromResult(transport(e));
        if (_roots is not null)
        {
            _roots.Changed += OnRootsChanged;
        }
    }

    /// <summary>
    /// The person's own marks count (standalone: the default). False: the host was given an administrator's marks, and a view
    /// shows them without a way to change them (a change of the person's would have no effect).
    /// </summary>
    public bool MarksArePersons { get; }

    /// <summary>The marks that count for <paramref name="entry"/> now (the person's or the administrator's); null when the host has none for it.</summary>
    public McpServerMarks? MarksOf(McpServerEntry entry) => _marks(entry);

    /// <summary>The module of the agent that runs now.</summary>
    public LocalMcpModule? Module
    {
        get
        {
            lock (_gate)
                return _module;
        }
    }

    /// <summary>The signed-in user's store, or null until a user is known.</summary>
    public McpServerStore? Store
    {
        get
        {
            lock (_gate)
                return _store;
        }
    }

    /// <summary>The servers changed, or one's status did. From any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Completes when the servers of the signed-in user have been handed to the module (they may still be starting).</summary>
    public Task Loaded
    {
        get
        {
            lock (_gate)
                return _loading;
        }
    }

    /// <summary>The module for a new agent. The module of the one before is stopped, and no user's servers are loaded until <see cref="UseUserAsync"/>.</summary>
    public LocalMcpModule NewModule()
    {
        LocalMcpModule? old;
        var module = new LocalMcpModule([]);
        module.Roots = CurrentRoots;
        module.Changed += OnModuleChanged;
        module.StatusChanged += OnStatusChanged;
        lock (_gate)
        {
            old = _module;
            _module = module;
            _userId = null;
            DropStore();
            _loading = Task.CompletedTask;
        }
        old?.Dispose();
        return module;
    }

    /// <summary>
    /// Loads the servers of <paramref name="userId"/> into the module (switched-on ones start, in the background, and join the
    /// agent at its next turn). Without a user (a server that names nobody) the list lives in memory. Does nothing for the user
    /// that is loaded already.
    /// </summary>
    public Task UseUserAsync(string? userId)
    {
        lock (_gate)
        {
            if (_disposed || _module is not { } module || (_store is not null && _userId == userId))
                return _loading;
            _userId = userId;
            DropStore();
            _store = userId is null ? McpServerStore.InMemory() : _openStore(userId);
            _audit = new McpAuditLog(_store.AuditFolder);
            _store.Changed += OnStoreChanged;
            var entries = _store.Entries;
            _loading = LoadAsync(module, entries);
            return _loading;
        }
    }

    /// <summary>
    /// Loads the standalone principal's servers (<see cref="McpServerStore.ForStandalone"/>): one list for each OS user, which the
    /// desktop and Office share. The same as <see cref="UseUserAsync"/> for the user <c>standalone</c>.
    /// </summary>
    public Task UseStandaloneAsync() => UseUserAsync(LocalAgentBackend.StandaloneId);

    private async Task LoadAsync(LocalMcpModule module, IReadOnlyList<McpServerEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                await module.AddAsync(_server(entry), entry.Enabled).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ArgumentException or ObjectDisposedException or InvalidOperationException)
            {
                // a label twice, or the module is gone: the row shows what the store has
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every server of the signed-in user, with its status.</summary>
    public IReadOnlyList<McpServerRow> Rows
    {
        get
        {
            McpServerStore? store;
            LocalMcpModule? module;
            lock (_gate)
            {
                store = _store;
                module = _module;
            }
            return store is null ? [] : [.. store.Entries.Select(e => new McpServerRow(e, module?.GetStatus(e.Label)))];
        }
    }

    /// <summary>Adds a server to the store and, when it is on, starts it. The problem when the entry is not valid.</summary>
    public async Task<McpValidation> AddAsync(McpServerEntry entry, CancellationToken cancellationToken = default)
    {
        var store = Store ?? throw new InvalidOperationException("No user is loaded.");
        var check = store.Add(entry);
        if (check.IsValid && Module is { } module)
            await module.AddAsync(_server(entry), entry.Enabled, cancellationToken).ConfigureAwait(false);
        return check;
    }

    /// <summary>Replaces the server <paramref name="label"/> (it is stopped and started again with the new entry).</summary>
    public async Task<McpValidation> UpdateAsync(string label, McpServerEntry entry, CancellationToken cancellationToken = default)
    {
        var store = Store ?? throw new InvalidOperationException("No user is loaded.");
        var check = store.Update(label, entry);
        if (check.IsValid && Module is { } module)
        {
            await module.RemoveAsync(label, cancellationToken).ConfigureAwait(false);
            await module.AddAsync(_server(entry), entry.Enabled, cancellationToken).ConfigureAwait(false);
        }
        return check;
    }

    public async Task RemoveAsync(string label, CancellationToken cancellationToken = default)
    {
        Store?.Remove(label);
        if (Module is { } module)
            await module.RemoveAsync(label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The switch: it holds from the next message (the module raises <c>Changed</c>, the agent asks again).</summary>
    public async Task SetEnabledAsync(string label, bool enabled, CancellationToken cancellationToken = default)
    {
        if (Store is not { } store || !store.SetEnabled(label, enabled))
            return;
        if (Module is { } module && module.GetStatus(label) is not null)
            await module.SetEnabledAsync(label, enabled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The device's audit log of the loaded list's switches (sdk/51 F05); null before a user is loaded.</summary>
    public McpAuditLog? AuditLog
    {
        get
        {
            lock (_gate)
                return _audit;
        }
    }

    /// <summary>
    /// The person's switch "Allow plain http on the local network" for an HTTP server (sdk/51 F05). Turning it on needs the
    /// version of the warning the person confirmed just before (<paramref name="warningVersion"/>); the app shows the warning, the
    /// SDK records which one. The server starts again with the switch; whether it connects is the profile's and this computer's
    /// policy's to say (its status tells). Every change goes into the device's audit log (<see cref="AuditLog"/>) and, in governed
    /// mode where the profile lists the feature, to the server as <c>local_mcp.plain_http</c>. A server that is sent a header value
    /// cannot have it (<see cref="McpProblem.PlainHttpWithHeaders"/>).
    /// </summary>
    public async Task<McpValidation> SetPlainHttpInLanAsync(string label, bool on, string? warningVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (on && string.IsNullOrWhiteSpace(warningVersion))
            throw new ArgumentException("Turning plain http on needs the version of the warning the person confirmed.", nameof(warningVersion));
        var store = Store ?? throw new InvalidOperationException("No user is loaded.");
        if (store.Find(label) is not { } entry)
            return new McpValidation(McpProblem.LabelMissing, "label");
        if (entry.IsProgram || !Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp)
            return new McpValidation(McpProblem.AddressInvalid, "address");
        if (entry.PlainHttpInLan == on)
            return McpValidation.Ok;
        var check = await UpdateAsync(label, entry with { PlainHttpInLan = on }, cancellationToken).ConfigureAwait(false);
        if (!check.IsValid)
            return check;
        var source = McpHttp.Origin(url);
        string person;
        McpAuditLog? audit;
        lock (_gate)
        {
            person = _userId ?? "";
            audit = _audit;
        }
        audit?.Append(new McpAuditEntry
        {
            Event = "local_mcp.plain_http",
            At = DateTimeOffset.UtcNow,
            Source = source,
            State = on ? "on" : "off",
            WarningVersion = on ? warningVersion : null,
            Person = person,
        });
        Module?.ReportPlainHttpChange(source, on);
        return check;
    }

    /// <summary>
    /// Marks a server trusted or not, as the person (standalone): its read-only hints count from then on, from the next turn.
    /// Not for a governed host, where the administrator's marks count (<c>marks</c>).
    /// </summary>
    public void SetTrusted(string label, bool trusted)
    {
        if (Store is { } store && store.SetTrusted(label, trusted))
            Reclassify(label);
    }

    /// <summary>The person's raise of one tool of a server to read or write (null takes it back), from the next turn.</summary>
    public void SetToolClass(string label, string tool, ToolClass? toolClass)
    {
        if (Store is { } store && store.SetToolClass(label, tool, toolClass))
            Reclassify(label);
    }

    private void Reclassify(string label)
    {
        if (Module is { } module && Store?.Find(label) is { } entry && _marks(entry) is { } marks)
        {
            // The module keeps the server it was given: the marks live on it, and the module offers the tools again.
            module.UpdateClasses(label, marks);
        }
    }

    /// <summary>Tells every running server that the readable folders changed.</summary>
    public void NotifyRootsChanged() => Module?.NotifyRootsChanged();

    /// <summary>The host's folders as the servers' roots.</summary>
    public IReadOnlyList<McpRoot> CurrentRoots() => McpRoots.From(_roots?.Folders() ?? []);

    /// <summary>
    /// Starts the server for a moment, lists its tools and stops it (the Tools page's Test). Only where the agent's policy allows
    /// local servers (<paramref name="allowed"/> false: nothing is started); the person's own click starts it, nothing else does.
    /// <paramref name="plainHttpPermitted"/>: this agent and computer allow plain http on the local network (the profile's
    /// <c>local_mcp.plain_http</c> and the machine policy); a refusal comes back as <see cref="McpTestResult.PlainHttp"/>.
    /// </summary>
    public async Task<McpTestResult> TestAsync(McpServerEntry entry, bool allowed, CancellationToken cancellationToken = default,
        bool plainHttpPermitted = false)
    {
        if (!allowed)
            return new McpTestResult(false, [], null, NotAllowed: true);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        McpClient? client = null;
        try
        {
            var transport = await _transport(entry, new McpConnection(plainHttpPermitted), limit.Token).ConfigureAwait(false);
            client = await McpClient.CreateAsync(transport, cancellationToken: limit.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: limit.Token).ConfigureAwait(false);
            return new McpTestResult(true, [.. tools.Select(t => t.Name)]);
        }
        catch (McpPlainHttpRefusedException e)
        {
            return new McpTestResult(false, [], e.Message) { PlainHttp = e.Reason };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new McpTestResult(false, [], "It did not answer in 30 seconds.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new McpTestResult(false, [], Clean(e.Message));
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The SDK's server for an entry, with the person's own trust marks.</summary>
    public static LocalMcpServer ToServer(McpServerEntry entry) => ToServer(entry, new McpServerMarks(entry.Trusted, entry.ToolClasses));

    /// <summary>The SDK's server for an entry: arguments and environment as lists, never a shell string. <paramref name="marks"/>: whose word counts for its tools' classes.</summary>
    public static LocalMcpServer ToServer(McpServerEntry entry, McpServerMarks? marks)
    {
        LocalMcpServer server;
        if (entry.IsProgram)
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var pair in entry.Environment)
                environment[pair.Name] = pair.Value;
            server = LocalMcpServer.Stdio(entry.Label, entry.Command!, entry.Arguments, environment.Count == 0 ? null : environment,
                string.IsNullOrWhiteSpace(entry.WorkingFolder) ? null : entry.WorkingFolder);
        }
        else
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in entry.Headers)
                headers[pair.Name] = pair.Value;
            server = LocalMcpServer.Http(entry.Label, new Uri(entry.Url!), null, headers.Count == 0 ? null : headers);
        }
        server.CallTimeout = TimeSpan.FromSeconds(entry.CallTimeoutSeconds);
        server.AllowPlainHttpInLan = entry.PlainHttpInLan;
        if (marks is not null)
        {
            server.Trusted = marks.Trusted;
            server.ToolClasses = marks.ToolClasses;
        }
        return server;
    }

    public void Dispose()
    {
        LocalMcpModule? module;
        lock (_gate)
        {
            _disposed = true;
            module = _module;
            _module = null;
            DropStore();
        }
        if (_roots is not null)
            _roots.Changed -= OnRootsChanged;
        module?.Dispose();
    }

    // Under the gate.
    private void DropStore()
    {
        if (_store is not null)
            _store.Changed -= OnStoreChanged;
        _store = null;
        _audit = null;
    }

    private static string Clean(string text)
    {
        var line = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
    }

    private void OnRootsChanged(object? sender, EventArgs e) => NotifyRootsChanged();

    private void OnModuleChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnStatusChanged(object? sender, McpServerStatusEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnStoreChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Whose word counts for a server's tools: the trust mark and the raises of single tools, by the server's own tool name. In
/// standalone mode they are the person's (the entry's); in governed mode the administrator's.
/// </summary>
public sealed record McpServerMarks(bool Trusted, IReadOnlyDictionary<string, ToolClass> ToolClasses);

/// <summary>
/// The seam between the host and where its folders come from (the desktop's readable folders, an Office add-in's workspace): the
/// folders now, and a "changed" event the source raises when they differ, which tells every running server.
/// </summary>
public sealed class McpRootsSource
{
    public McpRootsSource(Func<IReadOnlyList<string>> folders)
    {
        Folders = folders ?? throw new ArgumentNullException(nameof(folders));
    }

    /// <summary>The folders now.</summary>
    public Func<IReadOnlyList<string>> Folders { get; }

    /// <summary>The folders changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The owner of the folders says they changed.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>The readable folders as the roots servers are told (<c>file://</c> URIs).</summary>
public static class McpRoots
{
    public static IReadOnlyList<McpRoot> From(IEnumerable<string> folders)
    {
        var roots = new List<McpRoot>();
        foreach (var folder in folders)
        {
            try
            {
                var full = Path.GetFullPath(folder);
                var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                roots.Add(new McpRoot(new Uri(full).AbsoluteUri, name.Length > 0 ? name : null));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or UriFormatException or PathTooLongException)
            {
                // a path that is no folder name is no root
            }
        }
        return roots;
    }
}

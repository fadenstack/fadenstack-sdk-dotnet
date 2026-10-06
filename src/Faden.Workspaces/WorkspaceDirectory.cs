using System.Text;

namespace Faden.Workspaces;

/// <summary>What a reference resolved to: one workspace, or a message for the model (none, or which one?).</summary>
public sealed record WorkspaceResolution
{
    public WorkspaceInfo? Workspace { get; init; }

    /// <summary>When more than one fits: the model asks the user, with these.</summary>
    public IReadOnlyList<WorkspaceInfo> Candidates { get; init; } = [];

    public string Message { get; init; } = "";
}

/// <summary>
/// The applications an agent works with, their running instances, the workspaces open in them and the agents
/// active in each -- the one place tools and system tools ask. Instances get short handles (<c>word#1</c>,
/// <c>word#2</c>, in start order), stable while the directory lives and never reused, so the model can name an
/// instance and two instances of the same application are never confused.
/// </summary>
public sealed class WorkspaceDirectory
{
    private readonly Dictionary<string, IApplicationProvider> _providers;
    private readonly IProcessSource _processes;
    private readonly Dictionary<InstanceKey, string> _handles = new();
    private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceDirectory(IEnumerable<IApplicationProvider> providers, DeviceAgentRegistry? agents = null,
        DocumentLeases? leases = null, IProcessSource? processes = null)
    {
        _providers = providers.ToDictionary(p => p.Info.Id, StringComparer.OrdinalIgnoreCase);
        _processes = processes ?? SystemProcessSource.Instance;
        Agents = agents ?? new DeviceAgentRegistry(processes: _processes);
        Leases = leases ?? new DocumentLeases(processes: _processes);
    }

    public DeviceAgentRegistry Agents { get; }

    public DocumentLeases Leases { get; }

    public IProcessSource Processes => _processes;

    public IReadOnlyList<ApplicationInfo> Applications => _providers.Values.Select(p => p.Info).ToList();

    public IApplicationProvider? Provider(string app) => _providers.TryGetValue(app, out var p) ? p : null;

    /// <summary>Running instances of one application or of all, oldest first, with their agents.</summary>
    public IReadOnlyList<AppInstance> GetInstances(string? app = null)
    {
        var agents = Agents.List();
        var result = new List<AppInstance>();
        foreach (var provider in Selected(app))
        {
            var processes = provider.Info.ProcessNames.SelectMany(_processes.ByName).OrderBy(p => p.StartedUtc);
            foreach (var process in processes)
            {
                var key = InstanceKey.For(provider.Info.Id, process.Id, process.StartedUtc);
                var text = key.ToString();
                result.Add(new AppInstance
                {
                    Key = key,
                    Handle = HandleFor(key),
                    WindowTitle = process.WindowTitle,
                    Agents = agents.Where(a => a.Instance == text).ToList(),
                });
            }
        }
        return result;
    }

    /// <summary>The workspaces open in running instances, with leases and attached agents.</summary>
    public async Task<IReadOnlyList<WorkspaceInfo>> GetOpenWorkspacesAsync(string? app = null, CancellationToken cancellationToken = default)
    {
        var result = new List<WorkspaceInfo>();
        foreach (var instance in GetInstances(app))
        {
            var provider = _providers[instance.App];
            foreach (var open in await provider.GetOpenWorkspacesAsync(instance, cancellationToken).ConfigureAwait(false))
            {
                var reference = new WorkspaceRef(instance.App, WorkspaceRef.Normalize(open.Location), instance.Key);
                result.Add(new WorkspaceInfo
                {
                    Ref = reference,
                    InstanceHandle = instance.Handle,
                    Title = open.Title,
                    IsActive = open.IsActive,
                    IsDirty = open.IsDirty,
                    IsReadOnly = open.IsReadOnly,
                    Lease = Leases.Peek(reference),
                    Agents = instance.Agents
                        .Where(a => a.Workspaces.Any(w => reference.SameLocation(new WorkspaceRef(instance.App, w))))
                        .ToList(),
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the workspace a tool argument means. <paramref name="reference"/> may be empty (the active one), a
    /// name (<c>report.docx</c>, <c>report</c>), a path, a full reference (<c>word:C:\...\report.docx</c>), each
    /// optionally with an instance (<c>report.docx@word#2</c>). Several matches are never guessed between.
    /// </summary>
    public async Task<WorkspaceResolution> ResolveAsync(string app, string? reference, CancellationToken cancellationToken = default)
    {
        if (Provider(app) is null)
        {
            return new WorkspaceResolution { Message = $"No application '{app}' is set up for this agent." };
        }
        var (location, instanceHandle) = Split(app, reference);
        var open = await GetOpenWorkspacesAsync(app, cancellationToken).ConfigureAwait(false);
        if (instanceHandle is not null)
        {
            if (!open.Any(w => string.Equals(w.InstanceHandle, instanceHandle, StringComparison.OrdinalIgnoreCase))
                && !GetInstances(app).Any(i => string.Equals(i.Handle, instanceHandle, StringComparison.OrdinalIgnoreCase)))
            {
                return new WorkspaceResolution { Message = $"{instanceHandle} is not running. {Summary(open, app)}" };
            }
            open = open.Where(w => string.Equals(w.InstanceHandle, instanceHandle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        IReadOnlyList<WorkspaceInfo> matches;
        if (string.IsNullOrWhiteSpace(location))
        {
            matches = open.Where(w => w.IsActive).ToList();
            if (matches.Count == 0)
            {
                return new WorkspaceResolution { Message = $"No {Name(app)} document is open. {Summary(open, app)}" };
            }
        }
        else if (WorkspaceRef.LooksLikePath(location!) || location!.StartsWith("unsaved:", StringComparison.Ordinal))
        {
            var wanted = new WorkspaceRef(app, location!);
            matches = open.Where(w => w.Ref.SameLocation(wanted)).ToList();
        }
        else
        {
            matches = open.Where(w => NameMatches(w, location, exact: true)).ToList();
            if (matches.Count == 0)
            {
                matches = open.Where(w => NameMatches(w, location, exact: false)).ToList();
            }
        }

        if (matches.Count == 1)
        {
            return new WorkspaceResolution { Workspace = matches[0] };
        }
        if (matches.Count == 0)
        {
            return new WorkspaceResolution
            {
                Message = $"'{location}' is not open in {Name(app)}. Open it with open_workspace, or pick one: {Summary(open, app)}",
            };
        }
        return new WorkspaceResolution
        {
            Candidates = matches,
            Message = "More than one fits; ask the user which one, then pass it as the workspace argument: "
                + string.Join("; ", matches.Select(Describe)),
        };
    }

    /// <summary>The reference the model should pass back: location plus instance handle.</summary>
    public string Describe(WorkspaceInfo workspace) =>
        $"{workspace.Ref.App}:{workspace.Ref.Location}@{workspace.InstanceHandle}";

    internal AppInstance? InstanceFor(WorkspaceInfo workspace) =>
        workspace.Ref.Instance is { } key ? GetInstances(key.App).FirstOrDefault(i => i.Key == key) : null;

    internal string HandleFor(InstanceKey key)
    {
        lock (_handles)
        {
            if (!_handles.TryGetValue(key, out var handle))
            {
                _counters.TryGetValue(key.App, out var n);
                _counters[key.App] = ++n;
                _handles[key] = handle = $"{key.App}#{n}";
            }
            return handle;
        }
    }

    private IEnumerable<IApplicationProvider> Selected(string? app) =>
        string.IsNullOrEmpty(app) ? _providers.Values : Provider(app!) is { } p ? new[] { p } : Array.Empty<IApplicationProvider>();

    private string Name(string app) => Provider(app)?.Info.Name ?? app;

    private static bool NameMatches(WorkspaceInfo w, string name, bool exact)
    {
        var title = w.Ref.Name;
        var bare = Path.GetFileNameWithoutExtension(title);
        return exact
            ? string.Equals(title, name, StringComparison.OrdinalIgnoreCase) || string.Equals(bare, name, StringComparison.OrdinalIgnoreCase)
            : title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // "word:C:\x.docx@word#2" -> ("C:\x.docx", "word#2"). The instance suffix is only taken when it is a handle,
    // so an '@' in a path stays part of the path.
    private static (string? Location, string? Instance) Split(string app, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, null);
        }
        var text = reference!.Trim();
        string? instance = null;
        var at = text.LastIndexOf('@');
        if (at >= 0 && IsHandle(text.Substring(at + 1)))
        {
            instance = text.Substring(at + 1);
            text = text.Substring(0, at);
        }
        if (text.StartsWith(app + ":", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(app.Length + 1);
        }
        return (text.Length == 0 ? null : text, instance);
    }

    private static bool IsHandle(string text)
    {
        var hash = text.IndexOf('#');
        return hash > 0 && hash < text.Length - 1 && text.Substring(hash + 1).All(char.IsDigit)
            && text.Substring(0, hash).All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    private string Summary(IReadOnlyList<WorkspaceInfo> open, string app)
    {
        if (open.Count == 0)
        {
            return $"Nothing is open in {Name(app)}.";
        }
        var text = new StringBuilder("Open: ");
        text.Append(string.Join("; ", open.Select(Describe)));
        return text.ToString();
    }
}

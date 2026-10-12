namespace Faden.Workspaces;

/// <summary>A workspace as a provider reports it for one instance.</summary>
public sealed record OpenWorkspace(string Location, string Title, bool IsActive, bool? IsDirty = null, bool IsReadOnly = false);

/// <summary>
/// One application the framework works with, implemented once per application (Word, PowerPoint, TIA Portal)
/// and per kind of binding (in-process inside the add-in, automation from outside, file only). The
/// framework does the rest: instances, handles, agents, resolving what the model means, leases, threading.
/// </summary>
public interface IApplicationProvider
{
    ApplicationInfo Info { get; }

    /// <summary>The workspaces open in a running instance; none when the provider cannot see inside it.</summary>
    Task<IReadOnlyList<OpenWorkspace>> GetOpenWorkspacesAsync(AppInstance instance, CancellationToken cancellationToken);

    /// <summary>What tools work on: the application's object for the workspace, and the thread to use it on.</summary>
    Task<IWorkspaceBinding> BindAsync(WorkspaceInfo workspace, CancellationToken cancellationToken);
}

/// <summary>A provider that can also open, create and save workspaces (automation and file bindings).</summary>
public interface IWorkspaceLifecycle
{
    /// <summary>Opens a file: in <paramref name="instance"/>, in a running instance, or by starting the application.</summary>
    Task<OpenWorkspace> OpenAsync(string location, AppInstance? instance, CancellationToken cancellationToken);

    /// <summary>A new, unsaved workspace, optionally from a template.</summary>
    Task<OpenWorkspace> CreateAsync(string? template, AppInstance? instance, CancellationToken cancellationToken);

    /// <summary>Saves, or saves as <paramref name="location"/>.</summary>
    Task<OpenWorkspace> SaveAsync(WorkspaceInfo workspace, string? location, CancellationToken cancellationToken);
}

/// <summary>
/// A tool's access to one workspace. <see cref="RunAsync{T}"/> runs work on the thread the application's
/// objects belong to (Office's main thread in an add-in, a single-threaded apartment for COM automation).
/// </summary>
public interface IWorkspaceBinding : IDisposable
{
    WorkspaceInfo Workspace { get; }

    Task<T> RunAsync<T>(Func<object, T> work, CancellationToken cancellationToken);
}

/// <summary>
/// For applications the framework can only see as processes (no automation): instances, the agents in them,
/// nothing inside. Enough for <c>find_processes</c> and for telling instances apart.
/// </summary>
public sealed class ProcessOnlyProvider : IApplicationProvider
{
    public ProcessOnlyProvider(ApplicationInfo info)
    {
        Info = info;
    }

    public ApplicationInfo Info { get; }

    public Task<IReadOnlyList<OpenWorkspace>> GetOpenWorkspacesAsync(AppInstance instance, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenWorkspace>>([]);

    public Task<IWorkspaceBinding> BindAsync(WorkspaceInfo workspace, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Info.Name} has no automation the agent can use.");
}

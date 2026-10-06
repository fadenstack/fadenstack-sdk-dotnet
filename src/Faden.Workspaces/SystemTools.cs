using System.ComponentModel;
using System.Globalization;
using System.Text;
using Faden.Agents;
using Faden.Tools;

namespace Faden.Workspaces;

/// <summary>
/// The default system tools of an agent that works with applications: what runs, what is open in it, which
/// agents are active where, and opening, creating and saving workspaces. Answers are short text the model
/// reads; references in them are what tools take as their <c>workspace</c> argument.
/// <c>AgentBuilder.AddTools(new SystemTools(directory))</c>.
/// </summary>
public sealed class SystemTools
{
    private readonly WorkspaceDirectory _directory;

    public SystemTools(WorkspaceDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    [AgentTool(ToolClass.Read)]
    [Description("Running instances of the applications this agent works with, one per line: the instance handle " +
        "(word#2), process id, start time, window title, and the Fadenstack agents active in it. Several instances " +
        "of one application are told apart by their handle.")]
    public string ListRunningInstances([Description("Application id (word, excel, tia, ...); empty for all")] string? app = null)
    {
        var instances = _directory.GetInstances(Blank(app));
        if (instances.Count == 0)
        {
            return string.IsNullOrWhiteSpace(app) ? "None of the applications is running." : $"{AppName(app!)} is not running.";
        }
        return string.Join("\n", instances.Select(i =>
            $"{i.Handle}: process {i.ProcessId}, started {Time(i.StartedUtc)}{Title(i.WindowTitle)}; {Agents(i.Agents)}"));
    }

    [AgentTool(ToolClass.Read)]
    [Description("Documents, projects or files open in the running applications, one per line: the reference to " +
        "pass as a tool's 'workspace' argument, whether it is the active one, unsaved changes, read-only, the agent " +
        "working on it and who is changing it right now.")]
    public async Task<string> ListOpenWorkspacesAsync([Description("Application id; empty for all")] string? app = null,
        CancellationToken cancellationToken = default)
    {
        var open = await _directory.GetOpenWorkspacesAsync(Blank(app), cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return "Nothing is open.";
        }
        return string.Join("\n", open.Select(w =>
        {
            var line = new StringBuilder(_directory.Describe(w));
            if (w.IsActive)
            {
                line.Append(", active");
            }
            if (w.IsDirty == true)
            {
                line.Append(", unsaved changes");
            }
            if (w.IsReadOnly)
            {
                line.Append(", read-only");
            }
            if (w.Agents.Count > 0)
            {
                line.Append("; ").Append(Agents(w.Agents));
            }
            if (w.Lease is { } lease)
            {
                line.Append($"; being changed by {lease.Holder}");
            }
            return line.ToString();
        }));
    }

    [AgentTool(ToolClass.Read)]
    [Description("Processes of the user's session with this program name (without .exe), one per line: process id, " +
        "start time, window title, and any Fadenstack agent registered in that process or working in it.")]
    public string FindProcesses([Description("Program name, e.g. WINWORD or notepad")] string name)
    {
        var processes = _directory.Processes.ByName(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
        if (processes.Count == 0)
        {
            return $"No process named {name} is running in this session.";
        }
        var agents = _directory.Agents.List();
        return string.Join("\n", processes.Select(p =>
        {
            var inIt = agents.Where(a =>
                (a.HostProcessId == p.Id && a.HostStartTicks == p.StartTicks)
                || (InstanceKey.TryParse(a.Instance, out var key) && key.ProcessId == p.Id && key.StartTicks == p.StartTicks)).ToList();
            return $"process {p.Id} ({p.Name}), started {Time(p.StartedUtc)}{Title(p.WindowTitle)}; {Agents(inIt)}";
        }));
    }

    [AgentTool(ToolClass.Write)]
    [Description("Opens a file in an application: in the given instance, in a running one, or by starting it. " +
        "Returns the reference to use as 'workspace'.")]
    public async Task<string> OpenWorkspaceAsync(
        [Description("Application id")] string app,
        [Description("Full path of the file")] string location,
        [Description("Instance handle (word#2); empty for any")] string? instance = null,
        CancellationToken cancellationToken = default)
    {
        if (Lifecycle(app, out var error) is not { } lifecycle)
        {
            return error;
        }
        var opened = await lifecycle.OpenAsync(location, Instance(app, instance), cancellationToken).ConfigureAwait(false);
        return await ReferenceAsync(app, opened.Location, cancellationToken).ConfigureAwait(false);
    }

    [AgentTool(ToolClass.Write)]
    [Description("Creates a new document, presentation or project, optionally from a template. It stays unsaved " +
        "until save_workspace. Returns the reference to use as 'workspace'.")]
    public async Task<string> CreateWorkspaceAsync(
        [Description("Application id")] string app,
        [Description("Template path (.dotx, .potx, ...); empty for a blank one")] string? template = null,
        [Description("Instance handle; empty for any")] string? instance = null,
        CancellationToken cancellationToken = default)
    {
        if (Lifecycle(app, out var error) is not { } lifecycle)
        {
            return error;
        }
        var created = await lifecycle.CreateAsync(Blank(template), Instance(app, instance), cancellationToken).ConfigureAwait(false);
        return await ReferenceAsync(app, created.Location, cancellationToken).ConfigureAwait(false);
    }

    [AgentTool(ToolClass.Write)]
    [Description("Saves a workspace, or saves it under a new path. Never overwrites another existing file.")]
    public async Task<string> SaveWorkspaceAsync(
        [Description("Application id")] string app,
        [Description("The workspace reference; empty for the active one")] string? workspace = null,
        [Description("New full path to save as; empty to save in place")] string? saveAs = null,
        CancellationToken cancellationToken = default)
    {
        if (Lifecycle(app, out var error) is not { } lifecycle)
        {
            return error;
        }
        var resolution = await _directory.ResolveAsync(app, workspace, cancellationToken).ConfigureAwait(false);
        if (resolution.Workspace is not { } target)
        {
            return resolution.Message;
        }
        if (Blank(saveAs) is { } path && File.Exists(path) && !target.Ref.SameLocation(new WorkspaceRef(app, path)))
        {
            return $"{path} already exists. Choose another name, or ask the user whether to replace it.";
        }
        using var lease = _directory.Leases.TryAcquire(target.Ref, "save_workspace", TimeSpan.FromMinutes(1));
        if (lease is null)
        {
            return $"{target.Title} is being changed by {_directory.Leases.Peek(target.Ref)?.Holder ?? "another agent"}; try again shortly.";
        }
        var saved = await lifecycle.SaveAsync(target, Blank(saveAs), cancellationToken).ConfigureAwait(false);
        return $"Saved: {saved.Location}";
    }

    private IWorkspaceLifecycle? Lifecycle(string app, out string error)
    {
        error = "";
        if (_directory.Provider(app) is not { } provider)
        {
            error = $"No application '{app}' is set up for this agent.";
            return null;
        }
        if (provider is not IWorkspaceLifecycle lifecycle)
        {
            error = $"{provider.Info.Name} cannot open, create or save files from here.";
            return null;
        }
        return lifecycle;
    }

    private AppInstance? Instance(string app, string? handle) =>
        Blank(handle) is { } h ? _directory.GetInstances(app).FirstOrDefault(i => string.Equals(i.Handle, h, StringComparison.OrdinalIgnoreCase)) : null;

    private async Task<string> ReferenceAsync(string app, string location, CancellationToken cancellationToken)
    {
        var resolution = await _directory.ResolveAsync(app, location, cancellationToken).ConfigureAwait(false);
        return resolution.Workspace is { } w ? $"Open as {_directory.Describe(w)}" : $"Done: {location}. {resolution.Message}";
    }

    private string AppName(string app) => _directory.Provider(app)?.Info.Name ?? app;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();

    private static string Time(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Title(string? title) => title is null ? "" : $", window \"{title}\"";

    private static string Agents(IReadOnlyList<AgentPresence> agents)
    {
        if (agents.Count == 0)
        {
            return "no Fadenstack agent";
        }
        return "agents: " + string.Join(", ", agents.Select(a =>
        {
            var details = new List<string> { a.Status == AgentStatus.Busy ? "busy" : "idle" };
            if (a.Workspaces.Count > 0)
            {
                details.Add("on " + string.Join(", ", a.Workspaces.Select(w => new WorkspaceRef("", w).Name)));
            }
            if (a.Bridge is not null)
            {
                details.Add("accepts tasks");
            }
            return $"{a.Agent} ({string.Join(", ", details)})";
        }));
    }
}

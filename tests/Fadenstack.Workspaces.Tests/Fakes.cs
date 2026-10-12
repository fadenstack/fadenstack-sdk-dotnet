using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Fadenstack.Agents;
using Fadenstack.Tools;

namespace Fadenstack.Workspaces.Tests;

/// <summary>Processes under the test's control. Process 1 is "us".</summary>
internal sealed class FakeProcesses : IProcessSource
{
    private readonly List<ProcessSnapshot> _processes = new();

    public FakeProcesses()
    {
        Current = Add("testhost", new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc), id: 1);
    }

    public int CurrentSessionId => 1;

    public ProcessSnapshot Current { get; set; }

    public ProcessSnapshot Add(string name, DateTime startedUtc, int id, string? title = null)
    {
        var snapshot = new ProcessSnapshot(id, name, startedUtc, 1, title);
        _processes.Add(snapshot);
        return snapshot;
    }

    public void Kill(int id) => _processes.RemoveAll(p => p.Id == id);

    public IReadOnlyList<ProcessSnapshot> ByName(string name) =>
        _processes.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.StartedUtc).ToList();

    public ProcessSnapshot? ById(int id) => _processes.FirstOrDefault(p => p.Id == id);
}

/// <summary>A document of the fake Word: text and the thread it was last touched on.</summary>
internal sealed class FakeDocument
{
    public FakeDocument(string location, bool active = false)
    {
        Location = location;
        Active = active;
    }

    public string Location { get; set; }

    public bool Active { get; set; }

    public bool Dirty { get; set; }

    public StringBuilder Text { get; } = new();

    public int? LastThread { get; set; }
}

/// <summary>A Word-like application: documents per process, objects owned by one thread (like COM).</summary>
internal sealed class FakeWord : IApplicationProvider, IWorkspaceLifecycle, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _unsaved;

    public FakeWord()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }) { IsBackground = true, Name = "fake-word" };
        _thread.Start();
    }

    public ApplicationInfo Info { get; } = new()
    {
        Id = "word", Name = "Word", ProcessNames = ["WINWORD"], FileTypes = [".docx"], MultiInstance = true,
    };

    public int ThreadId => _thread.ManagedThreadId;

    public Dictionary<int, List<FakeDocument>> Documents { get; } = new();

    public FakeDocument Open(int processId, string location, bool active = false)
    {
        if (!Documents.TryGetValue(processId, out var documents))
        {
            Documents[processId] = documents = new List<FakeDocument>();
        }
        var document = new FakeDocument(location, active);
        documents.Add(document);
        return document;
    }

    public Task<IReadOnlyList<OpenWorkspace>> GetOpenWorkspacesAsync(AppInstance instance, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenWorkspace>>(Documents.TryGetValue(instance.ProcessId, out var documents)
            ? documents.Select(d => new OpenWorkspace(d.Location, new WorkspaceRef("word", d.Location).Name, d.Active, d.Dirty)).ToList()
            : []);

    public Task<IWorkspaceBinding> BindAsync(WorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        var document = Documents[workspace.Ref.Instance!.Value.ProcessId].Single(d => workspace.Ref.SameLocation(new WorkspaceRef("word", d.Location)));
        return Task.FromResult<IWorkspaceBinding>(new Binding(this, workspace, document));
    }

    public Task<OpenWorkspace> OpenAsync(string location, AppInstance? instance, CancellationToken cancellationToken)
    {
        var pid = instance?.ProcessId ?? Documents.Keys.First();
        var document = Open(pid, location);
        return Task.FromResult(new OpenWorkspace(document.Location, new WorkspaceRef("word", location).Name, false));
    }

    public Task<OpenWorkspace> CreateAsync(string? template, AppInstance? instance, CancellationToken cancellationToken)
    {
        var pid = instance?.ProcessId ?? Documents.Keys.First();
        var document = Open(pid, $"unsaved:Document{++_unsaved}");
        return Task.FromResult(new OpenWorkspace(document.Location, document.Location.Substring(8), false));
    }

    public Task<OpenWorkspace> SaveAsync(WorkspaceInfo workspace, string? location, CancellationToken cancellationToken)
    {
        var document = Documents[workspace.Ref.Instance!.Value.ProcessId].Single(d => workspace.Ref.SameLocation(new WorkspaceRef("word", d.Location)));
        document.Location = location ?? document.Location;
        document.Dirty = false;
        return Task.FromResult(new OpenWorkspace(document.Location, new WorkspaceRef("word", document.Location).Name, document.Active));
    }

    public void Dispose() => _queue.CompleteAdding();

    private sealed class Binding(FakeWord word, WorkspaceInfo workspace, FakeDocument document) : IWorkspaceBinding
    {
        public WorkspaceInfo Workspace => workspace;

        public Task<T> RunAsync<T>(Func<object, T> work, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            word._queue.Add(() =>
            {
                try
                {
                    document.LastThread = Environment.CurrentManagedThreadId;
                    done.SetResult(work(document));
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }, cancellationToken);
            return done.Task;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Tools as an integrator writes them: only the work, no resolving, leasing or threading.</summary>
internal sealed class FakeWordTools
{
    [AgentTool(ToolClass.Read)]
    [Description("Reads the document's text.")]
    public Task<string> ReadTextAsync(Workspace<FakeDocument> document, CancellationToken cancellationToken = default) =>
        document.RunAsync(d => d.Text.ToString(), cancellationToken);

    [AgentTool(ToolClass.Write)]
    [Description("Appends text.")]
    public Task<string> AppendTextAsync(Workspace<FakeDocument> document, [Description("The text")] string text,
        CancellationToken cancellationToken = default) =>
        document.RunAsync(d =>
        {
            d.Text.Append(text);
            d.Dirty = true;
            return $"Appended to {document.Info.Title}.";
        }, cancellationToken);

    [AgentTool(ToolClass.Read)]
    [Description("The tool set's version.")]
    public string ToolsVersion() => "1";
}

/// <summary>A directory over fake processes, a fake Word and temporary registry and lease folders.</summary>
internal sealed class Fixture : IDisposable
{
    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "faden-ws-" + Guid.NewGuid().ToString("N"));
        Registry = new DeviceAgentRegistry(Path.Combine(Root, "agents"), Processes);
        Leases = new DocumentLeases(Path.Combine(Root, "leases"), Processes);
        Directory = new WorkspaceDirectory([Word], Registry, Leases, Processes);
    }

    public string Root { get; }

    public FakeProcesses Processes { get; } = new();

    public FakeWord Word { get; } = new();

    public DeviceAgentRegistry Registry { get; }

    public DocumentLeases Leases { get; }

    public WorkspaceDirectory Directory { get; }

    public static DateTime At(int hour, int minute = 0) => new(2026, 10, 3, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>Two Word instances: #1 (pid 100) with report.docx active, #2 (pid 200) with notes.docx active and report.docx.</summary>
    public void TwoWords()
    {
        Processes.Add("WINWORD", At(9), 100, "report.docx - Word");
        Processes.Add("WINWORD", At(10), 200, "notes.docx - Word");
        Word.Open(100, @"C:\Docs\report.docx", active: true);
        Word.Open(200, @"C:\Docs\notes.docx", active: true);
        Word.Open(200, @"C:\Docs\report.docx");
    }

    public InstanceKey KeyOf(int pid) => InstanceKey.For("word", pid, Processes.ById(pid)!.StartedUtc);

    public void Dispose()
    {
        Word.Dispose();
        try
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

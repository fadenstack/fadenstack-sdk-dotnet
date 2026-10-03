using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Faden.Workspaces.Tests;

public class IdentityTests
{
    [Fact]
    public void Instance_keys_round_trip_and_differ_by_start_time()
    {
        var first = InstanceKey.For("word", 4242, Fixture.At(9));
        var reused = InstanceKey.For("word", 4242, Fixture.At(11)); // same pid, a later process
        Assert.True(InstanceKey.TryParse(first.ToString(), out var parsed));
        Assert.Equal(first, parsed);
        Assert.NotEqual(first, reused);
    }

    [Fact]
    public void Workspace_locations_compare_as_paths()
    {
        var a = new WorkspaceRef("word", @"C:\Docs\Report.docx");
        Assert.True(a.SameLocation(new WorkspaceRef("WORD", @"c:\docs\sub\..\report.docx")));
        Assert.False(a.SameLocation(new WorkspaceRef("excel", @"C:\Docs\Report.docx")));
        Assert.Equal("Document3", new WorkspaceRef("word", "unsaved:Document3").Name);
    }
}

public class DirectoryTests
{
    [Fact]
    public void Instances_get_handles_in_start_order_and_only_their_own_agents()
    {
        using var f = new Fixture();
        f.TwoWords();
        using var agent = f.Registry.Register(new AgentPresence { Agent = "word-assistant", Instance = f.KeyOf(200).ToString(), Workspaces = [@"C:\Docs\notes.docx"] }, autoHeartbeat: false);

        var instances = f.Directory.GetInstances("word");

        Assert.Equal(["word#1", "word#2"], instances.Select(i => i.Handle));
        Assert.Equal([100, 200], instances.Select(i => i.ProcessId));
        Assert.Empty(instances[0].Agents);
        Assert.Equal("word-assistant", Assert.Single(instances[1].Agents).Agent);
    }

    [Fact]
    public void Handles_stay_with_their_instance_and_are_never_reused()
    {
        using var f = new Fixture();
        f.TwoWords();
        f.Directory.GetInstances();
        f.Processes.Kill(100);
        f.Processes.Add("WINWORD", Fixture.At(12), 300);

        var instances = f.Directory.GetInstances("word");

        Assert.Equal(["word#2", "word#3"], instances.Select(i => i.Handle));
    }

    [Fact]
    public void An_agent_of_a_restarted_process_is_not_reported()
    {
        using var f = new Fixture();
        f.TwoWords();
        using var agent = f.Registry.Register(new AgentPresence { Agent = "word-assistant", Instance = f.KeyOf(200).ToString() }, autoHeartbeat: false);
        Assert.Single(f.Registry.List());

        // Our process (the agent's host) dies and the id comes back as another process.
        f.Processes.Kill(1);
        f.Processes.Add("testhost", Fixture.At(13), 1);

        Assert.Empty(f.Registry.List());
    }

    [Fact]
    public async Task The_active_document_is_ambiguous_with_two_instances()
    {
        using var f = new Fixture();
        f.TwoWords();

        var resolution = await f.Directory.ResolveAsync("word", null);

        Assert.Null(resolution.Workspace);
        Assert.Equal(2, resolution.Candidates.Count);
        Assert.Contains(@"word:C:\Docs\report.docx@word#1", resolution.Message);
        Assert.Contains(@"word:C:\Docs\notes.docx@word#2", resolution.Message);
    }

    [Fact]
    public async Task A_file_open_in_two_instances_asks_and_the_handle_decides()
    {
        using var f = new Fixture();
        f.TwoWords();

        var ambiguous = await f.Directory.ResolveAsync("word", "report");
        var picked = await f.Directory.ResolveAsync("word", "report.docx@word#2");
        var byPath = await f.Directory.ResolveAsync("word", @"word:c:\docs\report.docx@word#1");

        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Equal(200, picked.Workspace!.Ref.Instance!.Value.ProcessId);
        Assert.Equal(100, byPath.Workspace!.Ref.Instance!.Value.ProcessId);
    }

    [Fact]
    public async Task Unknown_names_and_instances_say_what_is_open()
    {
        using var f = new Fixture();
        f.TwoWords();

        var missing = await f.Directory.ResolveAsync("word", "budget.docx");
        var gone = await f.Directory.ResolveAsync("word", "report.docx@word#7");

        Assert.Contains("'budget.docx' is not open in Word", missing.Message);
        Assert.Contains("word#7 is not running", gone.Message);
    }
}

public class LeaseTests
{
    [Fact]
    public void A_second_holder_waits_until_release()
    {
        using var f = new Fixture();
        var doc = new WorkspaceRef("word", @"C:\Docs\report.docx");

        var first = f.Leases.TryAcquire(doc, "word-assistant", TimeSpan.FromMinutes(1));
        var second = f.Leases.TryAcquire(doc, "desktop-assistant", TimeSpan.FromMinutes(1));
        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("word-assistant", f.Leases.Peek(doc)!.Holder);

        first!.Dispose();
        using var third = f.Leases.TryAcquire(doc, "desktop-assistant", TimeSpan.FromMinutes(1));
        Assert.NotNull(third);
    }

    [Fact]
    public void A_lease_of_an_ended_process_is_taken_over()
    {
        using var f = new Fixture();
        var doc = new WorkspaceRef("word", @"C:\Docs\report.docx");
        var orphan = f.Leases.TryAcquire(doc, "word-assistant", TimeSpan.FromMinutes(5));
        Assert.NotNull(orphan);

        f.Processes.Kill(1); // the holder ended; we are a new process that got the same id
        f.Processes.Current = f.Processes.Add("testhost", Fixture.At(14), 1);
        var other = new DocumentLeases(Path.Combine(f.Root, "leases"), f.Processes);

        using var taken = other.TryAcquire(doc, "desktop-assistant", TimeSpan.FromMinutes(1));
        Assert.NotNull(taken);
        orphan!.Dispose(); // must not remove the new holder's lease
        Assert.Equal("desktop-assistant", other.Peek(doc)!.Holder);
    }

    [Fact]
    public void An_expired_lease_is_taken_over()
    {
        using var f = new Fixture();
        var doc = new WorkspaceRef("word", @"C:\Docs\report.docx");
        using var expired = f.Leases.TryAcquire(doc, "word-assistant", TimeSpan.FromMilliseconds(-1));

        using var fresh = f.Leases.TryAcquire(doc, "desktop-assistant", TimeSpan.FromMinutes(1));

        Assert.NotNull(fresh);
    }
}

public class WorkspaceToolTests
{
    private static AIFunction Tool(Fixture f, string name, WorkspaceToolOptions? options = null) =>
        WorkspaceTools.From(new FakeWordTools(), "word", f.Directory, options).Single(t => t.Name == name).Function;

    [Fact]
    public void The_workspace_parameter_becomes_an_optional_first_argument()
    {
        using var f = new Fixture();

        var schema = Tool(f, "append_text").JsonSchema;
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal("workspace", properties[0]);
        Assert.Contains("text", properties);
        Assert.DoesNotContain("document", properties);
        var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()).ToList() : [];
        Assert.DoesNotContain("workspace", required);
        Assert.Equal("tools_version", WorkspaceTools.From(new FakeWordTools(), "word", f.Directory).Last().Name);
    }

    [Fact]
    public async Task A_read_runs_on_the_application_thread_without_a_lease()
    {
        using var f = new Fixture();
        f.TwoWords();
        var report = f.Word.Documents[200].Single(d => d.Location.EndsWith("report.docx"));
        report.Text.Append("Q3 revenue");
        using var held = f.Leases.TryAcquire(new WorkspaceRef("word", report.Location), "someone", TimeSpan.FromMinutes(1));

        var result = await Tool(f, "read_text").InvokeAsync(new AIFunctionArguments { ["workspace"] = "report.docx@word#2" });

        Assert.Equal("Q3 revenue", Text(result));
        Assert.Equal(f.Word.ThreadId, report.LastThread);
    }

    [Fact]
    public async Task A_write_takes_the_lease_and_gives_it_back()
    {
        using var f = new Fixture();
        f.TwoWords();
        var doc = new WorkspaceRef("word", @"C:\Docs\notes.docx");

        var result = await Tool(f, "append_text", new WorkspaceToolOptions { Holder = "desktop-assistant" })
            .InvokeAsync(new AIFunctionArguments { ["workspace"] = JsonDocument.Parse("\"notes\"").RootElement, ["text"] = "hello" });

        Assert.Equal("Appended to notes.docx.", Text(result));
        Assert.Equal("hello", f.Word.Documents[200][0].Text.ToString());
        Assert.Null(f.Leases.Peek(doc));
    }

    [Fact]
    public async Task A_write_reports_another_agents_lease_and_does_nothing()
    {
        using var f = new Fixture();
        f.TwoWords();
        using var held = f.Leases.TryAcquire(new WorkspaceRef("word", @"C:\Docs\notes.docx"), "word-assistant", TimeSpan.FromMinutes(1));

        var result = await Tool(f, "append_text", new WorkspaceToolOptions { LeaseWait = TimeSpan.FromMilliseconds(300) })
            .InvokeAsync(new AIFunctionArguments { ["workspace"] = "notes.docx", ["text"] = "hello" });

        Assert.Contains("being changed by word-assistant", Text(result));
        Assert.Equal("", f.Word.Documents[200][0].Text.ToString());
    }

    [Fact]
    public async Task An_ambiguous_workspace_asks_back_and_does_nothing()
    {
        using var f = new Fixture();
        f.TwoWords();

        var result = await Tool(f, "append_text").InvokeAsync(new AIFunctionArguments { ["workspace"] = "report", ["text"] = "x" });

        Assert.Contains("ask the user which one", Text(result));
        Assert.All(f.Word.Documents.Values.SelectMany(d => d), d => Assert.Equal("", d.Text.ToString()));
    }

    private static string Text(object? result) => result is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString()! : result?.ToString() ?? "";
}

public class SystemToolsTests
{
    [Fact]
    public async Task Running_instances_and_open_workspaces_show_who_works_where()
    {
        using var f = new Fixture();
        f.TwoWords();
        using var agent = f.Registry.Register(new AgentPresence
        {
            Agent = "word-assistant", Instance = f.KeyOf(200).ToString(), Workspaces = [@"C:\Docs\report.docx"], Status = AgentStatus.Busy,
        }, autoHeartbeat: false);
        using var lease = f.Leases.TryAcquire(new WorkspaceRef("word", @"C:\Docs\report.docx"), "word-assistant", TimeSpan.FromMinutes(1));
        var tools = new SystemTools(f.Directory);

        var instances = tools.ListRunningInstances("word").Split('\n');
        var open = (await tools.ListOpenWorkspacesAsync("word")).Split('\n');

        Assert.StartsWith("word#1: process 100", instances[0]);
        Assert.EndsWith("no Fadenstack agent", instances[0]);
        Assert.Contains("agents: word-assistant (busy, on report.docx)", instances[1]);
        Assert.Contains(@"word:C:\Docs\report.docx@word#1, active; being changed by word-assistant", open);
        Assert.Contains(@"word:C:\Docs\report.docx@word#2; agents: word-assistant (busy, on report.docx); being changed by word-assistant", open);
    }

    [Fact]
    public async Task Open_create_and_save_go_through_the_provider()
    {
        using var f = new Fixture();
        f.TwoWords();
        var tools = new SystemTools(f.Directory);
        var existing = Path.Combine(f.Root, "taken.docx");
        File.WriteAllText(existing, "");

        var opened = await tools.OpenWorkspaceAsync("word", @"C:\Docs\budget.docx", "word#2");
        var created = await tools.CreateWorkspaceAsync("word", instance: "word#1");
        var refused = await tools.SaveWorkspaceAsync("word", "Document1@word#1", existing);
        var saved = await tools.SaveWorkspaceAsync("word", "Document1@word#1", Path.Combine(f.Root, "deck-notes.docx"));

        Assert.Equal(@"Open as word:C:\Docs\budget.docx@word#2", opened);
        Assert.Equal("Open as word:unsaved:Document1@word#1", created);
        Assert.Contains("already exists", refused);
        Assert.StartsWith("Saved: ", saved);
    }
}

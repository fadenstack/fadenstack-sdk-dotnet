using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// A local server's program is pinned (sdk/51 5.5): path and SHA-256 of the file its command names, and of a script among the
/// arguments. Before every start it is hashed again; a changed, moved or missing file does not start, says so with the server's
/// label, and waits for "Use the new program". ASVS 5.0 V15 (integrity of what is run).
/// </summary>
public sealed class McpProgramPinTests : IDisposable
{
    private const string Allow = """{"mode":"allow","developer_mode":false}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcppin-" + Guid.NewGuid().ToString("N"));

    public McpProgramPinTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string File1(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
    }

    private McpServerStore Store() => new(Path.Combine(_root, "user"), new Reversing());

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    private static McpServerEntry Program(string label, string command, params string[] arguments) =>
        new() { Label = label, Command = command, Arguments = arguments };

    private static LocalMcpServer ProgramServer(McpRig rig, McpServerEntry e) =>
        LocalMcpServer.ProgramWith(e.Label, e.Command!, e.Arguments, e.WorkingFolder, _ => Task.FromResult(rig.Transport()));

    private IReadOnlyList<(string Event, string Source, string State)> Audit() =>
        File.Exists(Path.Combine(_root, "user", McpAuditLog.FileName))
            ? [.. File.ReadAllLines(Path.Combine(_root, "user", McpAuditLog.FileName)).Select(l =>
            {
                using var json = JsonDocument.Parse(l);
                var r = json.RootElement;
                return (r.GetProperty("event").GetString()!, r.GetProperty("source").GetString()!, r.GetProperty("state").GetString()!);
            })]
            : [];

    // ---- the pin ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pin_holds_the_full_path_and_hash_of_the_program_and_of_a_script_among_the_arguments()
    {
        var program = File1("bin/runner.exe", "runner v1");
        var script = File1("app/server.py", "print('v1')");

        var pin = McpProgramPinner.Pin(Program("notes", program, script, "--flag"))!;

        Assert.Equal((Path.GetFullPath(program), Sha(program)), (pin.Path, pin.Sha256));
        Assert.Equal((Path.GetFullPath(script), Sha(script)), (pin.ScriptPath, pin.ScriptSha256));
        Assert.Matches("^[0-9a-f]{64}$", pin.Sha256);
        Assert.True(pin.PinnedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void A_script_is_looked_for_in_the_working_folder_and_an_argument_that_is_no_file_is_no_script()
    {
        var program = File1("bin/runner.exe", "runner");
        var script = File1("work/index.js", "js");
        var entry = Program("notes", program, "index.js", "--port=1") with { WorkingFolder = Path.Combine(_root, "work") };

        var pin = McpProgramPinner.Pin(entry)!;
        var package = McpProgramPinner.Pin(Program("pkg", program, "@scope/server", "--x"))!;

        Assert.Equal((Path.GetFullPath(script), Sha(script)), (pin.ScriptPath, pin.ScriptSha256));
        Assert.Null(package.ScriptPath);
        Assert.Null(package.ScriptSha256);
    }

    [Fact]
    public void A_command_that_names_no_file_has_no_pin_and_an_address_has_none_either()
    {
        Assert.Null(McpProgramPinner.Pin(Program("x", Path.Combine(_root, "not-there.exe"))));
        Assert.Null(McpProgramPinner.Pin(McpStoreTests.Address()));
    }

    [Fact]
    public void A_command_without_a_folder_is_looked_for_on_the_path_of_the_entry_and_the_first_hit_counts()
    {
        var first = File1("p1/tool-" + Guid.NewGuid().ToString("N")[..6], "one");
        var name = Path.GetFileName(first);
        File1("p2/" + name, "two");

        var pin = McpProgramPinner.Pin(Program("t", name) with
        {
            Environment = [new McpSecretPair("PATH", Path.Combine(_root, "p1") + Path.PathSeparator + Path.Combine(_root, "p2"))],
        })!;

        Assert.Equal(Path.GetFullPath(first), pin.Path);
        Assert.Equal(Sha(first), pin.Sha256);
    }

    [Fact]
    public void On_windows_the_command_is_found_through_pathext_and_elsewhere_by_its_plain_name()
    {
        var cmd = File1("bin/npx.CMD", "@echo off");
        var plain = File1("bin/tool", "x");
        var folder = Path.Combine(_root, "bin");

        Assert.Equal(Path.GetFullPath(cmd), McpProgramPinner.Resolve("npx", null, folder, ".COM;.EXE;.BAT;.CMD", windows: true));
        Assert.Equal(Path.GetFullPath(cmd), McpProgramPinner.Resolve("npx.CMD", null, folder, ".COM;.EXE;.BAT;.CMD", windows: true));
        Assert.Equal(Path.GetFullPath(plain), McpProgramPinner.Resolve("tool", null, folder, ".COM;.EXE;.BAT;.CMD", windows: true));
        Assert.Null(McpProgramPinner.Resolve("npx", null, folder, ".EXE", windows: true));
        // not on Windows: PATHEXT means nothing
        Assert.Null(McpProgramPinner.Resolve("npx", null, folder, ".CMD", windows: false));
        Assert.Equal(Path.GetFullPath(plain), McpProgramPinner.Resolve("tool", null, folder, ".CMD", windows: false));
    }

    [Fact]
    public void A_runner_with_a_package_name_says_only_the_runner_is_checked_and_a_runner_with_a_script_does_not()
    {
        var runner = File1("bin/npx.cmd", "x");
        var script = File1("app/server.js", "js");
        var package = Program("pkg", runner, "@scope/server") with { ProgramPin = McpProgramPinner.Pin(Program("pkg", runner, "@scope/server")) };
        var withScript = Program("own", runner, script) with { ProgramPin = McpProgramPinner.Pin(Program("own", runner, script)) };
        var other = Program("own2", File1("bin/my-server.exe", "x"), "--x");

        Assert.True(McpProgramPinner.OnlyRunnerChecked(package));
        Assert.False(McpProgramPinner.OnlyRunnerChecked(withScript));
        Assert.False(McpProgramPinner.OnlyRunnerChecked(other)); // no runner: the file is the program
        Assert.False(McpProgramPinner.OnlyRunnerChecked(McpStoreTests.Address()));
    }

    [Fact]
    public void The_comparison_tells_a_changed_file_from_a_moved_one_from_a_missing_one()
    {
        var program = File1("bin/a.exe", "v1");
        var script = File1("app/s.py", "v1");
        var pin = McpProgramPinner.Pin(Program("x", program, script))!;
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(McpProgramChange.None, McpProgramPinner.Compare(pin, pin with { PinnedAt = now }));
        Assert.Equal(McpProgramChange.Hash, McpProgramPinner.Compare(pin, pin with { Sha256 = new string('a', 64) }));
        Assert.Equal(McpProgramChange.Hash, McpProgramPinner.Compare(pin, pin with { ScriptSha256 = new string('b', 64) }));
        Assert.Equal(McpProgramChange.Path, McpProgramPinner.Compare(pin, pin with { Path = Path.Combine(_root, "other", "a.exe") }));
        Assert.Equal(McpProgramChange.Path, McpProgramPinner.Compare(pin, pin with { ScriptPath = Path.Combine(_root, "other", "s.py") }));
        Assert.Equal(McpProgramChange.Missing, McpProgramPinner.Compare(pin, null));
        Assert.Equal(McpProgramChange.Missing, McpProgramPinner.Compare(pin, pin with { ScriptPath = null, ScriptSha256 = null }));
        Assert.Equal(McpProgramChange.Path, McpProgramPinner.Compare(pin with { ScriptPath = null, ScriptSha256 = null }, pin));
    }

    // ---- the store -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Adding_pins_the_program_whatever_pin_the_entry_came_with_and_the_pin_survives_a_new_store()
    {
        var program = File1("bin/a.exe", "v1");
        var forged = new McpProgramPin(program, new string('0', 64), null, null, DateTimeOffset.UtcNow);
        var store = Store();

        Assert.True(store.Add(Program("a", program) with { ProgramPin = forged }).IsValid);
        Assert.True(store.Add(McpStoreTests.Address()).IsValid);

        var pin = store.Find("a")!.ProgramPin!;
        Assert.Equal(Sha(program), pin.Sha256);
        Assert.Null(store.Find("local-web")!.ProgramPin);
        Assert.Equal(pin, Store().Find("a")!.ProgramPin);
    }

    [Fact]
    public void An_edit_that_keeps_the_program_keeps_its_pin_and_one_that_changes_the_command_pins_again()
    {
        var one = File1("bin/a.exe", "v1");
        var two = File1("bin/b.exe", "v2");
        var store = Store();
        store.Add(Program("a", one));
        var first = store.Find("a")!.ProgramPin!;
        File.WriteAllText(one, "changed behind the app's back");

        store.Update("a", store.Find("a")! with { CallTimeoutSeconds = 30 });
        Assert.Equal(first, store.Find("a")!.ProgramPin); // the timeout is no word on the program

        store.Update("a", store.Find("a")! with { Command = two });
        Assert.Equal(Sha(two), store.Find("a")!.ProgramPin!.Sha256);
    }

    [Fact]
    public void An_entry_with_a_command_that_names_no_file_yet_is_kept_without_a_pin()
    {
        var store = Store();

        Assert.True(store.Add(Program("a", Path.Combine(_root, "later.exe"))).IsValid);

        Assert.Null(store.Find("a")!.ProgramPin);
    }

    // ---- the start -------------------------------------------------------------------------------------------------------

    private async Task<(McpHost Host, FadenstackAgent Agent, McpServerStore Store, McpRig Rig)> BootAsync(McpServerStore store, ILoggerFactory? logs = null)
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };
        var host = new McpHost(_ => store, server: e => e.IsProgram ? ProgramServer(rig, e) : rig.Server(e.Label));
        var builder = new ScriptedGateway(localMcp: Allow).Builder();
        if (logs is not null)
            builder.UseLogging(logs);
        var agent = builder.AddLocalMcp(host.NewModule()).Build();
        await host.UseUserAsync("anna");
        await host.Loaded;
        await agent.StartAsync();
        return (host, agent, store, rig);
    }

    [Fact]
    public async Task A_program_that_is_as_pinned_starts_and_one_with_other_content_does_not()
    {
        var program = File1("bin/a.exe", "v1");
        var store = Store();
        store.Add(Program("good", program));
        var (host, agent, _, rig) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;
        Assert.Equal(McpServerState.Running, host.Rows[0].State);
        Assert.Equal(1, rig.Connects);

        // another run of the app: the file was changed meanwhile
        File.WriteAllText(program, "v2, not what you added");
        var (host2, agent2, _, rig2) = await BootAsync(store);
        using var _h2 = host2;
        using var _a2 = agent2;

        var row = host2.Rows[0];
        Assert.Equal(McpServerState.ProgramChanged, row.State);
        Assert.Equal(McpProgramChange.Hash, row.Status!.ProgramChange);
        Assert.Equal("The program of good changed since you added it.", row.Status.Detail);
        Assert.Equal(0, rig2.Connects); // nothing was started
        Assert.True(row.Entry.Enabled); // the person's switch is untouched; the server waits for them
        Assert.Contains(host2.Module!.Skipped, s => s.Label == "good");
    }

    [Fact]
    public async Task A_script_with_other_content_a_moved_program_and_a_missing_one_each_refuse_the_start()
    {
        var program = File1("bin/a.exe", "v1");
        var script = File1("app/s.py", "v1");
        var store = Store();
        store.Add(Program("scripted", program, script));
        store.Add(Program("gone", File1("bin/gone.exe", "x")));
        File.WriteAllText(script, "v2");
        File.Delete(Path.Combine(_root, "bin", "gone.exe"));
        var pinned = store.Find("scripted")!.ProgramPin!;
        var rig = new McpRig();
        var module = new LocalMcpModule([]);
        using var agent = new ScriptedGateway(localMcp: Allow).Builder().AddLocalMcp(module).Build();
        await agent.StartAsync();

        var withOtherPath = ProgramServer(rig, Program("moved", program));
        withOtherPath.ProgramPin = pinned with { Path = Path.Combine(_root, "elsewhere", "a.exe"), ScriptPath = null, ScriptSha256 = null };
        await module.AddAsync(withOtherPath);
        foreach (var label in new[] { "scripted", "gone" })
        {
            var e = store.Find(label)!;
            var server = ProgramServer(rig, e);
            server.ProgramPin = e.ProgramPin;
            await module.AddAsync(server);
        }

        Assert.Equal(
            [("moved", McpServerState.ProgramChanged, McpProgramChange.Path), ("scripted", McpServerState.ProgramChanged, McpProgramChange.Hash),
                ("gone", McpServerState.ProgramChanged, McpProgramChange.Missing)],
            module.Servers.Select(s => (s.Label, s.State, s.ProgramChange!.Value)).OrderBy(s => s.Label == "moved" ? 0 : s.Label == "scripted" ? 1 : 2));
        Assert.Equal(0, rig.Connects);
    }

    [Fact]
    public async Task Using_the_new_program_pins_it_starts_the_server_and_goes_into_the_audit_log_by_label_only()
    {
        var program = File1("bin/a.exe", "v1");
        var store = Store();
        store.Add(Program("files", program));
        File.WriteAllText(program, "v2");
        var (host, agent, _, rig) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;
        Assert.Equal(McpServerState.ProgramChanged, host.Rows[0].State);

        var answer = await host.ConfirmNewProgramAsync("files");
        await McpRig.EventuallyAsync(() => host.Rows[0].State == McpServerState.Running, "the server starts with the new program");

        Assert.True(answer.IsValid);
        Assert.Equal(Sha(program), store.Find("files")!.ProgramPin!.Sha256);
        Assert.Equal(1, rig.Connects);
        Assert.Equal([("local_mcp.program_changed", "files", "hash"), ("local_mcp.program_confirmed", "files", "confirmed")], Audit());
        Assert.DoesNotContain(_root, File.ReadAllText(Path.Combine(_root, "user", McpAuditLog.FileName)));
    }

    [Fact]
    public async Task Using_the_new_program_when_the_file_is_gone_changes_nothing_and_a_label_without_a_program_is_no_program()
    {
        var program = File1("bin/a.exe", "v1");
        var store = Store();
        store.Add(Program("files", program));
        store.Add(McpStoreTests.Address());
        var before = store.Find("files")!.ProgramPin;
        File.Delete(program);
        var (host, agent, _, _) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;

        Assert.Equal(McpProblem.CommandInvalid, (await host.ConfirmNewProgramAsync("files")).Problem);
        Assert.Equal(McpProblem.LabelMissing, (await host.ConfirmNewProgramAsync("local-web")).Problem);
        Assert.Equal(McpProblem.LabelMissing, (await host.ConfirmNewProgramAsync("nothing")).Problem);

        Assert.Equal(before, store.Find("files")!.ProgramPin);
        Assert.Equal(McpServerState.ProgramChanged, host.Rows[0].State);
    }

    [Fact]
    public async Task An_entry_kept_before_pins_is_pinned_at_its_next_successful_start_once_and_then_watched()
    {
        var path = Path.Combine(_root, "bin", "later.exe");
        var store = Store();
        store.Add(Program("old", path)); // no file yet: kept without a pin, as an entry of an older SDK is
        Assert.Null(store.Find("old")!.ProgramPin);
        File1("bin/later.exe", "v1");

        var (host, agent, _, rig) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;

        Assert.Equal(McpServerState.Running, host.Rows[0].State);
        await McpRig.EventuallyAsync(() => store.Find("old")!.ProgramPin is not null, "the pin is kept");
        Assert.Equal(Sha(path), store.Find("old")!.ProgramPin!.Sha256);
        Assert.Equal([("local_mcp.program_pinned", "old", "first_start")], Audit());
        Assert.Equal(1, rig.Connects); // pinning did not restart the running server

        // the same run: the server is watched from now on (a restart after a crash checks the file)
        File.WriteAllText(path, "v2");
        await host.Module!.SetEnabledAsync("old", false);
        await host.Module.SetEnabledAsync("old", true);
        Assert.Equal(McpServerState.ProgramChanged, host.Rows[0].State);
        Assert.Equal(1, Audit().Count(a => a.Event == "local_mcp.program_pinned")); // once
    }

    [Fact]
    public async Task A_server_over_http_has_no_program_to_pin_or_check()
    {
        var store = Store();
        store.Add(McpStoreTests.Address() with { Url = "http://127.0.0.1:1/mcp" });
        var (host, agent, _, _) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;

        Assert.Null(store.Find("local-web")!.ProgramPin);
        Assert.Equal(McpServerState.Running, host.Rows[0].State);
        Assert.Empty(Audit());
    }

    [Fact]
    public async Task The_manifests_device_block_names_each_enabled_program_by_file_name_and_pinned_hash_and_no_address_server()
    {
        var program = File1("secret-user/bin/server-files.exe", "v1");
        var script = File1("secret-user/app/s.py", "v1");
        var store = Store();
        store.Add(Program("files", program, script, "--token=s3cret-arg"));
        store.Add(Program("off", File1("bin/other.exe", "v1")) with { Enabled = false });
        store.Add(McpStoreTests.Address() with { Url = "http://127.0.0.1:1/mcp" });
        var rig = new McpRig();
        using var host = new McpHost(_ => store, server: e => e.IsProgram ? ProgramServer(rig, e) : rig.Server(e.Label));
        var gateway = new ScriptedGateway(localMcp: Allow);
        using var agent = gateway.Builder().DescribeApp(new AppDescription { Name = "Desktop", Version = "0.3.0" }).AddLocalMcp(host.NewModule()).Build();
        await host.UseUserAsync("anna");
        await host.Loaded;

        await agent.StartAsync();
        await McpRig.EventuallyAsync(() => gateway.Manifests.Count > 0, "the manifest is sent");

        using var json = JsonDocument.Parse(gateway.Manifests[0]);
        var device = json.RootElement.GetProperty("device");
        var programs = device.GetProperty("mcp_programs").EnumerateArray().ToList();
        var one = Assert.Single(programs);
        Assert.Equal(("files", "server-files.exe", Sha(program)), (one.GetProperty("name").GetString(), one.GetProperty("program").GetString(), one.GetProperty("sha256").GetString()));
        Assert.Equal("0.3.0", device.GetProperty("app_version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(device.GetProperty("os").GetString()));
        var text = gateway.Manifests[0];
        Assert.DoesNotContain("secret-user", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain(_root, text);
    }

    [Fact]
    public async Task A_program_pinned_at_its_first_start_is_in_the_block_of_that_same_start()
    {
        var path = Path.Combine(_root, "bin", "later.exe");
        var store = Store();
        store.Add(Program("old", path)); // kept before pins: no file yet, no pin
        File1("bin/later.exe", "v1");
        var rig = new McpRig();
        using var host = new McpHost(_ => store, server: e => ProgramServer(rig, e));
        var gateway = new ScriptedGateway(localMcp: Allow);
        using var agent = gateway.Builder().AddLocalMcp(host.NewModule()).Build();
        await host.UseUserAsync("anna");
        await host.Loaded;

        await agent.StartAsync();
        await McpRig.EventuallyAsync(() => gateway.Manifests.Count > 0, "the manifest is sent");

        using var json = JsonDocument.Parse(gateway.Manifests[0]);
        var one = Assert.Single(json.RootElement.GetProperty("device").GetProperty("mcp_programs").EnumerateArray());
        Assert.Equal(("old", "later.exe", Sha(path)), (one.GetProperty("name").GetString(), one.GetProperty("program").GetString(), one.GetProperty("sha256").GetString()));
    }

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception) + " " + exception);
        }
    }

    [Fact]
    public async Task The_log_and_the_status_of_a_changed_program_hold_the_label_and_the_kind_never_a_path_or_an_argument()
    {
        var capture = new Capture();
        var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var program = File1("secret-user/bin/a.exe", "v1");
        var store = Store();
        store.Add(Program("files", program, "--token=s3cret-arg"));
        File.WriteAllText(program, "v2");

        var (host, agent, _, _) = await BootAsync(store, logs);
        using var _h = host;
        using var _a = agent;

        var seen = string.Join("\n", capture.Lines) + "\n" + host.Rows[0].Status!.Detail + "\n" + File.ReadAllText(Path.Combine(_root, "user", McpAuditLog.FileName));
        Assert.Contains(capture.Lines, l => l.Contains("files") && l.Contains("Hash"));
        Assert.DoesNotContain("secret-user", seen);
        Assert.DoesNotContain("s3cret", seen);
        Assert.DoesNotContain(_root, seen);
    }

    [Fact]
    public async Task A_test_of_a_saved_program_that_changed_is_refused_in_the_catalog_and_starts_nothing()
    {
        var program = File1("bin/a.exe", "v1");
        var store = Store();
        store.Add(Program("files", program));
        File.WriteAllText(program, "v2");
        var (host, agent, _, rig) = await BootAsync(store);
        using var _h = host;
        using var _a = agent;
        using var catalog = new McpToolsCatalog(host, null);
        var draft = catalog.GetDraft("files")!;

        var result = await catalog.TestAsync(draft, "files");

        Assert.Equal(Fadenstack.LocalTools.TestOutcome.ProgramChanged, result.Outcome);
        Assert.Equal(0, rig.Connects);
        Assert.Equal(Fadenstack.LocalTools.ServerProblem.None, await catalog.ConfirmNewProgramAsync("files"));
    }

    [Fact]
    public async Task Importing_pins_the_program_like_adding_one_does()
    {
        var program = File1("bin/a.exe", "v1");
        var file = File1("import/mcp.json", JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["notes"] = new { command = program, args = new[] { "--x" } } } }));
        var store = Store();
        using var host = new McpHost(_ => store);
        host.NewModule();
        await host.UseUserAsync("anna");
        using var catalog = new McpToolsCatalog(host, null, new McpImporter(McpImportLocations.ForThisComputer()));

        var preview = await catalog.ReadImportAsync(Fadenstack.LocalTools.ImportSourceKind.ProjectFile, file);
        var added = await catalog.ImportAsync(preview, [.. preview.Servers.Select(s => s.Label)]);

        Assert.Equal(1, added);
        Assert.Equal(Sha(program), store.Entries.Single().ProgramPin!.Sha256);
    }
}

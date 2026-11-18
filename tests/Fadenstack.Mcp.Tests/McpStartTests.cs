using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Fadenstack.Agents;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// How a stdio server is started (sdk 0.3.1; OWASP ASVS 5.0 V1, V13). The command is resolved once to an absolute file and that file
/// starts, with the arguments as a list: no shell on Linux and macOS, none for a Windows .exe, and for a .cmd or .bat (which only
/// cmd.exe runs) none of the characters cmd.exe reads as commands may be in an argument. The MCP C# SDK's own stdio transport hands
/// every command to <c>cmd.exe /c</c> on Windows, so it is not used there.
/// </summary>
public sealed class McpStartTests : IDisposable
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden mcp start " + Guid.NewGuid().ToString("N"));

    public McpStartTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a program still ends; the temporary folder is cleared with the next run
        }
    }

    // ---- the quoting ---------------------------------------------------------------------------------------------------------

    public static TheoryData<string[], string> Quoting => new()
    {
        { [], "" },
        { ["a"], "a" },
        { [""], "\"\"" },
        { ["a b"], "\"a b\"" },
        { ["a", "b c", "d"], "a \"b c\" d" },
        { ["tab\there"], "\"tab\there\"" },
        { [@"C:\x y\z\"], "\"C:\\x y\\z\\\\\"" }, // a trailing backslash is doubled before the closing quote
        { [@"C:\xyz\"], @"C:\xyz\" }, // no space and no quote: left alone
        { [@"a\b"], @"a\b" },
        { ["say \"hi\""], "\"say \\\"hi\\\"\"" }, // a quote is escaped
        { ["\"x\""], "\"\\\"x\\\"\"" },
        { ["a\\\\\"b"], "\"a\\\\\\\\\\\"b\"" }, // two backslashes before a quote: doubled, plus one for the quote
        { ["a\\\"b c"], "\"a\\\\\\\"b c\"" },
        { ["a&b", "c|d"], "a&b c|d" }, // no shell reads these here
    };

    [Theory]
    [MemberData(nameof(Quoting))]
    public void The_command_line_follows_the_windows_quoting_rules(string[] arguments, string expected) =>
        Assert.Equal(expected, McpStart.CommandLine(arguments));

    // ---- what starts: the plan -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_exe_starts_as_itself_with_a_quoted_command_line_and_no_cmd()
    {
        var plan = McpStart.Plan(@"C:\Program Files\node\node.exe", ["server.js", "a&b c", "--x=\"y\""], Cmd);

        Assert.Equal(@"C:\Program Files\node\node.exe", plan.FileName);
        Assert.Equal("server.js \"a&b c\" \"--x=\\\"y\\\"\"", plan.Arguments);
        Assert.False(plan.ThroughCmd);
    }

    [Theory]
    [InlineData(@"C:\tools\run.cmd")]
    [InlineData(@"C:\tools\RUN.BAT")]
    public void A_batch_file_starts_through_the_absolute_cmd_with_d_s_c_and_the_whole_line_quoted(string program)
    {
        var plan = McpStart.Plan(program, ["one", "two words", ""], Cmd);

        Assert.Equal(Cmd, plan.FileName);
        Assert.Equal("/d /s /c \"\"" + program + "\" one \"two words\" \"\"\"", plan.Arguments);
        Assert.True(plan.ThroughCmd);
    }

    [Fact]
    public void A_batch_file_in_a_folder_with_a_space_and_parentheses_is_fine_because_the_path_is_quoted()
    {
        var plan = McpStart.Plan(@"C:\Program Files (x86)\tool & co\run.cmd", ["a"], Cmd);

        Assert.Equal("/d /s /c \"\"C:\\Program Files (x86)\\tool & co\\run.cmd\" a\"", plan.Arguments);
    }

    [Theory]
    [InlineData("a&calc")]
    [InlineData("\"x\" & echo pwned")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("%PATH%")]
    [InlineData("a!b")]
    [InlineData("(a)")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("a\u007fb")]
    public void A_batch_file_is_refused_when_an_argument_holds_a_character_cmd_reads_as_a_command(string argument)
    {
        var refused = Assert.Throws<McpStartRefusedException>(() => McpStart.Plan(@"C:\tools\run.cmd", ["fine", argument], Cmd));

        Assert.Equal(McpStartRefusal.ShellCharacter, refused.Reason);
        Assert.DoesNotContain(argument.Trim(), refused.Message); // never the value
        Assert.Throws<McpStartRefusedException>(() => McpStart.Plan(@"C:\tools\run.BAT", [argument], Cmd));
    }

    [Theory]
    [InlineData(@"C:\100%\run.cmd")]
    [InlineData("C:\\a\"b\\run.cmd")]
    public void A_batch_file_whose_own_path_cmd_would_expand_is_refused_too(string program) =>
        Assert.Equal(McpStartRefusal.ShellCharacter, Assert.Throws<McpStartRefusedException>(() => McpStart.Plan(program, [], Cmd)).Reason);

    [Fact]
    public void The_same_characters_are_no_trouble_for_an_exe_because_no_shell_reads_them()
    {
        var plan = McpStart.Plan(@"C:\tools\run.exe", ["a&calc", "%PATH%", "(x)", "a|b"], Cmd);

        Assert.Equal(@"C:\tools\run.exe", plan.FileName);
        Assert.Equal("a&calc %PATH% (x) a|b", plan.Arguments);
    }

    [Fact]
    public void The_start_info_has_no_shell_and_the_given_folder_and_environment()
    {
        var plan = McpStart.Plan(@"C:\tools\run.exe", ["x"], Cmd);

        var info = McpStart.StartInfo(plan, new Dictionary<string, string?> { ["FADEN_T"] = "1", ["PATH_GONE_T"] = null }, @"C:\work");

        Assert.False(info.UseShellExecute);
        Assert.Equal(@"C:\tools\run.exe", info.FileName);
        Assert.Equal(@"C:\work", info.WorkingDirectory);
        Assert.Equal("1", info.Environment["FADEN_T"]);
        Assert.False(info.Environment.ContainsKey("PATH_GONE_T"));
    }

    // ---- the policy judges the file that starts -------------------------------------------------------------------------------

    [Fact]
    public void A_policy_item_names_the_resolved_file_not_only_the_word_that_was_configured()
    {
        var server = LocalMcpServer.Stdio("x", @"C:\good\tool"); // an item "C:\good\tool" matches the command as typed ...
        var rule = new PolicyRule(PolicyAccess.Listed, [@"C:\good\tool"]);
        Assert.True(McpSources.Decide(rule, server).Allowed);

        server.ResolvedProgram = @"C:\good\tool.com"; // ... but PATHEXT starts another file: the item does not name it
        Assert.False(McpSources.Decide(rule, server).Allowed);

        server.ResolvedProgram = @"C:\good\tool";
        Assert.True(McpSources.Decide(rule, server).Allowed);
        server.ResolvedProgram = @"C:\good\tool.com";
        Assert.True(McpSources.Decide(new PolicyRule(PolicyAccess.Listed, [@"C:\good\tool.com"]), server).Allowed); // the item names the file
        Assert.True(McpSources.Decide(new PolicyRule(PolicyAccess.Listed, ["tool"]), server).Allowed); // a file name still names the file
    }

    [Fact]
    public void A_bare_name_is_looked_for_on_the_path_and_never_in_the_working_folder()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var onPath = Path.Combine(bin, McpProgramPinner.IsWindows ? "fakenode.cmd" : "fakenode");
        File.WriteAllText(onPath, "x");
        File.WriteAllText(Path.Combine(work, McpProgramPinner.IsWindows ? "other.cmd" : "other"), "x");
        File.WriteAllText(Path.Combine(work, McpProgramPinner.IsWindows ? "fakenode.cmd" : "fakenode"), "planted");

        Assert.Equal(onPath, McpProgramPinner.Resolve("fakenode", work, bin), ignoreCase: true); // PATHEXT may give its own letter case
        Assert.Null(McpProgramPinner.Resolve("other", work, bin)); // only in the working folder: not found
    }

    // ---- real starts (Windows) ------------------------------------------------------------------------------------------------

    private const string ServerScript = """
        $in = [Console]::In
        $tool = ($args -join '~')
        while ($true) {
          $line = $in.ReadLine()
          if ($null -eq $line) { break }
          if ($line -notmatch '"id"\s*:\s*(\d+|"[^"]*")') { continue }
          $id = $Matches[1]
          $method = ''
          if ($line -match '"method"\s*:\s*"([^"]+)"') { $method = $Matches[1] }
          $version = '2025-11-25'
          if ($line -match '"protocolVersion"\s*:\s*"([^"]+)"') { $version = $Matches[1] }
          if ($method -eq 'initialize') {
            $out = '{"jsonrpc":"2.0","id":' + $id + ',"result":{"protocolVersion":"' + $version + '","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"}}}'
          } elseif ($method -eq 'tools/list') {
            $out = '{"jsonrpc":"2.0","id":' + $id + ',"result":{"tools":[{"name":"' + $tool + '","description":"d","inputSchema":{"type":"object"}}]}}'
          } elseif ($method -eq 'ping') {
            $out = '{"jsonrpc":"2.0","id":' + $id + ',"result":{}}'
          } else {
            $out = '{"jsonrpc":"2.0","id":' + $id + ',"error":{"code":-32601,"message":"no"}}'
          }
          [Console]::Out.Write($out + "`n")
          [Console]::Out.Flush()
        }
        """;

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A stdio MCP server in PowerShell whose one tool is named by the arguments, joined with ~; a .cmd runs it and leaves a marker.</summary>
    private (string Cmd, string Script, string Started) Install(string folder, string name = "server")
    {
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, name + ".ps1");
        File.WriteAllText(script, ServerScript);
        var cmd = Path.Combine(folder, name + ".cmd");
        var started = Path.Combine(folder, name + ".started");
        File.WriteAllText(cmd, "@echo off\r\necho x> \"%~dp0" + name + ".started\"\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0" + name + ".ps1\" %*\r\n");
        return (cmd, script, started);
    }

    private static async Task<IReadOnlyList<string>> ToolsAsync(LocalMcpServer server)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var transport = await server.Connect(new McpConnection(false), limit.Token);
        var client = await McpClient.CreateAsync(transport, cancellationToken: limit.Token);
        try
        {
            return [.. (await client.ListToolsAsync(cancellationToken: limit.Token)).Select(t => t.Name)];
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [WindowsFact]
    public async Task A_cmd_server_with_plain_arguments_starts_through_cmd_and_lists_its_tools()
    {
        var (cmd, _, started) = Install(Path.Combine(_root, "plain"));
        var server = LocalMcpServer.Stdio("plain", cmd, ["alpha", "two words"]);

        var tools = await ToolsAsync(server);

        Assert.Equal(["alpha~two words"], tools);
        Assert.True(File.Exists(started));
    }

    [WindowsTheory]
    [InlineData("a&calc")]
    [InlineData("\"x\" & echo pwned")]
    [InlineData("%COMSPEC%")]
    public async Task A_cmd_server_with_a_shell_character_in_an_argument_is_refused_and_nothing_starts(string argument)
    {
        var (cmd, _, started) = Install(Path.Combine(_root, "refused"));
        var server = LocalMcpServer.Stdio("refused", cmd, ["fine", argument]);

        var refused = await Assert.ThrowsAsync<McpStartRefusedException>(() => ToolsAsync(server));

        Assert.Equal(McpStartRefusal.ShellCharacter, refused.Reason);
        Assert.False(File.Exists(started)); // not even the .cmd ran
    }

    [WindowsFact]
    public async Task An_exe_server_starts_without_cmd_and_its_arguments_arrive_as_they_were()
    {
        var (_, script, _) = Install(Path.Combine(_root, "exe"));
        var server = LocalMcpServer.Stdio("exe", PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "a&b c", "%PATH%"]);

        var transport = await server.Connect(new McpConnection(false), CancellationToken.None);
        var info = ((McpProcessTransport)transport).StartInfo;
        Assert.Equal(PowerShell, info.FileName); // not cmd.exe /c
        Assert.False(info.UseShellExecute);
        Assert.DoesNotContain("cmd.exe", info.FileName, StringComparison.OrdinalIgnoreCase);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var client = await McpClient.CreateAsync(transport, cancellationToken: limit.Token);
        try
        {
            Assert.Equal(["a&b c~%PATH%"], (await client.ListToolsAsync(cancellationToken: limit.Token)).Select(t => t.Name));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [WindowsFact]
    public async Task A_bare_name_starts_the_one_on_the_path_and_never_one_planted_in_the_working_folder()
    {
        var bin = Path.Combine(_root, "pathbin");
        var (_, _, started) = Install(bin, "fakenode");
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var planted = Path.Combine(work, "fakenode.cmd");
        File.WriteAllText(planted, "@echo off\r\necho pwned> \"%~dp0pwned.txt\"\r\n");
        var env = new Dictionary<string, string?> { ["PATH"] = bin + ";" + Environment.GetEnvironmentVariable("PATH") };
        var server = LocalMcpServer.Stdio("bare", "fakenode", ["from-path"], env, work);

        var tools = await ToolsAsync(server);

        Assert.Equal(["from-path"], tools);
        Assert.True(File.Exists(started));
        Assert.False(File.Exists(Path.Combine(work, "pwned.txt")));
    }

    [WindowsFact]
    public async Task A_command_that_is_not_found_is_not_started_and_says_so_without_the_command()
    {
        var server = LocalMcpServer.Stdio("gone", "faden-no-such-program-" + Guid.NewGuid().ToString("N"), ["--token=s3cret"]);

        var e = await Assert.ThrowsAsync<FileNotFoundException>(() => ToolsAsync(server));

        Assert.DoesNotContain("s3cret", e.Message);
    }

    // ---- in the module: the status, the log, the policy ------------------------------------------------------------------------

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<(int Id, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue((eventId.Id, formatter(state, exception) + " " + exception));
        }
    }

    private static FadenstackAgent Agent(LocalMcpModule module, MachinePolicy policy, ILoggerFactory? logs = null)
    {
        var builder = new ScriptedGateway(localMcp: """{"mode":"allow","developer_mode":false}""").Builder().UseMachinePolicy(policy);
        if (logs is not null)
            builder.UseLogging(logs);
        return builder.AddLocalMcp(module).Build();
    }

    [WindowsFact]
    public async Task The_module_shows_a_refused_start_as_not_allowed_with_its_kind_and_logs_it_without_the_argument()
    {
        var capture = new Capture();
        var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var (cmd, _, started) = Install(Path.Combine(_root, "module"));
        var module = new LocalMcpModule([LocalMcpServer.Stdio("batch", cmd, ["--key=s3cret&calc"])]);
        using var agent = Agent(module, MachinePolicy.None, logs);

        await agent.StartAsync();

        var status = module.GetStatus("batch")!;
        Assert.Equal((McpServerState.NotAllowed, McpStartRefusal.ShellCharacter), (status.State, status.StartRefusal));
        Assert.False(File.Exists(started));
        var line = Assert.Single(capture.Lines, l => l.Id == 3012);
        Assert.Contains("batch", line.Text);
        Assert.DoesNotContain("s3cret", string.Join("\n", capture.Lines.Select(l => l.Text)) + status.Detail);
        Assert.DoesNotContain("calc", status.Detail);
    }

    [WindowsFact]
    public async Task The_module_starts_the_file_the_policy_judged_and_refuses_a_list_that_names_another()
    {
        var (cmd, _, _) = Install(Path.Combine(_root, "judged"));
        var withoutExtension = Path.ChangeExtension(cmd, null);
        var json = (string name) => "{\"mcp\":[" + System.Text.Json.JsonSerializer.Serialize(name) + "]}";

        // the command is typed without an extension, PATHEXT makes it server.cmd: an item naming the typed word does not name the file
        var typed = new LocalMcpModule([LocalMcpServer.Stdio("typed", withoutExtension, ["x"])]);
        using (var agent = Agent(typed, MachinePolicy.FromJson(json(withoutExtension))))
        {
            await agent.StartAsync();
            Assert.Equal(McpServerState.NotAllowed, typed.GetStatus("typed")!.State);
        }

        var named = new LocalMcpModule([LocalMcpServer.Stdio("named", withoutExtension, ["x"])]);
        using (var agent = Agent(named, MachinePolicy.FromJson(json(cmd))))
        {
            await agent.StartAsync();
            Assert.Equal(McpServerState.Running, named.GetStatus("named")!.State);
        }
    }
}

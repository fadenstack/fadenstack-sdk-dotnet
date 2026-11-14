

namespace Fadenstack.Mcp.Tests;

/// <summary>Importing the server lists of Claude Desktop, VS Code, Cursor and a .mcp.json (desktop-agent/31 §4.4), from fixtures.</summary>
public sealed class McpImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcpimport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string ClaudeDesktop = """
        {"globalShortcut":"Ctrl+Space","mcpServers":{
          "filesystem":{"command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","C:\\Users\\anna\\Desktop"]},
          "Brave Search":{"command":"npx","args":["-y","@modelcontextprotocol/server-brave-search"],"env":{"BRAVE_API_KEY":"key-123"}},
          "empty":{}
        }}
        """;

    private const string VsCode = """
        // the user's mcp.json has comments
        {
          "inputs":[{"type":"promptString","id":"api-key","description":"API key","password":true}],
          "servers":{
            "localTool":{"type":"stdio","command":"npx","args":["-y","some-mcp-server"],"env":{"API_KEY":"${input:api-key}"},"envFile":"x.env"},
            "viaHttp":{"type":"http","url":"http://localhost:7000/mcp","headers":{"Authorization":"Bearer ${input:api-key}"}},
            "remote":{"type":"http","url":"https://example.com/mcp"},
            "withVars":{"command":"${env:HOME}/bin/tool","args":["${workspaceFolder}"]},
          }
        }
        """;

    private const string Cursor = """
        {"mcpServers":{"docs":{"command":"python","args":["-m","docs_server"],"cwd":"/home/anna/docs"},
                       "off":{"command":"tool","disabled":true},
                       "net":{"url":"http://192.168.0.9:9000/sse"}}}
        """;

    private const string ProjectFile = """
        {"mcpServers":{"local-api":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"X-Key":"k"}},
                       "remote-api":{"type":"http","url":"https://api.example.com/mcp"}}}
        """;

    [Fact]
    public void Claude_desktop_servers_come_as_candidates_with_their_origin_and_the_odd_ones_are_skipped()
    {
        var result = McpImporter.Parse(McpImportSource.ClaudeDesktop, "x.json", ClaudeDesktop, []);

        Assert.Equal(McpImportProblem.None, result.Problem);
        Assert.Equal(["filesystem", "brave-search"], result.Servers.Select(s => s.Entry.Label));
        var files = result.Servers[0].Entry;
        Assert.Equal(("npx", "Claude Desktop", false), (files.Command, files.Origin, files.Enabled)); // imported servers arrive off
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\anna\\Desktop"], files.Arguments);
        var brave = result.Servers[1];
        Assert.Equal("Brave Search", brave.OriginalName);
        Assert.Contains(McpImportNote.Renamed, brave.Notes);
        Assert.Equal("key-123", Assert.Single(brave.Entry.Environment).Value);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(("empty", McpSkipReason.NothingToStart), (skip.Name, skip.Reason));
    }

    [Fact]
    public void Vs_code_inputs_become_empty_values_and_the_server_waits_switched_off()
    {
        var result = McpImporter.Parse(McpImportSource.VsCode, "mcp.json", VsCode, []);

        Assert.Equal(["localtool", "viahttp", "withvars"], result.Servers.Select(s => s.Entry.Label));
        var local = result.Servers[0];
        Assert.Equal("", Assert.Single(local.Entry.Environment).Value);
        Assert.False(local.Entry.Enabled);
        Assert.Contains(McpImportNote.NeedsValue, local.Notes);
        Assert.Contains(McpImportNote.IgnoredFields, local.Notes);
        Assert.Equal("envFile", local.Detail);
        var http = result.Servers[1];
        Assert.Equal("http://localhost:7000/mcp", http.Entry.Url);
        Assert.Equal("Bearer ", Assert.Single(http.Entry.Headers).Value);
        Assert.Equal("VS Code", http.Entry.Origin);
        var vars = result.Servers[2];
        Assert.Contains(McpImportNote.HasPlaceholders, vars.Notes);
        Assert.False(vars.Entry.Enabled);
        var network = Assert.Single(result.Skipped);
        Assert.Equal(("remote", McpSkipReason.NetworkServer), (network.Name, network.Reason));
    }

    [Fact]
    public void Cursor_servers_keep_their_folder_a_switched_off_one_stays_off_and_a_network_one_is_skipped()
    {
        var result = McpImporter.Parse(McpImportSource.Cursor, "mcp.json", Cursor, []);

        Assert.Equal(["docs", "off"], result.Servers.Select(s => s.Entry.Label));
        Assert.Equal("/home/anna/docs", result.Servers[0].Entry.WorkingFolder);
        Assert.False(result.Servers[1].Entry.Enabled);
        Assert.Contains(McpImportNote.WasOff, result.Servers[1].Notes);
        Assert.Equal(McpSkipReason.NetworkServer, Assert.Single(result.Skipped).Reason);
        Assert.All(result.Servers, s => Assert.Equal("Cursor", s.Entry.Origin));
    }

    [Fact]
    public async Task A_project_file_is_read_from_the_path_the_person_picks_and_labels_avoid_the_ones_taken()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".mcp.json");
        File.WriteAllText(path, ProjectFile);
        var importer = new McpImporter(new McpImportLocations(McpOs.Linux, _ => null, _root));

        var result = await importer.ReadAsync(McpImportSource.ProjectFile, path, ["local-api"]);

        var server = Assert.Single(result.Servers);
        Assert.Equal("local-api-2", server.Entry.Label);
        Assert.Equal(".mcp.json", server.Entry.Origin);
        Assert.Equal("X-Key", Assert.Single(server.Entry.Headers).Name);
        Assert.Equal(McpSkipReason.NetworkServer, Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Files_that_are_missing_broken_or_without_a_server_list_give_a_problem_not_a_throw()
    {
        Directory.CreateDirectory(_root);
        var broken = Path.Combine(_root, "broken.json");
        var none = Path.Combine(_root, "none.json");
        File.WriteAllText(broken, "{ not json");
        File.WriteAllText(none, """{"servers":{}}""");
        var importer = new McpImporter(new McpImportLocations(McpOs.Linux, _ => null, _root));

        Assert.Equal(McpImportProblem.NotFound, (await importer.ReadAsync(McpImportSource.Cursor, null, [])).Problem);
        Assert.Equal(McpImportProblem.Unreadable, (await importer.ReadAsync(McpImportSource.ProjectFile, broken, [])).Problem);
        Assert.Equal(McpImportProblem.NoServerList, (await importer.ReadAsync(McpImportSource.ProjectFile, none, [])).Problem);
    }

    [Fact]
    public void Every_config_shape_arrives_switched_off_and_not_trusted_whatever_the_file_says()
    {
        var shapes = new[]
        {
            McpImporter.Parse(McpImportSource.ClaudeDesktop, "x.json", ClaudeDesktop, []),
            McpImporter.Parse(McpImportSource.VsCode, "mcp.json", VsCode, []),
            McpImporter.Parse(McpImportSource.Cursor, "mcp.json", Cursor, []),
            McpImporter.Parse(McpImportSource.ProjectFile, ".mcp.json", ProjectFile, []),
            McpImporter.Parse(McpImportSource.ClaudeCode, ".claude.json", ClaudeDesktop, []),
            McpImporter.Parse(McpImportSource.Windsurf, "mcp_config.json", Cursor, []),
            McpImporter.Parse(McpImportSource.GeminiCli, "settings.json", Cursor, []),
            McpImporter.Parse(McpImportSource.LmStudio, "mcp.json", Cursor, []),
        };

        var all = shapes.SelectMany(s => s.Servers).ToList();
        Assert.True(all.Count >= 12);
        Assert.All(all, c => Assert.False(c.Entry.Enabled));
        Assert.All(all, c => Assert.False(c.Entry.Trusted));
        Assert.All(all, c => Assert.Empty(c.Entry.ToolClasses));
    }

    [Fact]
    public void Nothing_is_added_by_reading()
    {
        var store = new McpServerStore(_root, new Fadenstack.Auth.OwnerOnlyFileProtector());

        McpImporter.Parse(McpImportSource.ClaudeDesktop, "x", ClaudeDesktop, store.Entries.Select(e => e.Label));

        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Each_program_is_looked_for_where_its_vendor_keeps_the_list_on_each_os()
    {
        var windows = new McpImportLocations(McpOs.Windows, n => n switch { "APPDATA" => @"C:\Users\a\AppData\Roaming", "LOCALAPPDATA" => @"C:\Users\a\AppData\Local", _ => null },
            @"C:\Users\a", _ => [@"C:\Users\a\AppData\Local\Packages\Claude_x"]);
        var mac = new McpImportLocations(McpOs.MacOs, _ => null, "/Users/a");
        var linux = new McpImportLocations(McpOs.Linux, _ => null, "/home/a");
        var xdg = new McpImportLocations(McpOs.Linux, n => n == "XDG_CONFIG_HOME" ? "/cfg" : null, "/home/a");

        string P(string path) => path.Replace('\\', '/');
        Assert.Equal(["C:/Users/a/AppData/Roaming/Claude/claude_desktop_config.json", "C:/Users/a/AppData/Local/Packages/Claude_x/LocalCache/Roaming/Claude/claude_desktop_config.json"],
            windows.PathsOf(McpImportSource.ClaudeDesktop).Select(P));
        Assert.Equal("C:/Users/a/AppData/Roaming/Code/User/mcp.json", P(Assert.Single(windows.PathsOf(McpImportSource.VsCode))));
        Assert.Equal("C:/Users/a/.cursor/mcp.json", P(Assert.Single(windows.PathsOf(McpImportSource.Cursor))));
        Assert.Equal("/Users/a/Library/Application Support/Claude/claude_desktop_config.json", P(Assert.Single(mac.PathsOf(McpImportSource.ClaudeDesktop))));
        Assert.Equal("/Users/a/Library/Application Support/Code/User/mcp.json", P(Assert.Single(mac.PathsOf(McpImportSource.VsCode))));
        Assert.Equal("/Users/a/.cursor/mcp.json", P(Assert.Single(mac.PathsOf(McpImportSource.Cursor))));
        Assert.Equal("/home/a/.config/Claude/claude_desktop_config.json", P(Assert.Single(linux.PathsOf(McpImportSource.ClaudeDesktop))));
        Assert.Equal("/home/a/.config/Code/User/mcp.json", P(Assert.Single(linux.PathsOf(McpImportSource.VsCode))));
        Assert.Equal("/cfg/Code/User/mcp.json", P(Assert.Single(xdg.PathsOf(McpImportSource.VsCode))));
        Assert.Empty(linux.PathsOf(McpImportSource.ProjectFile));
    }
}

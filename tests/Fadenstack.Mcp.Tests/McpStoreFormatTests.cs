using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Auth;
using Fadenstack.Storage;
using Fadenstack.Tools;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// The tests that point <see cref="FadenstackPaths"/> at a temporary folder run alone: the root is process-wide, and a test running
/// beside them (an agent with default stores) would put its stores in a folder that is deleted under it.
/// </summary>
[CollectionDefinition("McpPaths", DisableParallelization = true)]
public sealed class McpPathsCollection;

/// <summary>
/// The store keeps the desktop's format and folder (existing desktop users keep their server lists); the trust mark and the
/// person's raises are optional keys of an entry; standalone has a folder of its own.
/// </summary>
[Collection("McpPaths")]
public sealed class McpStoreFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcpformat-" + Guid.NewGuid().ToString("N"));

    public McpStoreFormatTests() => FadenstackPaths.RootForTests = Path.Combine(_root, "paths");

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    /// <summary>What the desktop's store wrote (version 1, camelCase, nulls left out): no trust, no raises.</summary>
    private const string DesktopFile = """
        {"version":1,"servers":[
          {"label":"notes-db","enabled":true,"command":"node","arguments":["server.js","--token=abc"],"workingFolder":"C:\\work",
           "environment":[{"name":"API_KEY","value":"s3cret-key-123"}],"headers":[],"callTimeoutSeconds":90,"origin":"Cursor"},
          {"label":"local-web","enabled":false,"arguments":[],"environment":[],"url":"http://127.0.0.1:8123/mcp",
           "headers":[{"name":"Authorization","value":"Bearer s3cret-header"}],"callTimeoutSeconds":120,"origin":"typed"}
        ]}
        """;

    private string WriteFile(string folder, string json)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, McpServerStore.FileName);
        File.WriteAllBytes(path, new Reversing().Protect(Encoding.UTF8.GetBytes(json)));
        return path;
    }

    private static string Read(string path) => Encoding.UTF8.GetString(new Reversing().Unprotect(File.ReadAllBytes(path)));

    [Fact]
    public void A_file_the_desktop_wrote_loads_unchanged_with_nothing_trusted_and_nothing_raised()
    {
        var folder = Path.Combine(_root, "desktop");
        WriteFile(folder, DesktopFile);

        var store = new McpServerStore(folder, new Reversing());

        Assert.False(store.Unreadable);
        Assert.Equal(["notes-db", "local-web"], store.Entries.Select(e => e.Label));
        var program = store.Find("notes-db")!;
        Assert.Equal(("node", "Cursor", 90, @"C:\work"), (program.Command, program.Origin, program.CallTimeoutSeconds, program.WorkingFolder));
        Assert.Equal(["server.js", "--token=abc"], program.Arguments);
        Assert.Equal("s3cret-key-123", Assert.Single(program.Environment).Value);
        Assert.All(store.Entries, e => Assert.False(e.Trusted));
        Assert.All(store.Entries, e => Assert.Empty(e.ToolClasses));
        Assert.False(store.Find("local-web")!.Enabled);
        Assert.Equal("Bearer s3cret-header", Assert.Single(store.Find("local-web")!.Headers).Value);
    }

    [Fact]
    public void Saving_an_untouched_list_writes_the_desktops_format_and_no_new_keys()
    {
        var folder = Path.Combine(_root, "desktop");
        var path = WriteFile(folder, DesktopFile);
        var store = new McpServerStore(folder, new Reversing());

        store.SetEnabled("local-web", true); // any save
        var json = Read(path);

        Assert.DoesNotContain("trusted", json);
        Assert.DoesNotContain("toolClasses", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(new[] { "notes-db", "local-web" }, doc.RootElement.GetProperty("servers").EnumerateArray().Select(s => s.GetProperty("label").GetString()));
        Assert.Equal("Cursor", doc.RootElement.GetProperty("servers")[0].GetProperty("origin").GetString());
    }

    // The desktop's reader before the move: its own DTOs, no knowledge of the new keys.
    private sealed class OldFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("servers")]
        public List<OldEntry> Servers { get; set; } = new();
    }

    private sealed class OldEntry
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("arguments")]
        public List<string>? Arguments { get; set; }

        [JsonPropertyName("origin")]
        public string Origin { get; set; } = "typed";
    }

    [Fact]
    public void Trust_and_raises_survive_a_reload_and_a_reader_that_does_not_know_them_still_reads_the_file()
    {
        var folder = Path.Combine(_root, "new");
        var store = new McpServerStore(folder, new Reversing());
        Assert.True(store.Add(McpStoreTests.Program("alpha")).IsValid);
        Assert.True(store.Add(McpStoreTests.Address("beta")).IsValid);
        Assert.True(store.SetTrusted("alpha", true));
        Assert.True(store.SetToolClass("alpha", "plain", ToolClass.Write));
        Assert.True(store.SetToolClass("alpha", "peek", ToolClass.Read));

        var again = new McpServerStore(folder, new Reversing());
        var alpha = again.Find("alpha")!;
        Assert.True(alpha.Trusted);
        Assert.Equal(ToolClass.Write, alpha.ToolClasses["plain"]);
        Assert.Equal(ToolClass.Read, alpha.ToolClasses["peek"]);
        Assert.False(again.Find("beta")!.Trusted);

        var json = Read(Path.Combine(folder, McpServerStore.FileName));
        var old = JsonSerializer.Deserialize<OldFile>(json)!; // the desktop's earlier reader: unknown keys are ignored
        Assert.Equal(1, old.Version);
        Assert.Equal(["alpha", "beta"], old.Servers.Select(s => s.Label));
        Assert.Equal(("node", "Cursor"), (old.Servers[0].Command, old.Servers[0].Origin));
        Assert.Contains("\"trusted\":true", json);
        Assert.Contains("\"toolClasses\"", json);
    }

    [Fact]
    public void Only_read_and_write_are_kept_from_the_file_and_a_raise_is_only_ever_read_or_write()
    {
        var folder = Path.Combine(_root, "hand");
        WriteFile(folder, """
            {"version":1,"servers":[{"label":"alpha","enabled":true,"command":"node","arguments":[],"environment":[],"headers":[],
              "callTimeoutSeconds":120,"origin":"typed","trusted":true,
              "toolClasses":{"a":"read","b":"WRITE","c":"destructive","d":"unknown","e":"banana"}}]}
            """);
        var store = new McpServerStore(folder, new Reversing());

        var raised = store.Find("alpha")!.ToolClasses;
        Assert.Equal(new[] { "a", "b" }, raised.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal((ToolClass.Read, ToolClass.Write), (raised["a"], raised["b"]));
        Assert.Throws<ArgumentException>(() => store.SetToolClass("alpha", "x", ToolClass.Destructive));
        Assert.Throws<ArgumentException>(() => store.SetToolClass("alpha", "x", ToolClass.Unknown));
        Assert.False(store.SetToolClass("missing", "x", ToolClass.Read));
        Assert.False(store.SetTrusted("missing", true));
    }

    [Fact]
    public void Taking_a_raise_back_and_a_trust_change_are_saved_and_announced()
    {
        var folder = Path.Combine(_root, "back");
        var store = new McpServerStore(folder, new Reversing());
        store.Add(McpStoreTests.Program("alpha"));
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.SetToolClass("alpha", "plain", ToolClass.Write);
        store.SetToolClass("alpha", "plain", null);
        store.SetTrusted("alpha", true);
        store.SetTrusted("alpha", true); // no change, no event
        store.SetTrusted("alpha", false);

        Assert.Equal(4, raised);
        var json = Read(Path.Combine(folder, McpServerStore.FileName));
        Assert.DoesNotContain("trusted", json);
        Assert.DoesNotContain("toolClasses", json);
        Assert.Empty(new McpServerStore(folder, new Reversing()).Find("alpha")!.ToolClasses);
    }

    [Fact]
    public void Signed_in_users_keep_their_folder_and_the_standalone_principal_has_its_own()
    {
        var data = FadenstackPaths.DataRoot;

        var anna = McpServerStore.ForUser("anna", new Reversing());
        anna.Add(McpStoreTests.Program("anna-server"));
        var standalone = McpServerStore.ForStandalone(new Reversing());
        standalone.Add(McpStoreTests.Program("mine"));

        Assert.True(File.Exists(Path.Combine(data, "users", "anna", "mcp", "servers.dat"))); // as the desktop had it
        Assert.True(File.Exists(Path.Combine(data, "users", "standalone", "mcp", "servers.dat")));
        Assert.Equal(["anna-server"], McpServerStore.ForUser("anna", new Reversing()).Entries.Select(e => e.Label));
        Assert.Equal(["mine"], McpServerStore.ForStandalone(new Reversing()).Entries.Select(e => e.Label));
    }
}


using Fadenstack.Auth;

namespace Fadenstack.Mcp.Tests;

/// <summary>The person's servers on disk (desktop-agent/31 §4.4): encrypted per user, memory only without a key store, valid, masked.</summary>
public sealed class McpStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcpstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static McpServerEntry Program(string label = "notes-db") => new()
    {
        Label = label,
        Command = "node",
        Arguments = ["server.js", "--token=abc"],
        WorkingFolder = Path.GetTempPath(),
        Environment = [new McpSecretPair("API_KEY", "s3cret-key-123")],
        Origin = "Cursor",
        CallTimeoutSeconds = 90,
    };

    internal static McpServerEntry Address(string label = "local-web") => new()
    {
        Label = label,
        Url = "http://127.0.0.1:8123/mcp",
        Headers = [new McpSecretPair("Authorization", "Bearer s3cret-header")],
    };

    /// <summary>Reverses the bytes: unreadable as text, and "encrypts" for the store's purposes.</summary>
    private sealed class Reversing(bool encrypts) : ISecretProtector
    {
        public bool Encrypts => encrypts;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    [Fact]
    public void The_list_survives_a_new_store_and_the_file_holds_no_plain_text()
    {
        var store = new McpServerStore(_root, new Reversing(true));
        Assert.True(store.Add(Program()).IsValid);
        Assert.True(store.Add(Address()).IsValid);
        store.SetEnabled("local-web", false);

        var file = File.ReadAllBytes(Path.Combine(_root, McpServerStore.FileName));
        var text = System.Text.Encoding.UTF8.GetString(file);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("notes-db", text);
        Assert.DoesNotContain("node", text);

        var again = new McpServerStore(_root, new Reversing(true));
        Assert.Equal(["notes-db", "local-web"], again.Entries.Select(e => e.Label));
        var program = again.Find("notes-db")!;
        Assert.Equal(("node", "Cursor", 90), (program.Command, program.Origin, program.CallTimeoutSeconds));
        Assert.Equal(["server.js", "--token=abc"], program.Arguments);
        Assert.Equal("s3cret-key-123", Assert.Single(program.Environment).Value);
        Assert.False(again.Find("local-web")!.Enabled);
        Assert.Equal("Bearer s3cret-header", Assert.Single(again.Find("local-web")!.Headers).Value);
    }

    [Fact]
    public void Without_a_key_store_the_list_stays_in_memory_and_the_store_says_so()
    {
        var store = new McpServerStore(_root, new Reversing(false));
        Assert.False(store.KeepsOnDisk);
        Assert.True(store.Add(Program()).IsValid);

        Assert.Single(store.Entries);
        Assert.False(Directory.Exists(_root));
        Assert.Empty(new McpServerStore(_root, new Reversing(false)).Entries);
        Assert.False(McpServerStore.InMemory().KeepsOnDisk);
    }

    [Fact]
    public void A_file_that_cannot_be_read_gives_an_empty_list_and_is_kept_aside_at_the_next_save()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, McpServerStore.FileName), "not json at all");
        var store = new McpServerStore(_root, new Reversing(true));

        Assert.True(store.Unreadable);
        Assert.Empty(store.Entries);
        store.Add(Program());

        Assert.False(store.Unreadable);
        Assert.True(File.Exists(Path.Combine(_root, McpServerStore.FileName + ".unreadable")));
        Assert.Single(new McpServerStore(_root, new Reversing(true)).Entries);
    }

    [Fact]
    public void Update_renames_in_place_remove_forgets_and_changes_are_announced()
    {
        var store = new McpServerStore(_root, new Reversing(true));
        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Add(Program("one"));
        store.Add(Program("two"));

        Assert.True(store.Update("one", Program("uno")).IsValid);
        Assert.Equal(["uno", "two"], store.Entries.Select(e => e.Label));
        Assert.Equal(McpProblem.LabelTaken, store.Update("uno", Program("two")).Problem);
        Assert.True(store.Remove("two"));
        Assert.False(store.Remove("two"));
        Assert.Equal(4, raised);
    }

    [Theory]
    [InlineData("", McpProblem.LabelMissing)]
    [InlineData("Notes", McpProblem.LabelInvalid)]
    [InlineData("has space", McpProblem.LabelInvalid)]
    [InlineData("under_score", McpProblem.LabelInvalid)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", McpProblem.LabelInvalid)]
    public void Labels_are_a_to_z_digits_and_hyphens_up_to_32(string label, McpProblem problem)
    {
        Assert.Equal(problem, McpServerValidator.Validate(Program(label), []).Problem);
        Assert.True(McpServerValidator.Validate(Program("a-1"), []).IsValid);
        Assert.True(McpServerValidator.Validate(Program(new string('a', 32)), []).IsValid);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8000/mcp")]
    [InlineData("http://mcp.example.com/")]
    [InlineData("http://192.168.1.2/mcp")]
    [InlineData("http://localhost.evil.example/mcp")]
    public void A_plain_http_address_beyond_this_computer_is_kept_with_the_switch_off_and_waits_for_it(string url)
    {
        var store = new McpServerStore(_root, new Reversing(true));

        var check = store.Add(Address() with { Url = url, Headers = [] });

        Assert.True(check.IsValid, check.Problem.ToString());
        Assert.False(Assert.Single(store.Entries).PlainHttpInLan); // its connection is refused until the person switches it on
    }

    [Theory]
    [InlineData("http://localhost:3000/mcp")]
    [InlineData("http://127.0.0.1/mcp")]
    [InlineData("http://127.5.5.5:9/mcp")]
    [InlineData("http://[::1]:8080/mcp")]
    public void Addresses_on_this_computer_are_accepted(string url) =>
        Assert.True(McpServerValidator.Validate(Address() with { Url = url }, []).IsValid);

    [Fact]
    public void Other_rules_commands_names_values_and_timeouts()
    {
        Assert.Equal(McpProblem.CommandMissing, McpServerValidator.Validate(Program() with { Command = " " }, []).Problem);
        Assert.Equal(McpProblem.CommandInvalid, McpServerValidator.Validate(Program() with { Command = "node\n--evil" }, []).Problem);
        Assert.Equal(McpProblem.FolderNotAbsolute, McpServerValidator.Validate(Program() with { WorkingFolder = "relative/dir" }, []).Problem);
        Assert.Equal(McpProblem.NameInvalid, McpServerValidator.Validate(Program() with { Environment = [new("A B", "x")] }, []).Problem);
        Assert.Equal(McpProblem.NameInvalid, McpServerValidator.Validate(Address() with { Headers = [new("Bad Header", "x")] }, []).Problem);
        Assert.Equal(McpProblem.ValueInvalid, McpServerValidator.Validate(Address() with { Headers = [new("X-Key", "a\r\nInjected: 1")] }, []).Problem);
        Assert.Equal(McpProblem.AddressInvalid, McpServerValidator.Validate(Address() with { Url = "not a url" }, []).Problem);
        Assert.Equal(McpProblem.AddressInvalid, McpServerValidator.Validate(Address() with { Url = "http://user:pw@127.0.0.1/" }, []).Problem);
        Assert.Equal(McpProblem.TimeoutOutOfRange, McpServerValidator.Validate(Program() with { CallTimeoutSeconds = 1 }, []).Problem);
        Assert.Equal(McpProblem.TimeoutOutOfRange, McpServerValidator.Validate(Program() with { CallTimeoutSeconds = 601 }, []).Problem);
    }

    [Fact]
    public void Labels_made_from_other_names_are_valid_and_free()
    {
        Assert.Equal("my-server-2", McpServerValidator.LabelFrom("My Server_2"));
        Assert.Equal("server", McpServerValidator.LabelFrom("???"));
        Assert.Equal(32, McpServerValidator.LabelFrom(new string('x', 80)).Length);
        Assert.Equal("a-3", McpServerValidator.FreeLabel("a", new HashSet<string> { "a", "a-2" }));
        Assert.True(McpServerValidator.IsLabel(McpServerValidator.FreeLabel(new string('x', 32), new HashSet<string> { new string('x', 32) })));
    }

    [Fact]
    public void A_secret_is_not_printed_by_the_entry_or_its_pairs()
    {
        var entry = Program();

        var printed = entry.ToString() + entry.Environment[0] + string.Join(",", entry.Environment.Select(p => p.ToString()));

        Assert.DoesNotContain("s3cret", printed);
        Assert.DoesNotContain("abc", printed);
        Assert.Contains("API_KEY", printed);
        Assert.Equal("node", entry.Where);
        Assert.Equal("http://127.0.0.1:8123/mcp", Address().Where);
    }
}

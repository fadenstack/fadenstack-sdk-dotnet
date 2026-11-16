using System.IO.Compression;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>A user's rights on the device: the export of their data, its deletion, and the server's limit on how long it is kept.</summary>
[Collection(PathsCollection.Name)]
public class DataRightsTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string TokenSecret = "token-secret-value";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-rights-" + Guid.NewGuid());

    public DataRightsTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests(); // sessions go to files only when the protector encrypts, on every OS
        SecretProtection.Use(new FakeEncryptingProtector());
    }

    public void Dispose()
    {
        SecretProtection.ResetForTests();
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private string Data(params string[] parts) => Path.Combine(new[] { _temporary, "data" }.Concat(parts).ToArray());

    private static AgentBuilder Plain(Gateway gateway) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(gateway.Server))
        .UseAgent(Gateway.Slug);

    private static TranscriptEntry Entry(string text) =>
        new() { Type = EntryTypes.UserMessage, TurnId = "t", Data = JsonDocument.Parse(JsonSerializer.Serialize(new { text })).RootElement.Clone() };

    /// <summary>Another agent's session and another user's, written the way their stores write them.</summary>
    private static async Task Seed(string user, string agent, string session, string text, bool approval = false)
    {
        await new FileSessionStore(FadenstackPaths.User(user, "sessions")).AppendAsync(agent, session, Entry(text));
        if (approval)
        {
            await new FileApprovalStore(FadenstackPaths.User(user, "approvals"))
                .AddAsync(agent, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));
        }
    }

    private void KeepAToken() => File.WriteAllText(Path.Combine(FadenstackPaths.Data("tokens"), "agent.bin"), TokenSecret);

    private static Dictionary<string, string> Unzip(MemoryStream zip)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public async Task The_export_holds_every_session_and_approval_of_the_user_decrypted_and_a_readme()
    {
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await agent.StartAsync();
        var mine = await agent.StartSessionAsync();
        await Seed("u-1", "other-agent", "s9", "hello from another agent", approval: true);
        await Seed("u-2", Gateway.Slug, "s1", "somebody else's secret");
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        KeepAToken();

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip, DataScope.AllApps); // every app's: another agent's too
        var files = Unzip(zip);

        Assert.Equal(new[]
        {
            "README.txt", "approvals/docs-assistant.json", "approvals/other-agent.json", "apps/docs-assistant.json",
            $"sessions/docs-assistant/{mine.Id}.json", "sessions/other-agent/s9.json",
        }, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Contains("hello from another agent", files["sessions/other-agent/s9.json"]);
        Assert.Contains(EntryTypes.SessionStart, files[$"sessions/docs-assistant/{mine.Id}.json"]);
        Assert.Contains("stamp", files["approvals/docs-assistant.json"]);
        Assert.Contains("2 tool(s)", files["README.txt"]);
        Assert.Contains("never exported", files["README.txt"]);
        Assert.DoesNotContain(files.Values, text => text.Contains("somebody else's secret") || text.Contains(TokenSecret));
    }

    [Fact]
    public async Task Without_a_principal_the_export_holds_the_shared_folders_of_an_older_sdk()
    {
        using var agent = Plain(new Gateway()).Build();
        var session = await agent.StartSessionAsync();
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        await Seed("u-2", Gateway.Slug, "s1", "somebody else's secret");

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip);
        var files = Unzip(zip);

        Assert.Equal(new[] { "README.txt", "approvals/docs-assistant.json", $"sessions/docs-assistant/{session.Id}.json" },
            files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(files.Values, text => text.Contains("somebody else's secret"));
    }

    /// <summary>A person's folder as the modules write it: preferences, language, skills, MCP, provenance, a key, and a stranger.</summary>
    private static void SeedPersonsFiles(string user)
    {
        void Write(string relative, string text)
        {
            var path = Path.Combine(FadenstackPaths.UserFolder(user), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("preferences/reasoning.json", """{"docs-assistant":"high"}""");
        Write("preferences/skills.json", """{"off":["quiet-skill"]}""");
        Write("language", "de");
        Write("skills/notes/SKILL.md", "---\nname: notes\ndescription: Keeps notes.\n---\n\nShort sentences.\n");
        Write("skills/notes/template.txt", "a template");
        Write("mcp/audit.jsonl", """{"event":"local_mcp.plain_http","state":"on"}""");
        Write("mcp/audit.jsonl.1", """{"event":"local_mcp.plain_http","state":"off"}""");
        Write("mcp/servers.dat.lock", "");
        Write("provenance/records/r1.json", """{"id":"r1","kind":"text"}""");
        Write("provenance/placements.jsonl", """{"record":"r1","app":"word"}""");
        Write("keys/provenance-signing.key", "s3cret-signing-key");
        Write("mcp/.servers.dat.0123.tmp", "s3cret-in-flight");
        Write("unknown.bin", "s3cret-unknown");
        var list = """
            {"version":1,"servers":[
              {"label":"notes","enabled":true,"command":"node","arguments":["server.js","--token=s3cret-arg"],"workingFolder":"/work",
               "environment":[{"name":"API_KEY","value":"s3cret-env"}],"headers":[],"callTimeoutSeconds":90,"origin":"Cursor",
               "trusted":true,"toolClasses":{"find":"read"}},
              {"label":"web","enabled":false,"url":"https://anna:s3cret-pw@mcp.example.com/mcp?token=s3cret-query#s3cret-frag",
               "headers":[{"name":"Authorization","value":"Bearer s3cret-header"}],"futureSecret":"s3cret-future","plainHttpInLan":true}
            ]}
            """;
        var path = Path.Combine(FadenstackPaths.User(user, "mcp"), "servers.dat");
        File.WriteAllBytes(path, SecretProtection.Default.Protect(System.Text.Encoding.UTF8.GetBytes(list)));
    }

    [Fact]
    public async Task The_export_holds_all_the_deletion_removes_without_a_key_or_a_secret_value()
    {
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await agent.StartAsync();
        SeedPersonsFiles("u-1");
        SeedPersonsFiles("u-2");

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip);
        var files = Unzip(zip);

        Assert.Equal(new[]
        {
            "README.txt", "apps/docs-assistant.json", "language", "mcp/audit.jsonl", "mcp/audit.jsonl.1", "mcp/servers.json",
            "preferences/reasoning.json", "preferences/skills.json", "provenance/placements.jsonl", "provenance/records/r1.json",
            "skills/notes/SKILL.md", "skills/notes/template.txt",
        }, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(files.Values, text => text.Contains("s3cret") || text.Contains("anna"));
        Assert.Equal("de", files["language"]);
        Assert.Contains("quiet-skill", files["preferences/skills.json"]);

        var servers = JsonDocument.Parse(files["mcp/servers.json"]).RootElement.GetProperty("servers");
        var notes = servers[0];
        Assert.Equal(("notes", "node", 2, "API_KEY"), (notes.GetProperty("label").GetString(), notes.GetProperty("command").GetString(),
            notes.GetProperty("argumentCount").GetInt32(), notes.GetProperty("environmentNames")[0].GetString()));
        Assert.Equal("read", notes.GetProperty("toolClasses").GetProperty("find").GetString());
        Assert.True(notes.GetProperty("trusted").GetBoolean());
        var web = servers[1];
        Assert.Equal("https://mcp.example.com/mcp", web.GetProperty("url").GetString());
        Assert.Equal("Authorization", web.GetProperty("headerNames")[0].GetString());
        Assert.False(web.TryGetProperty("futureSecret", out _)); // a key the export does not know stays out
        Assert.True(web.GetProperty("plainHttpInLan").GetBoolean());

        var readme = files["README.txt"];
        Assert.Contains("2 MCP server(s)", readme);
        Assert.Contains("signs your provenance records", readme);
        Assert.Contains("unknown.bin", readme);
        Assert.DoesNotContain("servers.dat.lock", readme);
    }

    [Fact]
    public async Task Without_a_principal_the_export_holds_the_shared_preferences_too()
    {
        using var agent = Plain(new Gateway()).Build();
        await agent.StartAsync();
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("preferences"), "reasoning.json"), """{"docs-assistant":"low"}""");
        SeedPersonsFiles("u-2");

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip);
        var files = Unzip(zip);

        Assert.Equal(new[] { "README.txt", "preferences/reasoning.json" }, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Contains("low", files["preferences/reasoning.json"]);
    }

    [Fact]
    public async Task A_host_store_is_exported_for_this_agent_only_and_the_destination_stays_open()
    {
        var sessions = new MemorySessionStore();
        using var agent = new Gateway { Principal = Principal }.Builder(sessions).Build();
        await sessions.AppendAsync(Gateway.Slug, "s1", Entry("mine"));
        await sessions.AppendAsync("other-agent", "s2", Entry("not listed"));

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip);

        Assert.True(zip.CanWrite);
        Assert.Equal(new[] { "README.txt", "sessions/docs-assistant/s1.json" }, Unzip(zip).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Deleting_removes_the_users_folder_only_and_the_next_turn_starts_empty()
    {
        var gateway = new Gateway { Principal = Principal }.Says("Hello.").Says("Hello again.");
        using var agent = Plain(gateway).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        await Seed("u-1", "other-agent", "s9", "also mine");
        await Seed("u-2", Gateway.Slug, "s1", "somebody else's");
        KeepAToken();

        var deletion = await agent.DeleteMyDataAsync(DataScope.AllApps); // every app's: the whole folder

        Assert.False(deletion.HostStoresLeft); // the SDK's own stores: nothing left anywhere
        Assert.False(Directory.Exists(Data("users", "u-1")));
        Assert.True(File.Exists(Data("users", "u-2", "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.Equal(TokenSecret, File.ReadAllText(Data("tokens", "agent.bin")));
        Assert.Empty(await agent.ListSessionsAsync());
        Assert.Empty(await agent.ApprovalStore.ListAsync(Gateway.Slug));

        var next = await agent.StartSessionAsync();
        var result = await next.SendAsync("Hi again");
        Assert.Equal("Hello again.", result.Text);
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        Assert.Single(await agent.ListSessionsAsync());
        Assert.Single(await agent.ApprovalStore.ListAsync(Gateway.Slug));
    }

    [Fact]
    public async Task Deleting_says_so_when_the_host_passed_its_own_store_and_leaves_it_to_the_host()
    {
        // sdk/46 D-1: a host's store may hold other people's chats, so the SDK does not empty it -- but it must not let the
        // host believe the person's data gone.
        var sessions = new MemorySessionStore();
        using var agent = new Gateway { Principal = Principal }.Builder(sessions).Build();
        await sessions.AppendAsync(Gateway.Slug, "s1", Entry("mine"));

        var deletion = await agent.DeleteMyDataAsync();

        Assert.True(deletion.HostSessionStoreLeft);
        Assert.True(deletion.HostApprovalStoreLeft); // the test gateway's builder passes an approval store of its own too
        Assert.True(deletion.HostStoresLeft);
        Assert.Single(await sessions.ListAsync(Gateway.Slug));
    }

    [Fact]
    public async Task Without_a_principal_deleting_removes_the_shared_folders_only()
    {
        using var agent = Plain(new Gateway()).Build();
        await agent.StartSessionAsync();
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        await Seed("u-2", Gateway.Slug, "s1", "somebody else's");
        KeepAToken();

        await agent.DeleteMyDataAsync(DataScope.AllApps);

        Assert.False(Directory.Exists(Data("sessions")));
        Assert.False(Directory.Exists(Data("approvals")));
        Assert.True(File.Exists(Data("users", "u-2", "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.True(File.Exists(Data("tokens", "agent.bin")));
        Assert.Empty(await agent.ListSessionsAsync());
    }

    [Fact]
    public async Task Deleting_never_goes_outside_the_data_root()
    {
        Directory.CreateDirectory(Data("users"));
        var outside = Path.Combine(_temporary, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");

        Assert.Throws<ArgumentException>(() => FadenstackPaths.DeleteUnderData(outside));
        Assert.Throws<ArgumentException>(() => FadenstackPaths.DeleteUnderData(Data("..", "outside")));
        Assert.Throws<ArgumentException>(() => FadenstackPaths.DeleteUnderData(FadenstackPaths.DataRoot));
        Assert.Throws<ArgumentException>(() => FadenstackPaths.DeleteUnderData(Data("users") + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".."));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(Data("users")));
        Assert.False(FadenstackPaths.DeleteUnderData(Data("users", "nobody")));
    }

    /// <summary>A hostile user id cannot name a folder outside the users folder either.</summary>
    [Fact]
    public async Task A_user_id_with_path_parts_is_deleted_as_one_folder_below_users()
    {
        using var agent = Plain(new Gateway { Principal = """{"tenant_id":"t","user_id":"../..","agent_id":"a"}""" }).Build();
        await agent.StartSessionAsync();
        Assert.True(Directory.Exists(Data("users", "_____")));

        await agent.DeleteMyDataAsync();

        Assert.False(Directory.Exists(Data("users", "_____")));
        Assert.True(Directory.Exists(Data()));
    }

    private async Task<int> SessionsAfter(string? server, int count, SessionRetention? host)
    {
        var builder = Plain(new Gateway { LocalRetention = server });
        using var agent = (host is null ? builder : builder.UseSessionRetention(host)).Build();
        for (var i = 0; i < count; i++)
        {
            await agent.StartSessionAsync();
        }
        return (await agent.ListSessionsAsync()).Count;
    }

    [Theory]
    [InlineData("""{"max_sessions":2}""", 4, 2)] // the server's is stricter
    [InlineData("""{"max_sessions":9}""", 3, 3)] // the host's is stricter
    [InlineData("""{"max_sessions":0}""", 3, 3)] // invalid values are ignored
    [InlineData("""{"max_sessions":-4,"max_age_days":"x"}""", 3, 3)]
    [InlineData("""{"max_sessions":1.5}""", 3, 3)]
    [InlineData("""[1]""", 3, 3)]
    [InlineData(null, 3, 3)] // absent: the host's
    public async Task The_stricter_session_limit_of_host_and_server_applies(string? server, int hostMax, int expected) =>
        Assert.Equal(expected, await SessionsAfter(server, 5, new SessionRetention(MaxSessions: hostMax)));

    [Fact]
    public async Task Without_either_the_defaults_apply()
    {
        Assert.Equal(5, await SessionsAfter(null, 5, null));
    }

    [Theory]
    [InlineData(50, 90, null, null, 50, 90)]
    [InlineData(50, 90, 10, 30, 10, 30)]
    [InlineData(5, 20, 10, 30, 5, 20)]
    [InlineData(50, 20, 10, null, 10, 20)]
    public void Each_limit_is_the_stricter_of_host_and_server(int hostSessions, int hostDays, int? serverSessions, int? serverDays, int sessions, int days)
    {
        var server = serverSessions is null && serverDays is null ? null : new LocalRetention { MaxSessions = serverSessions, MaxAgeDays = serverDays };
        Assert.Equal(new SessionRetention(sessions, days), new SessionRetention(hostSessions, hostDays).Within(server));
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(50, -1)]
    public void The_hosts_limits_must_be_positive(int sessions, int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FadenstackAgent.CreateBuilder().UseSessionRetention(new SessionRetention(sessions, days)));
}

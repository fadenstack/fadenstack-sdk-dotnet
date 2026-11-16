using System.IO.Compression;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// "Remove my data…" takes only the calling app's data; the shared data goes only when no other Fadenstack app on the device
/// uses the person's data; "Remove all" takes everything; the export follows the same scope (owner, 16 Nov; GDPR Art. 15,
/// 17, 20). App A is the agent under test ("Word add-in", agent docs-assistant); app B ("Desktop agent", agent
/// desktop-assistant) is another process, written to disk as it writes.
/// </summary>
[Collection(PathsCollection.Name)]
public sealed class MyDataScopeTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string AppA = "Word add-in";
    private const string AppB = "Desktop agent";
    private const string AgentB = "desktop-assistant";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-mydata-" + Guid.NewGuid().ToString("N"));

    public MyDataScopeTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
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

    private static string Person => FadenstackPaths.UserFolder("u-1");

    private static string In(params string[] parts) => Path.Combine(new[] { Person }.Concat(parts).ToArray());

    private static FadenstackAgent AgentA() => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
            o => o.HttpClient = new HttpClient(new Gateway { Principal = Principal }.Server))
        .UseAgent(Gateway.Slug)
        .DescribeApp(new AppDescription { Name = AppA, Version = "1.0" })
        .Build();

    private static TranscriptEntry Entry(string text) =>
        new() { Type = EntryTypes.UserMessage, TurnId = "t", Data = JsonDocument.Parse(JsonSerializer.Serialize(new { text })).RootElement.Clone() };

    /// <summary>A's own data: a session, an "Allow always", a thinking level (beside B's, when B is there).</summary>
    private static async Task<string> SeedA(FadenstackAgent agent)
    {
        var session = await agent.StartSessionAsync();
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        return session.Id;
    }

    /// <summary>B as it writes its data from its own process: its record (unless it is on an older SDK), a session, approvals.</summary>
    private static async Task SeedB(bool record = true, bool approval = true, string agent = AgentB, IReadOnlyList<string>? recordAgents = null)
    {
        if (record)
        {
            foreach (var each in recordAgents ?? new[] { agent })
            {
                MyDataApps.TryRecord(Person, AppB, each, DateTimeOffset.UtcNow);
            }
        }
        await new FileSessionStore(In("sessions")).AppendAsync(agent, "b1", Entry("B's chat"));
        if (approval)
        {
            await new FileApprovalStore(In("approvals")).AddAsync(agent, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>The person's shared data: skills, the MCP list, a key, provenance, the person's settings, both thinking levels.</summary>
    private static void SeedShared()
    {
        void Write(string relative, string text)
        {
            var path = In(relative.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("preferences/reasoning.json", $$"""{"{{Gateway.Slug}}":"high","{{AgentB}}":"low"}""");
        Write("preferences/web-search.json", """{"provider":"searxng"}""");
        Write("skills/notes/SKILL.md", "---\nname: notes\ndescription: Keeps notes.\n---\n");
        Write("mcp/audit.jsonl", """{"event":"local_mcp.plain_http","state":"on"}""");
        Write("keys/provenance-signing.key", "a-key");
        Write("provenance/records/r1.json", """{"id":"r1"}""");
        Write("language", "de");
    }

    private static Dictionary<string, string> Reasoning() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(In("preferences", "reasoning.json")))!;

    private static string[] SharedFiles() => new[]
    {
        In("preferences", "web-search.json"), In("skills", "notes", "SKILL.md"), In("mcp", "audit.jsonl"),
        In("keys", "provenance-signing.key"), In("provenance", "records", "r1.json"), In("language"),
    };

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
    public async Task By_default_the_removal_takes_this_apps_data_and_leaves_the_other_apps_and_the_shared_data()
    {
        using var agent = AgentA();
        var mine = await SeedA(agent);
        await SeedB();
        SeedShared();

        var deletion = await agent.DeleteMyDataAsync(); // the default: this app

        Assert.Equal(DataScope.ThisApp, deletion.Scope);
        Assert.Equal(new[] { AppB }, deletion.OtherApps);
        Assert.True(deletion.SharedDataKept);
        Assert.Empty(deletion.AgentsKept);
        // A's data is gone ...
        Assert.False(Directory.Exists(In("sessions", Gateway.Slug)));
        Assert.False(File.Exists(In("approvals", Gateway.Slug + ".json")));
        Assert.False(File.Exists(MyDataApps.PathOf(Person, AppA)));
        Assert.Equal(new[] { AgentB }, Reasoning().Keys.ToArray());
        Assert.Empty(await agent.ListSessionsAsync());
        Assert.DoesNotContain(mine, Directory.GetFiles(In("sessions"), "*", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension));
        // ... and B's, and the shared data, stay
        Assert.True(File.Exists(In("sessions", AgentB, "b1.jsonl")));
        Assert.True(File.Exists(In("approvals", AgentB + ".json")));
        Assert.True(File.Exists(In("approvals", FileApprovalStore.SealKeyName))); // B's approvals still need it
        Assert.True(File.Exists(MyDataApps.PathOf(Person, AppB)));
        Assert.All(SharedFiles(), f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public async Task When_no_other_app_uses_the_data_this_apps_removal_takes_the_shared_data_and_the_folder()
    {
        using var agent = AgentA();
        await SeedA(agent);
        SeedShared();
        File.WriteAllText(In("preferences", "reasoning.json"), $$"""{"{{Gateway.Slug}}":"high"}""");

        var deletion = await agent.DeleteMyDataAsync(DataScope.ThisApp);

        Assert.Empty(deletion.OtherApps);
        Assert.False(deletion.SharedDataKept);
        Assert.False(Directory.Exists(Person));
    }

    [Fact]
    public async Task The_last_app_removing_its_data_takes_the_shared_data_and_the_folder()
    {
        // B after A is gone: B's removal now finds no other app.
        MyDataApps.TryRecord(Person, AppB, AgentB, DateTimeOffset.UtcNow);
        await new FileSessionStore(In("sessions")).AppendAsync(AgentB, "b1", Entry("B's chat"));
        SeedShared();
        File.WriteAllText(In("preferences", "reasoning.json"), $$"""{"{{AgentB}}":"low"}""");

        var plan = MyDataApps.Plan(Person, AppB, AgentB);
        MyDataApps.Remove(Person, AppB, plan);

        Assert.True(plan.SharedGoes);
        Assert.False(Directory.Exists(Person));
    }

    [Fact]
    public async Task An_app_on_an_older_sdk_without_a_record_still_keeps_the_shared_data()
    {
        using var agent = AgentA();
        await SeedA(agent);
        await SeedB(record: false, agent: "excel-assistant");
        SeedShared();
        File.WriteAllText(In("preferences", "reasoning.json"), $$"""{"{{Gateway.Slug}}":"high"}"""); // no desktop-assistant here

        var apps = await agent.MyDataAppsAsync();
        var deletion = await agent.DeleteMyDataAsync(DataScope.ThisApp);

        var older = Assert.Single(apps, a => !a.IsThisApp);
        Assert.Equal(("excel-assistant", false), (older.Name, older.Recorded));
        Assert.Equal(new[] { "excel-assistant" }, deletion.OtherApps);
        Assert.True(File.Exists(In("sessions", "excel-assistant", "b1.jsonl")));
        Assert.All(SharedFiles(), f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public async Task An_agent_another_app_also_uses_keeps_its_data()
    {
        using var agent = AgentA();
        await SeedA(agent);
        await SeedB(recordAgents: new[] { AgentB, Gateway.Slug }); // B runs docs-assistant too

        var deletion = await agent.DeleteMyDataAsync(DataScope.ThisApp);

        Assert.Equal(new[] { Gateway.Slug }, deletion.AgentsKept);
        Assert.Single(await agent.ListSessionsAsync());
        Assert.True(File.Exists(In("approvals", Gateway.Slug + ".json")));
        Assert.False(File.Exists(MyDataApps.PathOf(Person, AppA))); // A's record goes all the same
    }

    [Fact]
    public async Task The_seal_key_goes_with_the_last_approvals()
    {
        using var agent = AgentA();
        await SeedA(agent);
        await SeedB(approval: false); // B keeps chats, no "Allow always"

        await agent.DeleteMyDataAsync(DataScope.ThisApp);

        Assert.False(File.Exists(In("approvals", FileApprovalStore.SealKeyName)));
        Assert.True(File.Exists(In("sessions", AgentB, "b1.jsonl")));
    }

    [Fact]
    public async Task Remove_all_takes_every_apps_data_and_the_list_names_them_first()
    {
        using var agent = AgentA();
        await SeedA(agent);
        await SeedB();
        await SeedB(record: false, agent: "excel-assistant", approval: false);
        SeedShared();

        var apps = await agent.MyDataAppsAsync();
        var deletion = await agent.DeleteMyDataAsync(DataScope.AllApps);

        Assert.Equal(new[] { (AppA, true, true), (AppB, false, true), ("excel-assistant", false, false) },
            apps.Select(a => (a.Name, a.IsThisApp, a.Recorded)).ToArray());
        Assert.Equal(new[] { Gateway.Slug }, apps[0].Agents);
        Assert.Equal(DataScope.AllApps, deletion.Scope);
        Assert.False(Directory.Exists(Person));
    }

    [Fact]
    public async Task The_export_of_this_app_holds_exactly_what_its_removal_takes_and_says_what_stayed()
    {
        using var agent = AgentA();
        var mine = await SeedA(agent);
        await SeedB();
        SeedShared();

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip, DataScope.ThisApp);
        var files = Unzip(zip);

        Assert.Equal(new[]
        {
            "README.txt", "approvals/docs-assistant.json", "apps/Word add-in.json", "preferences/reasoning.json",
            $"sessions/docs-assistant/{mine}.json",
        }, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { Gateway.Slug }, JsonSerializer.Deserialize<Dictionary<string, string>>(files["preferences/reasoning.json"])!.Keys);
        Assert.DoesNotContain(files.Values, text => text.Contains("B's chat"));
        Assert.Contains("Scope: the data of Word add-in only", files["README.txt"]);
        Assert.Contains(AppB, files["README.txt"]);
    }

    [Fact]
    public async Task The_export_of_all_apps_holds_every_apps_data_and_the_shared_data()
    {
        using var agent = AgentA();
        await SeedA(agent);
        await SeedB();
        SeedShared();

        var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip, DataScope.AllApps);
        var files = Unzip(zip);

        Assert.Contains("sessions/desktop-assistant/b1.json", files.Keys);
        Assert.Contains("approvals/desktop-assistant.json", files.Keys);
        Assert.Contains("apps/Desktop agent.json", files.Keys);
        Assert.Contains("apps/Word add-in.json", files.Keys);
        Assert.Contains("skills/notes/SKILL.md", files.Keys);
        Assert.Contains("Scope: all of your Fadenstack data on this device", files["README.txt"]);
        Assert.DoesNotContain(files.Keys, k => k.StartsWith("keys/", StringComparison.Ordinal)); // never a key
    }

    [Fact]
    public async Task The_record_names_the_app_and_its_agents_and_no_content()
    {
        using var agent = AgentA();
        await agent.StartAsync();

        var record = JsonDocument.Parse(File.ReadAllText(MyDataApps.PathOf(Person, AppA))).RootElement;

        Assert.Equal(AppA, record.GetProperty("app").GetString());
        Assert.Equal(new[] { Gateway.Slug }, record.GetProperty("agents").EnumerateArray().Select(a => a.GetString()).ToArray());
        Assert.Equal(new[] { "agents", "app", "firstSeen", "lastSeen" },
            record.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task From_a_gateway_that_names_no_user_the_export_holds_exactly_what_the_removal_takes()
    {
        using var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
                o => o.HttpClient = new HttpClient(new Gateway { Principal = null }.Server))
            .UseAgent(Gateway.Slug)
            .DescribeApp(new AppDescription { Name = AppA, Version = "1.0" })
            .Build();
        await SeedA(agent);
        string Shared(params string[] parts) => Path.Combine(new[] { FadenstackPaths.DataRoot }.Concat(parts).ToArray());
        await new FileSessionStore(Shared("sessions")).AppendAsync(AgentB, "b1", Entry("B's chat"));
        Directory.CreateDirectory(Shared("preferences"));
        File.WriteAllText(Shared("preferences", "reasoning.json"), $$"""{"{{Gateway.Slug}}":"high","{{AgentB}}":"low"}""");
        File.WriteAllText(Shared("preferences", "web-search.json"), """{"provider":"searxng"}""");

        using var zip = new MemoryStream();
        await agent.ExportMyDataAsync(zip); // the default: this app
        var files = Unzip(zip);

        Assert.Contains(files.Keys, k => k.StartsWith($"sessions/{Gateway.Slug}/", StringComparison.Ordinal));
        Assert.Contains($"approvals/{Gateway.Slug}.json", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.StartsWith($"sessions/{AgentB}/", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Values, v => v.Contains("B's chat"));
        Assert.Equal(new[] { Gateway.Slug },
            JsonSerializer.Deserialize<Dictionary<string, string>>(files["preferences/reasoning.json"])!.Keys.ToArray());
        Assert.DoesNotContain("preferences/web-search.json", files.Keys);
        Assert.Contains("The other agents' chats and the settings this device shares", files["README.txt"]);
        Assert.DoesNotContain("()", files["README.txt"]);

        var deletion = await agent.DeleteMyDataAsync(); // the default: this app

        Assert.Equal(DataScope.ThisApp, deletion.Scope);
        Assert.Empty(deletion.OtherApps);
        Assert.False(Directory.Exists(Shared("sessions", Gateway.Slug)));
        Assert.False(File.Exists(Shared("approvals", Gateway.Slug + ".json")));
        Assert.True(Directory.Exists(Shared("sessions", AgentB)));
        Assert.Equal(new[] { AgentB },
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Shared("preferences", "reasoning.json")))!.Keys.ToArray());
        Assert.True(File.Exists(Shared("preferences", "web-search.json")));
        Assert.False(Directory.Exists(FadenstackPaths.UserFolder("u-1"))); // no person, no record
    }
}

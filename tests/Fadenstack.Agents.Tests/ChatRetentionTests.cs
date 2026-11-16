using System.IO.Compression;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Storage;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>How long old chats are kept: the person's choice in standalone, the server's in governed mode, the machine policy over both.</summary>
[Collection(PathsCollection.Name)]
public class ChatRetentionTests : IDisposable
{
    private const string Definition = """
        {"slug":"docs-assistant","name":"Docs assistant","version":{"instructions":"You are the docs assistant.","host_tool_contract":[],"ui":{}}}
        """;

    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string Slug = "docs-assistant";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-retention-" + Guid.NewGuid());

    public ChatRetentionTests()
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

    private static AgentBuilder Standalone(MachinePolicy? policy = null)
    {
        var builder = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "qwen3:14b", Dialect = EndpointDialect.Ollama, Handler = new OpenAiFake() })
            .UseLocalProfile(AgentDefinition.FromJson(Definition));
        return policy is null ? builder : builder.UseMachinePolicy(policy);
    }

    private static AgentBuilder Governed(Gateway gateway) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(gateway.Server))
        .UseAgent(Gateway.Slug);

    private static string Folder(string user, string name) => FadenstackPaths.User(user, name);

    /// <summary>Saves <paramref name="count"/> chats, the newest first one minute apart from <paramref name="newest"/> ago, and returns their ids (newest first).</summary>
    private static async Task<List<string>> Seed(string user, int count, TimeSpan newest)
    {
        var store = new FileSessionStore(Folder(user, "sessions"), retention: new SessionRetention(100_000, 100_000));
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = "c" + i.ToString("D3");
            await store.AppendAsync(Slug, id, new TranscriptEntry { Type = EntryTypes.SessionStart, Data = JsonDocument.Parse("{}").RootElement });
            File.SetLastWriteTimeUtc(Path.Combine(Folder(user, "sessions"), Slug, id + ".jsonl"), DateTime.UtcNow - newest - TimeSpan.FromMinutes(i));
            ids.Add(id);
        }
        return ids;
    }

    private static void WriteChoice(string user, string json)
    {
        Directory.CreateDirectory(Folder(user, "preferences"));
        File.WriteAllText(Path.Combine(Folder(user, "preferences"), "retention.json"), json);
    }

    private static async Task<List<string>> Kept(FadenstackAgent agent) =>
        (await agent.ListSessionsAsync()).Select(s => s.Id).OrderBy(s => s).ToList();

    // -- standalone: the person chooses ---------------------------------------------------------------------

    [Fact]
    public async Task Standalone_defaults_to_on_with_90_days_and_50_chats()
    {
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.Equal(new ChatRetentionState(ChatRetentionControl.Person, true, 90, 50), agent.ChatRetention);
        Assert.True(agent.ChatRetention.CanChange);
    }

    [Fact]
    public async Task The_apps_limits_give_the_default()
    {
        using var agent = Standalone().UseSessionRetention(new SessionRetention(MaxSessions: 20, MaxAgeDays: 30)).Build();
        await agent.StartAsync();
        Assert.Equal((true, 30, 20), (agent.ChatRetention.AutoDelete, agent.ChatRetention.MaxAgeDays, agent.ChatRetention.MaxSessions));
    }

    [Fact]
    public async Task Pruning_runs_at_start_and_keeps_the_newest_fifty_not_older_than_ninety_days()
    {
        await Seed("standalone", 60, TimeSpan.FromHours(1)); // 60 recent chats
        var old = await Seed("standalone", 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(Folder("standalone", "sessions"), Slug, "ancient.jsonl"), "");
        File.SetLastWriteTimeUtc(Path.Combine(Folder("standalone", "sessions"), Slug, "ancient.jsonl"), DateTime.UtcNow.AddDays(-100));
        Assert.Empty(old);

        using var agent = Standalone().Build();
        await agent.StartAsync();

        var kept = await Kept(agent);
        Assert.Equal(50, kept.Count);
        Assert.DoesNotContain("c050", kept);
        Assert.Contains("c049", kept);
        Assert.False(File.Exists(Path.Combine(Folder("standalone", "sessions"), Slug, "ancient.jsonl")));
    }

    [Fact]
    public async Task An_idle_profile_ages_out_at_start()
    {
        await Seed("standalone", 3, TimeSpan.FromDays(120));
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.Empty(await Kept(agent));
    }

    [Fact]
    public async Task Off_keeps_everything_at_start_and_when_a_chat_is_created()
    {
        WriteChoice("standalone", """{"autoDelete":false,"maxAgeDays":30,"maxSessions":5}""");
        await Seed("standalone", 12, TimeSpan.FromDays(200));
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.False(agent.ChatRetention.AutoDelete);
        Assert.Equal(12, (await Kept(agent)).Count);

        await agent.StartSessionAsync();
        await agent.StartSessionAsync();
        Assert.Equal(14, (await agent.ListSessionsAsync()).Count);
    }

    [Fact]
    public async Task A_change_persists_for_the_person_and_survives_a_new_agent()
    {
        using (var first = Standalone().Build())
        {
            var state = await first.SetChatRetentionAsync(new ChatRetentionChoice(false, 7, 9));
            Assert.Equal(new ChatRetentionState(ChatRetentionControl.Person, false, 7, 9), state);
        }
        Assert.True(File.Exists(Path.Combine(Folder("standalone", "preferences"), "retention.json")));

        using var second = Standalone().Build();
        await second.StartAsync();
        Assert.Equal((false, 7, 9), (second.ChatRetention.AutoDelete, second.ChatRetention.MaxAgeDays, second.ChatRetention.MaxSessions));
    }

    [Fact]
    public async Task Turning_it_on_prunes_at_once()
    {
        WriteChoice("standalone", """{"autoDelete":false,"maxAgeDays":90,"maxSessions":50}""");
        await Seed("standalone", 10, TimeSpan.FromHours(1));
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.Equal(10, (await Kept(agent)).Count);

        await agent.SetChatRetentionAsync(new ChatRetentionChoice(true, 90, 4));

        Assert.Equal(new[] { "c000", "c001", "c002", "c003" }, await Kept(agent));
    }

    [Fact]
    public async Task Lowering_a_limit_prunes_at_once()
    {
        await Seed("standalone", 6, TimeSpan.FromDays(10));
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.Equal(6, (await Kept(agent)).Count);
        await agent.SetChatRetentionAsync(new ChatRetentionChoice(true, 5, 50)); // all older than 5 days
        Assert.Empty(await Kept(agent));
    }

    [Fact]
    public async Task The_chat_that_is_open_is_never_deleted()
    {
        var ids = await Seed("standalone", 5, TimeSpan.FromDays(200));
        using var agent = Standalone().Build();
        // Start first with pruning off so the old chats are still there to open.
        WriteChoice("standalone", """{"autoDelete":false,"maxAgeDays":90,"maxSessions":50}""");
        await agent.StartAsync();
        var open = await agent.OpenSessionAsync(ids[2]);

        await agent.SetChatRetentionAsync(new ChatRetentionChoice(true, 30, 1));

        Assert.Equal(new[] { open.Id }, await Kept(agent));
        GC.KeepAlive(open);
    }

    [Fact]
    public async Task A_new_chat_counts_among_the_chats_kept()
    {
        WriteChoice("standalone", """{"autoDelete":true,"maxAgeDays":90,"maxSessions":3}""");
        await Seed("standalone", 3, TimeSpan.FromHours(1));
        using var agent = Standalone().Build();
        await agent.StartAsync();
        var created = await agent.StartSessionAsync();
        var kept = await Kept(agent);
        Assert.Equal(3, kept.Count);
        Assert.Contains(created.Id, kept);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(3651, 50)]
    [InlineData(90, 0)]
    [InlineData(90, 10001)]
    [InlineData(-1, -1)]
    public void Invalid_limits_are_refused(int days, int chats)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatRetentionChoice(true, days, chats));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatRetentionChoice(false, days, chats));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3650, 10000)]
    public void The_bounds_themselves_are_allowed(int days, int chats) => Assert.True(new ChatRetentionChoice(true, days, chats).MaxSessions == chats);

    [Theory]
    [InlineData("""{"autoDelete":false,"maxAgeDays":0,"maxSessions":5}""")]
    [InlineData("""{"autoDelete":false,"maxAgeDays":10,"maxSessions":99999}""")]
    [InlineData("""{"autoDelete":"no","maxAgeDays":10,"maxSessions":5}""")]
    [InlineData("""{"autoDelete":false}""")]
    [InlineData("not json")]
    public async Task A_damaged_stored_choice_counts_as_none(string json)
    {
        WriteChoice("standalone", json);
        using var agent = Standalone().Build();
        await agent.StartAsync();
        Assert.Equal(new ChatRetentionState(ChatRetentionControl.Person, true, 90, 50), agent.ChatRetention);
    }

    // -- governed: the server decides -----------------------------------------------------------------------

    [Fact]
    public async Task Governed_ignores_the_persons_choice_and_follows_the_stricter_server_limit()
    {
        WriteChoice("u-1", """{"autoDelete":false,"maxAgeDays":3650,"maxSessions":10000}""");
        await Seed("u-1", 8, TimeSpan.FromHours(1));
        using var agent = Governed(new Gateway { Principal = Principal, LocalRetention = """{"max_sessions":3}""" }).Build();
        await agent.StartAsync();

        Assert.Equal(new ChatRetentionState(ChatRetentionControl.Organisation, true, 90, 3, OrganisationSetsLimit: true), agent.ChatRetention);
        Assert.False(agent.ChatRetention.CanChange);
        Assert.Equal(3, (await Kept(agent)).Count);

        var after = await agent.SetChatRetentionAsync(new ChatRetentionChoice(false, 1, 1));
        Assert.Equal(agent.ChatRetention, after);
        Assert.Contains("\"autoDelete\":false,\"maxAgeDays\":3650", File.ReadAllText(Path.Combine(Folder("u-1", "preferences"), "retention.json"))); // untouched
        Assert.Equal(3, (await Kept(agent)).Count);
    }

    [Fact]
    public async Task Governed_takes_the_apps_limit_when_it_is_stricter_and_says_when_the_server_sets_none()
    {
        using var agent = Governed(new Gateway { Principal = Principal }).UseSessionRetention(new SessionRetention(MaxSessions: 10, MaxAgeDays: 20)).Build();
        await agent.StartAsync();
        Assert.Equal(new ChatRetentionState(ChatRetentionControl.Organisation, true, 20, 10, OrganisationSetsLimit: false), agent.ChatRetention);

        using var stricter = Governed(new Gateway { Principal = Principal, LocalRetention = """{"max_sessions":50,"max_age_days":7}""" })
            .UseSessionRetention(new SessionRetention(MaxSessions: 10, MaxAgeDays: 20)).Build();
        await stricter.StartAsync();
        Assert.Equal((7, 10), (stricter.ChatRetention.MaxAgeDays, stricter.ChatRetention.MaxSessions));
    }

    [Fact]
    public async Task One_persons_choice_is_not_anothers()
    {
        using (var standalone = Standalone().Build())
        {
            await standalone.SetChatRetentionAsync(new ChatRetentionChoice(false, 5, 5));
        }
        using var governed = Governed(new Gateway { Principal = Principal }).Build();
        await governed.StartAsync();
        Assert.False(File.Exists(Path.Combine(Folder("u-1", "preferences"), "retention.json")));
        Assert.True(File.Exists(Path.Combine(Folder("standalone", "preferences"), "retention.json")));
    }

    // -- the machine policy ---------------------------------------------------------------------------------

    private static MachinePolicy Fixed(string json) => MachinePolicy.FromJson("{\"chatRetention\":" + json + "}");

    [Fact]
    public async Task The_machine_policy_fixes_it_and_the_persons_choice_cannot_change_it()
    {
        WriteChoice("standalone", """{"autoDelete":false,"maxAgeDays":90,"maxSessions":50}""");
        await Seed("standalone", 6, TimeSpan.FromHours(1));
        using var agent = Standalone(Fixed("""{"autoDelete":true,"maxAgeDays":30,"maxSessions":4}""")).Build();
        await agent.StartAsync();

        Assert.Equal(new ChatRetentionState(ChatRetentionControl.MachinePolicy, true, 30, 4), agent.ChatRetention);
        Assert.False(agent.ChatRetention.CanChange);
        Assert.Equal(4, (await Kept(agent)).Count);

        await agent.SetChatRetentionAsync(new ChatRetentionChoice(true, 90, 50));
        Assert.Equal(4, agent.ChatRetention.MaxSessions);
        Assert.Contains("\"autoDelete\":false", File.ReadAllText(Path.Combine(Folder("standalone", "preferences"), "retention.json")));
    }

    [Fact]
    public async Task The_machine_policy_can_turn_pruning_off()
    {
        await Seed("standalone", 6, TimeSpan.FromDays(400));
        using var agent = Standalone(Fixed("""{"autoDelete":false}""")).Build();
        await agent.StartAsync();
        Assert.Equal(6, (await Kept(agent)).Count);
    }

    [Fact]
    public void The_policy_entry_reads_with_defaults_and_warns_about_what_it_does_not_understand()
    {
        var full = Fixed("""{"autoDelete":true,"maxAgeDays":30,"maxSessions":100}""");
        Assert.Equal(new ChatRetentionChoice(true, 30, 100), full.ChatRetention);
        Assert.Empty(full.Warnings);
        Assert.Contains("chatRetention", full.Locked);
        Assert.Null(MachinePolicy.None.ChatRetention);
        Assert.Equal(new ChatRetentionChoice(true, 90, 50), Fixed("{}").ChatRetention);

        var bad = Fixed("""{"autoDelete":"yes","maxAgeDays":0,"maxSessions":2.5,"colour":1}""");
        Assert.Equal(ChatRetentionChoice.Default, bad.ChatRetention);
        Assert.Equal(4, bad.Warnings.Count);
        Assert.All(bad.Warnings, w => Assert.DoesNotContain("yes", w));

        var notObject = Fixed("\"30\"");
        Assert.Equal(ChatRetentionChoice.Default, notObject.ChatRetention);
        Assert.Single(notObject.Warnings);
        Assert.Equal(ChatRetentionChoice.Default, Fixed("""{"maxAgeDays":99999}""").ChatRetention);
    }

    [Fact]
    public async Task A_governed_agent_follows_its_server_not_the_policy()
    {
        using var agent = Governed(new Gateway { Principal = Principal, LocalRetention = """{"max_sessions":7}""" })
            .UseMachinePolicy(Fixed("""{"autoDelete":false}""")).Build();
        await agent.StartAsync();
        Assert.Equal(ChatRetentionControl.Organisation, agent.ChatRetention.Control);
        Assert.Equal(7, agent.ChatRetention.MaxSessions);
    }

    // -- the person's data ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_export_and_the_deletion_include_the_choice()
    {
        using var agent = Standalone().Build();
        await agent.SetChatRetentionAsync(new ChatRetentionChoice(true, 14, 20));

        using var stream = new MemoryStream();
        await agent.ExportMyDataAsync(stream);
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.Single(e => e.FullName.EndsWith("preferences/retention.json", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            Assert.Contains("\"maxAgeDays\":14", reader.ReadToEnd());
        }

        await agent.DeleteMyDataAsync();
        Assert.False(File.Exists(Path.Combine(Folder("standalone", "preferences"), "retention.json")));
    }
}

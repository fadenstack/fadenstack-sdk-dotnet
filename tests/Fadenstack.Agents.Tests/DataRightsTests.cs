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
        await agent.ExportMyDataAsync(zip);
        var files = Unzip(zip);

        Assert.Equal(new[]
        {
            "README.txt", "approvals/docs-assistant.json", "approvals/other-agent.json",
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

        await agent.DeleteMyDataAsync();

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
    public async Task Without_a_principal_deleting_removes_the_shared_folders_only()
    {
        using var agent = Plain(new Gateway()).Build();
        await agent.StartSessionAsync();
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("stamp", ToolClass.Write, DateTimeOffset.UtcNow));
        await Seed("u-2", Gateway.Slug, "s1", "somebody else's");
        KeepAToken();

        await agent.DeleteMyDataAsync();

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
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "x");

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

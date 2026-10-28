using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The tests that point <see cref="FadenstackPaths"/> at a temporary folder run alone: the roots are process-wide, and a
/// test running beside them would put its stores there.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PathsCollection
{
    public const string Name = "FadenstackPaths";
}

/// <summary>Sessions and approvals on the device: in the signed-in user's own folder, and what an older SDK left behind.</summary>
[Collection(PathsCollection.Name)]
public class UserStorageTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-user-" + Guid.NewGuid());

    public UserStorageTests()
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

    /// <summary>A builder that leaves the stores to the SDK.</summary>
    private static AgentBuilder Plain(Gateway gateway) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(gateway.Server))
        .UseAgent(Gateway.Slug);

    private static async Task Use(FadenstackAgent agent)
    {
        await agent.StartSessionAsync();
        await agent.ApprovalStore.AddAsync(Gateway.Slug, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task With_a_principal_sessions_and_approvals_go_to_the_users_folder()
    {
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await Use(agent);
        Assert.Single(Directory.GetFiles(Data("users", "u-1", "sessions", Gateway.Slug)));
        Assert.Single(Directory.GetFiles(Data("users", "u-1", "approvals")));
        Assert.False(Directory.Exists(Data("sessions")));
        Assert.False(Directory.Exists(Data("approvals")));
    }

    [Fact]
    public async Task Without_a_principal_they_stay_in_the_shared_folders_of_an_older_sdk()
    {
        using var agent = Plain(new Gateway()).Build();
        await Use(agent);
        Assert.Single(Directory.GetFiles(Data("sessions", Gateway.Slug)));
        Assert.Single(Directory.GetFiles(Data("approvals")));
        Assert.False(Directory.Exists(Data("users")));
    }

    [Fact]
    public async Task A_store_the_host_passed_is_used_as_it_is()
    {
        var sessions = new MemorySessionStore();
        var approvals = new MemoryApprovalStore();
        using var agent = new Gateway { Principal = Principal }.Builder(sessions, approvals).Build();
        await Use(agent);
        Assert.Same(sessions, agent.SessionStore);
        Assert.Same(approvals, agent.ApprovalStore);
        Assert.False(Directory.Exists(Data("users")));
        Assert.Single(await agent.ListSessionsAsync());
    }

    [Fact]
    public async Task The_first_user_adopts_what_an_older_sdk_kept()
    {
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions", Gateway.Slug), "s1.jsonl"), "x");
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("approvals"), "a.json"), "x");
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await agent.StartAsync();
        Assert.True(File.Exists(Data("users", "u-1", "sessions", Gateway.Slug, "s1.jsonl")));
        Assert.True(File.Exists(Data("users", "u-1", "approvals", "a.json")));
        Assert.False(Directory.Exists(Data("sessions")));
        Assert.False(Directory.Exists(Data("approvals")));
    }

    [Fact]
    public void Nothing_moves_once_any_user_has_a_folder()
    {
        FadenstackPaths.User("someone-else");
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl"), "x");
        UserDataMigration.Adopt("u-1", NullLogger.Instance);
        Assert.True(File.Exists(Data("sessions", "s1.jsonl")));
        Assert.False(Directory.Exists(Data("users", "u-1")));
    }

    [Fact]
    public void Nothing_happens_without_older_data()
    {
        UserDataMigration.Adopt("u-1", NullLogger.Instance);
        Assert.False(Directory.Exists(Data("users")));
    }
}

using System.Security.Cryptography;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>A transcript reaches the disk only encrypted: without a protector that encrypts, the session stays in memory.</summary>
[Collection(PathsCollection.Name)]
public class TranscriptProtectionTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-transcript-" + Guid.NewGuid());

    public TranscriptProtectionTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
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

    private static AgentBuilder Plain(Gateway gateway, ILoggerFactory? logs = null)
    {
        var builder = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(gateway.Server))
            .UseAgent(Gateway.Slug);
        return logs is null ? builder : builder.UseLogging(logs);
    }

    [Fact]
    public async Task Without_encryption_and_without_a_host_store_the_session_stays_in_memory_and_one_warning_says_so()
    {
        SecretProtection.Use(new OwnerOnlyFileProtector());
        var logs = new CapturingLoggerFactory();
        using var agent = Plain(new Gateway(), logs).Build();

        await agent.StartAsync();
        await agent.StartSessionAsync();

        Assert.IsType<MemorySessionStore>(agent.SessionStore);
        Assert.False(Directory.Exists(Path.Combine(_temporary, "data", "sessions")));
        var warning = Assert.Single(logs.Entries, e => e.EventId == 2040);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal($"Agent {Gateway.Slug} keeps session transcripts in memory only: no OS key store is available to encrypt them on disk", warning.Message);
    }

    [Fact]
    public async Task With_encryption_the_session_goes_to_the_file_store_and_nothing_is_warned()
    {
        SecretProtection.Use(new FakeEncryptingProtector());
        var logs = new CapturingLoggerFactory();
        using var agent = Plain(new Gateway(), logs).Build();

        await agent.StartAsync();
        await agent.StartSessionAsync();

        Assert.IsType<FileSessionStore>(agent.SessionStore);
        Assert.Single(Directory.GetFiles(Path.Combine(_temporary, "data", "sessions", Gateway.Slug)));
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 2040);
    }

    [Fact]
    public async Task A_store_the_host_passed_is_kept_even_without_encryption()
    {
        SecretProtection.Use(new OwnerOnlyFileProtector());
        var sessions = new MemorySessionStore();
        var logs = new CapturingLoggerFactory();
        using var agent = Plain(new Gateway(), logs).UseSessionStore(sessions).Build();

        await agent.StartAsync();

        Assert.Same(sessions, agent.SessionStore);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 2040);
    }

}

/// <summary>Stands in for the OS key store, so tests that need files for sessions do not depend on the machine's.</summary>
internal sealed class FakeEncryptingProtector : ISecretProtector
{
    public bool Encrypts => true;

    public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

    public byte[] Unprotect(byte[] data) => Protect(data);
}

using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A kept sign-in that cannot be read counts as signed out, and the log says what kind of failure it was, never the path
/// or a token (sdk/51 I00 #4, I-9).
/// </summary>
public sealed class TokenStoreFailureTests : IDisposable
{
    private const string Key = "https://faden.test/|docs";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-tokens-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static TokenSet Tokens() => new()
    {
        AccessToken = "SECRET-A", RefreshToken = "SECRET-R", DeviceId = "d", RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    [Fact]
    public async Task A_sign_in_another_process_holds_locked_reads_as_signed_out_and_the_kind_is_logged()
    {
        var logs = new CapturingLoggerFactory();
        var store = new FileTokenStore(_directory, logger: logs.CreateLogger("Fadenstack.Client"));
        await store.SaveAsync(Key, Tokens());
        var file = Directory.GetFiles(_directory).Single();

        // Another process (an antivirus scanner, a backup) holds the file and shares nothing.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await store.LoadAsync(Key));
        }

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(1040, entry.EventId);
        Assert.Contains(entry.Values, v => v.StartsWith("Kind=", StringComparison.Ordinal) && v.Length > "Kind=".Length);
        Assert.DoesNotContain(logs.Texts, t => t.Contains(_directory) || t.Contains("SECRET") || t.Contains("docs"));

        // Once the file is free again, the sign-in is there.
        Assert.Equal("SECRET-A", (await store.LoadAsync(Key))?.AccessToken);
    }

    [Fact]
    public async Task A_damaged_sign_in_reads_as_signed_out()
    {
        var store = new FileTokenStore(_directory, new FailingProtector());
        await store.SaveAsync(Key, Tokens());

        Assert.Null(await store.LoadAsync(Key));
    }

    [Fact]
    public async Task A_folder_deleted_meanwhile_reads_as_signed_out()
    {
        var store = new FileTokenStore(_directory);
        Directory.Delete(_directory, true); // the user's data was deleted while the app ran

        Assert.Null(await store.LoadAsync(Key));
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => data;

        public byte[] Unprotect(byte[] data) => throw new System.Security.Cryptography.CryptographicException("not this user's");
    }
}

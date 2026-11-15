using System.Security.Cryptography;
using System.Text;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Secrets.Tests;

public sealed class FakeKeyStore : IKeyStore
{
    public byte[]? Key { get; set; }

    public int Writes { get; private set; }

    public byte[]? Read() => Key?.ToArray();

    public void Write(byte[] key)
    {
        Key = key;
        Writes++;
    }
}

public class OsKeyStoreProtectorTests
{
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"secret\":\"transcript\"}");

    private static OsKeyStoreProtector Make(FakeKeyStore? keys = null) => new(keys ?? new FakeKeyStore());

    [Fact]
    public void Data_round_trips_and_the_clear_text_is_not_in_the_output()
    {
        var protector = Make();
        var protectedData = protector.Protect(Plain);
        Assert.True(protector.Encrypts);
        Assert.Equal(Plain, protector.Unprotect(protectedData));
        Assert.DoesNotContain("transcript", Encoding.UTF8.GetString(protectedData));
    }

    [Fact]
    public void The_layout_is_header_nonce_tag_ciphertext_with_a_version_byte()
    {
        var protectedData = Make().Protect(Plain);
        Assert.Equal(new byte[] { 0xFA, 0xDE, 0x01 }, protectedData[..3]);
        Assert.Equal(3 + 12 + 16 + Plain.Length, protectedData.Length);
    }

    [Fact]
    public void Every_call_uses_a_fresh_nonce()
    {
        var protector = Make();
        var first = protector.Protect(Plain);
        var second = protector.Protect(Plain);
        Assert.NotEqual(first[3..15], second[3..15]);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(5)]   // nonce
    [InlineData(20)]  // tag
    [InlineData(40)]  // ciphertext
    [InlineData(2)]   // version
    public void A_changed_byte_is_detected(int index)
    {
        var protector = Make();
        var protectedData = protector.Protect(Plain);
        protectedData[index] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedData));
    }

    [Fact]
    public void Truncated_data_is_refused()
    {
        var protector = Make();
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protector.Protect(Plain)[..10]));
    }

    [Fact]
    public void The_key_is_created_once_and_a_second_protector_reads_the_same_one()
    {
        var keys = new FakeKeyStore();
        var protectedData = Make(keys).Protect(Plain);
        Make(keys).Protect(Plain);
        Assert.Equal(1, keys.Writes);
        Assert.Equal(32, keys.Key!.Length);
        Assert.Equal(Plain, Make(keys).Unprotect(protectedData));
    }

    [Fact]
    public void Another_key_cannot_read_the_data()
    {
        var protectedData = Make().Protect(Plain);
        Assert.ThrowsAny<CryptographicException>(() => Make().Unprotect(protectedData));
    }

    [Fact]
    public void A_key_store_that_keeps_nothing_fails_loudly()
    {
        var protector = new OsKeyStoreProtector(new ForgetfulKeyStore());
        Assert.Throws<CryptographicException>(() => protector.Protect(Plain));
    }

    [Fact]
    public void Clear_text_is_refused_except_during_the_one_move_of_the_owner_only_files()
    {
        var keys = new FakeKeyStore();
        var protector = Make(keys);
        var old = new OwnerOnlyFileProtector().Protect(Plain);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(old)); // a plain "always allow" planted later is never read

        protector.AcceptsPlain = true;
        Assert.Equal(Plain, protector.Unprotect(old));
        Assert.Equal(0, keys.Writes); // reading old data needs no key
        protector.AcceptsPlain = false;
        var rewritten = protector.Protect(Plain);
        Assert.NotEqual(old, rewritten);
        Assert.Equal(Plain, protector.Unprotect(rewritten));
    }

    [Fact]
    public void The_move_encrypts_the_owner_only_files_once_and_records_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "faden-plainmove-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Plant(string relative)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Plain);
            }
            Plant("tokens/abc.token");
            Plant("approvals/docs.json");
            Plant("users/u-1/approvals/docs.json");
            Plant("users/u-1/approvals/approvals-seal.key");
            Plant("users/u-1/sessions/docs/s1.jsonl"); // sessions are never written in the clear: not the move's
            var protector = Make();

            Assert.True(PlainMigration.Run(protector, root));

            foreach (var moved in new[] { "tokens/abc.token", "approvals/docs.json", "users/u-1/approvals/docs.json", "users/u-1/approvals/approvals-seal.key" })
            {
                var bytes = File.ReadAllBytes(Path.Combine(root, moved));
                Assert.True(OsKeyStoreProtector.IsEncrypted(bytes), moved);
                Assert.Equal(Plain, protector.Unprotect(bytes));
            }
            Assert.Equal(Plain, File.ReadAllBytes(Path.Combine(root, "users/u-1/sessions/docs/s1.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "keystore", PlainMigration.MarkerName)));
            Assert.False(protector.AcceptsPlain);

            Plant("users/u-1/approvals/planted.json"); // after the move: refused, and the move does not run again
            Assert.True(PlainMigration.Run(protector, root));
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(File.ReadAllBytes(Path.Combine(root, "users/u-1/approvals/planted.json"))));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Empty_old_content_is_read_as_empty()
    {
        Assert.Empty(Make().Unprotect(Array.Empty<byte>()));
    }

    private sealed class ForgetfulKeyStore : IKeyStore
    {
        public byte[]? Read() => null;

        public void Write(byte[] key)
        {
        }
    }
}

public class OsKeyStoreTests
{
    [Fact]
    public void On_Windows_the_factory_returns_DPAPI_and_existing_data_stays_readable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.True(OsKeyStore.TryCreate(out var protector));
        Assert.IsType<DpapiSecretProtector>(protector);
        Assert.Equal(new byte[] { 1, 2, 3 }, protector.Unprotect(new DpapiSecretProtector().Protect(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public void A_working_key_store_gives_the_encrypting_protector()
    {
        Assert.True(OsKeyStore.TryCreate(out var protector, null, () => new FakeKeyStore()));
        Assert.IsType<OsKeyStoreProtector>(protector);
        Assert.True(protector.Encrypts);
    }

    [Fact]
    public void When_the_key_store_fails_verification_the_factory_says_false_and_hands_back_owner_only_files()
    {
        var logs = new ListLogger();
        Assert.False(OsKeyStore.TryCreate(out var protector, logs, () => throw new InvalidOperationException("no Secret Service")));
        Assert.IsType<OwnerOnlyFileProtector>(protector);
        Assert.False(protector.Encrypts);
        var entry = Assert.Single(logs.Messages);
        Assert.DoesNotContain("no Secret Service", entry);
        Assert.Contains("InvalidOperationException", entry);
    }
}

internal sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

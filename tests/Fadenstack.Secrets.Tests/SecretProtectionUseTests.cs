using Fadenstack.Auth;
using Fadenstack.Storage;
using Xunit;

namespace Fadenstack.Secrets.Tests;

/// <summary>The tests that change the process-wide default protector run alone: a test beside them would read their choice.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DefaultProtectorCollection
{
    public const string Name = "SecretProtectionDefault";
}

/// <summary>The default protector is process-wide state: these tests run one after another and reset it.</summary>
[Collection(DefaultProtectorCollection.Name)]
public class SecretProtectionUseTests : IDisposable
{
    public SecretProtectionUseTests() => SecretProtection.ResetForTests();

    public void Dispose() => SecretProtection.ResetForTests();

    [Fact]
    public async Task Use_sets_the_default_and_stores_created_afterwards_take_it()
    {
        var protector = new OsKeyStoreProtector(new FakeKeyStore());
        SecretProtection.Use(protector);

        Assert.Same(protector, SecretProtection.Default);
        var folder = Path.Combine(Path.GetTempPath(), "faden-use-" + Guid.NewGuid());
        try
        {
            var store = new FileTokenStore(folder);
            await store.SaveAsync("k", new TokenSet { AccessToken = "a-token", RefreshToken = "r", DeviceId = "d" });
            var onDisk = File.ReadAllBytes(Directory.GetFiles(folder).Single());
            Assert.DoesNotContain("a-token", System.Text.Encoding.UTF8.GetString(onDisk));
            Assert.Equal("a-token", (await store.LoadAsync("k"))!.AccessToken);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void A_second_Use_is_refused()
    {
        SecretProtection.Use(new OwnerOnlyFileProtector());
        Assert.Throws<InvalidOperationException>(() => SecretProtection.Use(new OwnerOnlyFileProtector()));
    }

    [Fact]
    public void Use_after_the_default_was_read_is_refused_and_the_default_does_not_change()
    {
        var first = SecretProtection.Default;
        Assert.Throws<InvalidOperationException>(() => SecretProtection.Use(new OwnerOnlyFileProtector()));
        Assert.Same(first, SecretProtection.Default);
    }

    [Fact]
    public void Use_refuses_null()
    {
        Assert.Throws<ArgumentNullException>(() => SecretProtection.Use(null!));
    }

    [Fact]
    public void UseOsKeyStore_makes_the_factorys_protector_the_default_when_it_works()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // elsewhere it would touch the real Keychain or keyring
        }
        Assert.True(FadenstackSecrets.UseOsKeyStore());
        Assert.IsType<DpapiSecretProtector>(SecretProtection.Default);
    }
}

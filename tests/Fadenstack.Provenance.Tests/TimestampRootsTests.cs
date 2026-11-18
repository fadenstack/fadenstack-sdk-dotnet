using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>
/// The time-stamp authority's certificate chains to a pinned root, not to whatever this computer's store trusts (OWASP ASVS 5.0
/// V11, V13): DigiCert's root is carried, as in the TypeScript SDK, and an organisation's own server adds its root.
/// </summary>
public class TimestampRootsTests
{
    private static readonly byte[] Nonce = [0x41, 2, 3, 4, 5, 6, 7, 8];

    private static (byte[] Hash, BigInteger Nonce) Parts()
    {
        var hash = new byte[32];
        new Random(11).NextBytes(hash);
        return (hash, new BigInteger(Enumerable.Reverse(Nonce).Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void The_carried_root_is_DigiCert_Trusted_Root_G4_with_the_fingerprint_the_TypeScript_SDK_names()
    {
        using var roots = new DisposableRoots(TimestampRoots.DigiCert());

        var root = Assert.Single(roots.Certificates);

        Assert.Equal("552f7bdcf1a7af9e6ce672017f4f12abf77240c78e761ac203d1d9d20ac89988", TimestampRoots.DigiCertTrustedRootG4Sha256);
        using var sha = SHA256.Create();
        Assert.Equal(TimestampRoots.DigiCertTrustedRootG4Sha256, Hex(sha.ComputeHash(root.RawData)));
        Assert.Equal("DDFB16CD4931C973A2037D3FC83A4D7D775D05E4", root.Thumbprint);
        Assert.Contains("CN=DigiCert Trusted Root G4", root.Subject);
        Assert.Equal(root.Subject, root.Issuer); // self-signed
    }

    [Fact]
    public void A_token_is_accepted_with_its_root_pinned_and_refused_when_it_is_not()
    {
        var (hash, nonce) = Parts();
        var answer = LocalProvenanceTests.TestAuthority.Default.Answer(hash, nonce);

        Assert.True(Rfc3161.Grants(answer, hash, Nonce, LocalProvenanceTests.TestAuthority.Roots));
        Assert.False(Rfc3161.Grants(answer, hash, Nonce)); // the root is in no store and not pinned
        Assert.False(Rfc3161.Grants(answer, hash, Nonce, new X509Certificate2Collection { LocalProvenanceTests.TestAuthority.Untrusted.Root }));
    }

    [Fact]
    public void A_pinned_root_of_another_authority_does_not_vouch_for_this_one_and_DigiCerts_stays_pinned_beside_it()
    {
        var (hash, nonce) = Parts();
        var answer = LocalProvenanceTests.TestAuthority.Default.Answer(hash, nonce);
        var onlyOthers = new X509Certificate2Collection { LocalProvenanceTests.TestAuthority.Untrusted.Root };

        Assert.False(Rfc3161.Grants(answer, hash, Nonce, onlyOthers));
        onlyOthers.Add(LocalProvenanceTests.TestAuthority.Default.Root);
        Assert.True(Rfc3161.Grants(answer, hash, Nonce, onlyOthers));
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

    private sealed class DisposableRoots(X509Certificate2Collection roots) : IDisposable
    {
        public IReadOnlyList<X509Certificate2> Certificates { get; } = roots.Cast<X509Certificate2>().ToList();

        public void Dispose()
        {
            foreach (var c in Certificates)
            {
                c.Dispose();
            }
        }
    }
}

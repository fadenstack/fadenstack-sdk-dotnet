using System.Security.Cryptography.X509Certificates;

namespace Fadenstack.Provenance;

/// <summary>
/// The trust anchors for a time-stamp authority's certificate (sdk/51 F10; OWASP ASVS 5.0 V11, V13), the same as the
/// TypeScript SDK's <c>DIGICERT_TIMESTAMP_ROOTS</c>: the root DigiCert's time-stamping service chains to is carried here, and
/// an organisation's own time-stamp server adds its root with <see cref="LocalProvenanceOptions.TimestampRoots"/>. The
/// computer's own certificate store decides nothing, so a root added to it, by a person or by software, cannot vouch for a
/// time-stamp.
/// <para>
/// DigiCert Trusted Root G4 (CN=DigiCert Trusted Root G4, OU=www.digicert.com, O=DigiCert Inc, C=US; RSA 4096, sha384RSA; valid
/// 1 Aug 2013 to 15 Jan 2038). SHA-1 thumbprint DDFB16CD4931C973A2037D3FC83A4D7D775D05E4, SHA-256 fingerprint
/// <see cref="DigiCertTrustedRootG4Sha256"/>. The DER is the one in the TypeScript SDK (<c>timestampRoots.ts</c>); a test checks
/// it against the fingerprint.
/// </para>
/// </summary>
internal static class TimestampRoots
{
    /// <summary>The SHA-256 fingerprint of DigiCert Trusted Root G4's DER, lower-case hex.</summary>
    internal const string DigiCertTrustedRootG4Sha256 = "552f7bdcf1a7af9e6ce672017f4f12abf77240c78e761ac203d1d9d20ac89988";

    private const string DigiCertTrustedRootG4Base64 =
        "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";

    /// <summary>The roots DigiCert's time-stamping service chains to (a new collection each time; the caller may dispose it).</summary>
    internal static X509Certificate2Collection DigiCert() =>
        new() { new X509Certificate2(Convert.FromBase64String(DigiCertTrustedRootG4Base64)) };
}

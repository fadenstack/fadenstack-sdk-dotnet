using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Fadenstack.Provenance;

/// <summary>
/// The two RFC 3161 messages a device needs: a time-stamp request over a SHA-256 hash, and the check of the authority's
/// answer. The request and the token's <c>TSTInfo</c> are read and written with System.Formats.Asn1 because netstandard2.0
/// (what .NET Framework hosts such as Office load) has no <c>Rfc3161TimestampRequest</c>; the token's CMS signature is
/// checked with <see cref="SignedCms"/>. The answer is input from outside (owner, 14 Nov): a token counts only when its
/// signature, the authority's certificate and the nonce all check out, and nothing in it ever throws past this class.
/// </summary>
internal static class Rfc3161
{
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string TstInfo = "1.2.840.113549.1.9.16.1.4";
    private const string TimeStampingUsage = "1.3.6.1.5.5.7.3.8";

    /// <summary>A <c>TimeStampReq</c>: version 1, the hash, the nonce, and the authority's certificate asked for.</summary>
    public static byte[] Request(byte[] sha256, byte[] nonce)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.PushSequence();
        w.WriteInteger(1);
        w.PushSequence(); // MessageImprint
        w.PushSequence();
        w.WriteObjectIdentifier(Sha256);
        w.WriteNull();
        w.PopSequence();
        w.WriteOctetString(sha256);
        w.PopSequence();
        w.WriteIntegerUnsigned(DeviceSigner.Minimal(nonce));
        w.WriteBoolean(true); // certReq: the token carries the authority's certificate
        w.PopSequence();
        return w.Encode();
    }

    /// <summary>
    /// Whether <paramref name="response"/> (a <c>TimeStampResp</c>) grants a token that: is signed (CMS) by a certificate made
    /// for time-stamping, which chains to DigiCert's pinned root or to one of <paramref name="roots"/> (never to a root only this computer's store has); and whose signed
    /// <c>TSTInfo</c> covers <paramref name="sha256"/> with <paramref name="nonce"/>. Anything else, a malformed answer too,
    /// is false.
    /// </summary>
    public static bool Grants(byte[] response, byte[] sha256, byte[] nonce, X509Certificate2Collection? roots = null)
    {
        try
        {
            var token = Token(response);
            if (token is null)
            {
                return false;
            }
            var cms = new SignedCms();
            cms.Decode(token);
            if (cms.ContentInfo.ContentType.Value != TstInfo || cms.SignerInfos.Count != 1)
            {
                return false;
            }
            cms.CheckSignature(verifySignatureOnly: true); // the signature over the content and its attributes
            var signer = cms.SignerInfos[0].Certificate;
            if (signer is null || !ForTimeStamping(signer) || !Trusted(signer, cms.Certificates, roots))
            {
                return false;
            }
            return Covers(cms.ContentInfo.Content, sha256, nonce);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException or InvalidOperationException
            or OverflowException or FormatException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>The token's DER from a granted answer; null for a refusal or an answer without one.</summary>
    private static byte[]? Token(byte[] response)
    {
        var resp = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
        var statusInfo = resp.ReadSequence();
        if (!statusInfo.TryReadInt32(out var status) || status is not (0 or 1))
        {
            return null; // granted, or granted with modifications; anything else is a refusal
        }
        return resp.HasData ? resp.ReadEncodedValue().ToArray() : null;
    }

    /// <summary>A certificate made for time-stamping (RFC 3161 §2.3: the extended key usage, that purpose).</summary>
    private static bool ForTimeStamping(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == TimeStampingUsage));

    /// <summary>
    /// Whether the authority's certificate chains to one of the pinned roots: DigiCert's (<see cref="TimestampRoots"/>) or one of
    /// <paramref name="roots"/> (an organisation's own authority; tests). The computer's certificate store decides nothing
    /// (OWASP ASVS 5.0 V11, V13), whether the authority is the default or a policy's own server. Revocation is not checked:
    /// the device may be offline.
    /// </summary>
    private static bool Trusted(X509Certificate2 certificate, X509Certificate2Collection intermediates, X509Certificate2Collection? roots)
    {
        var pinned = TimestampRoots.DigiCert();
        if (roots is not null)
        {
            pinned.AddRange(roots);
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
#if NET8_0_OR_GREATER
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(pinned);
#else
        // .NET Framework / netstandard2.0 has no custom trust mode: the pinned roots are offered, the "unknown authority" error
        // of a root outside the store is let through, and the root the chain ends at must then be one of the pinned ones.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.AddRange(pinned);
#endif
        if (!chain.Build(certificate) || chain.ChainElements.Count == 0)
        {
            return false;
        }
        var root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
        return pinned.Cast<X509Certificate2>().Any(r => Enumerable.SequenceEqual(r.RawData, root.RawData));
    }

    /// <summary>Whether a signed <c>TSTInfo</c> covers the hash with the nonce.</summary>
    private static bool Covers(byte[] tstInfo, byte[] sha256, byte[] nonce)
    {
        var info = new AsnReader(tstInfo, AsnEncodingRules.BER).ReadSequence();
        info.ReadInteger(); // version
        info.ReadObjectIdentifier(); // policy
        var imprint = info.ReadSequence();
        if (imprint.ReadSequence().ReadObjectIdentifier() != Sha256 || !imprint.ReadOctetString().SequenceEqual(sha256))
        {
            return false;
        }
        info.ReadInteger(); // serialNumber
        info.ReadGeneralizedTime(); // genTime
        while (info.HasData)
        {
            if (info.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                return info.ReadInteger() == new BigInteger(Enumerable.Reverse(nonce).Concat(new byte[] { 0 }).ToArray());
            }
            info.ReadEncodedValue(); // accuracy, ordering
        }
        return false; // we sent a nonce: a token without it is not ours
    }
}

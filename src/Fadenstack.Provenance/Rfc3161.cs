using System.Formats.Asn1;
using System.Numerics;

namespace Fadenstack.Provenance;

/// <summary>
/// The two RFC 3161 messages a device needs: a time-stamp request over a SHA-256 hash, and the check that an answer grants a
/// token over that hash with that nonce. Written with System.Formats.Asn1 because netstandard2.0 (what .NET Framework hosts
/// such as Office load) has no <c>Rfc3161TimestampRequest</c>. The token's own signature is the verifier's to check, as the
/// provenance engine leaves it.
/// </summary>
internal static class Rfc3161
{
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string TstInfo = "1.2.840.113549.1.9.16.1.4";

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
        w.WriteBoolean(true); // certReq
        w.PopSequence();
        return w.Encode();
    }

    /// <summary>
    /// Whether <paramref name="response"/> (a <c>TimeStampResp</c>) granted a token whose <c>TSTInfo</c> covers
    /// <paramref name="sha256"/> with <paramref name="nonce"/>. Anything else, a malformed answer too, is false.
    /// </summary>
    public static bool Grants(byte[] response, byte[] sha256, byte[] nonce)
    {
        try
        {
            var resp = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
            var statusInfo = resp.ReadSequence();
            if (!statusInfo.TryReadInt32(out var status) || status is not (0 or 1))
            {
                return false; // granted, or granted with modifications; anything else is a refusal
            }
            if (!resp.HasData)
            {
                return false;
            }
            var contentInfo = resp.ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != SignedData)
            {
                return false;
            }
            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            signedData.ReadInteger(); // version
            signedData.ReadSetOf(); // digestAlgorithms
            var encapsulated = signedData.ReadSequence();
            if (encapsulated.ReadObjectIdentifier() != TstInfo)
            {
                return false;
            }
            var wrapped = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            var info = new AsnReader(wrapped.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
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
                var tag = info.PeekTag();
                if (tag.HasSameClassAndValue(Asn1Tag.Integer))
                {
                    return info.ReadInteger() == new BigInteger(Enumerable.Reverse(nonce).Concat(new byte[] { 0 }).ToArray());
                }
                info.ReadEncodedValue(); // accuracy, ordering
            }
            return false; // we sent a nonce: an answer without it is not ours
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Auth;

namespace Fadenstack.Provenance;

/// <summary>
/// Signs provenance records on the device (sdk/51 F10, owner 14 Nov), in the provenance engine's own format so one verifier
/// reads both: a compact JWS (RFC 7515, payload attached) over the record's canonical JSON (keys sorted, no spaces, UTF-8),
/// ES256 with a P-256 key, the header <c>{"alg":"ES256","typ":"faden-provenance+jws","kid":"device:…","x5c":[…]}</c>. The
/// <c>x5c</c> holds a self-signed certificate of the device's public key, so anyone can check the signature; the signer shows
/// as unknown, since no trust list names it.
/// </summary>
/// <remarks>
/// The key is one per installation and OS user, made on first use. Its private half is kept only encrypted by the OS key
/// store (<see cref="SecretProtection"/>: DPAPI on Windows; the Keychain or libsecret with <c>Fadenstack.Secrets</c>), never
/// in a file as such and never in a log. Where nothing encrypts, the key lives only in memory for as long as the app runs.
/// OWASP ASVS 5.0 V11 (approved algorithms, keys in a key store), V14 (data at rest).
/// </remarks>
public sealed class DeviceSigner : IDisposable
{
    public const string Typ = "faden-provenance+jws";

    /// <summary>The <c>kid</c> of a device's signature starts so: a server's names its key version instead.</summary>
    public const string KidPrefix = "device:";

    private const int Coordinate = 32; // P-256

    private static readonly JsonSerializerOptions Canonical = new()
    {
        // Like Python's json.dumps(ensure_ascii=False): characters stay as they are, only what JSON requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly ECDsa _key;
    private readonly byte[] _certificate;

    private DeviceSigner(ECDsa key, byte[] certificate, bool persisted)
    {
        _key = key;
        _certificate = certificate;
        Persisted = persisted;
        KeyId = KidPrefix + Thumbprint(key);
    }

    /// <summary><c>device:</c> and the first 16 hex digits of the SHA-256 of the public key.</summary>
    public string KeyId { get; }

    /// <summary>Whether the key is kept across runs (encrypted by the OS key store); false: in memory only.</summary>
    public bool Persisted { get; }

    /// <summary>The self-signed certificate of the public key, DER, as the JWS carries it.</summary>
    public byte[] Certificate => (byte[])_certificate.Clone();

    /// <summary>
    /// The installation's signer: its key from <paramref name="file"/>, decrypted by <paramref name="protector"/>, or a new
    /// key written there. A <paramref name="protector"/> that does not encrypt gets a key in memory only, and nothing is written.
    /// </summary>
    public static DeviceSigner ForInstallation(string file, ISecretProtector protector)
    {
        if (protector is null)
        {
            throw new ArgumentNullException(nameof(protector));
        }
        if (!protector.Encrypts)
        {
            return Ephemeral();
        }
        if (File.Exists(file))
        {
            try
            {
                return Load(protector.Unprotect(File.ReadAllBytes(file)));
            }
            catch (Exception e) when (e is CryptographicException or FormatException or JsonException or KeyNotFoundException)
            {
                // A key this user cannot read (another user's copy, a damaged file): a new one takes its place. Records signed
                // before keep their own public key, so they still verify.
            }
        }
        var signer = Create(persisted: true);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        SecretProtection.WriteOwnerOnly(file, protector.Protect(signer.Export()));
        return signer;
    }

    /// <summary>A signer whose key lives in memory only.</summary>
    public static DeviceSigner Ephemeral() => Create(persisted: false);

    private static DeviceSigner Create(bool persisted)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceSigner(key, SelfSigned(key), persisted);
    }

    /// <summary>The compact JWS over <paramref name="payload"/>'s canonical JSON.</summary>
    public string Sign(JsonNode payload)
    {
        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["typ"] = Typ,
            ["kid"] = KeyId,
            ["x5c"] = new JsonArray(Convert.ToBase64String(_certificate)),
        };
        var input = Base64Url(CanonicalBytes(header)) + "." + Base64Url(CanonicalBytes(payload));
        var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256); // IEEE P1363: r || s
        return input + "." + Base64Url(signature);
    }

    /// <summary>
    /// Checks a device's or a server's JWS with the key in its <c>x5c</c>: its payload when the signature matches, else null.
    /// It says nothing about who signed: a device's certificate is on no trust list.
    /// </summary>
    public static JsonElement? Verify(string jws)
    {
        try
        {
            var parts = (jws ?? "").Split('.');
            if (parts.Length != 3)
            {
                return null;
            }
            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            if (header.RootElement.ValueKind != JsonValueKind.Object
                || !header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "ES256"
                || !header.RootElement.TryGetProperty("x5c", out var x5c) || x5c.ValueKind != JsonValueKind.Array
                || x5c.GetArrayLength() == 0)
            {
                return null;
            }
            var signature = FromBase64Url(parts[2]);
            if (signature.Length != 2 * Coordinate)
            {
                return null;
            }
            using var certificate = new X509Certificate2(Convert.FromBase64String(x5c[0].GetString()!));
            using var key = certificate.GetECDsaPublicKey();
            if (key is null || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))
            {
                return null;
            }
            using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
            return payload.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or ArgumentException
            or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The bytes of the signature part of a JWS (what an RFC 3161 time-stamp covers, as the engine's does).</summary>
    internal static byte[] SignatureBytes(string jws) => FromBase64Url(jws.Substring(jws.LastIndexOf('.') + 1));

    /// <summary>The canonical JSON a signature covers: keys sorted (ordinal), no spaces, UTF-8.</summary>
    internal static byte[] CanonicalBytes(JsonNode node) => Encoding.UTF8.GetBytes(Sorted(node)?.ToJsonString(Canonical) ?? "null");

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    private byte[] Export()
    {
        var p = _key.ExportParameters(includePrivateParameters: true);
        var json = new JsonObject
        {
            ["crv"] = "P-256",
            ["d"] = Convert.ToBase64String(p.D!),
            ["x"] = Convert.ToBase64String(p.Q.X!),
            ["y"] = Convert.ToBase64String(p.Q.Y!),
            ["certificate"] = Convert.ToBase64String(_certificate),
        };
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        Array.Clear(p.D!, 0, p.D!.Length);
        return bytes;
    }

    private static DeviceSigner Load(byte[] stored)
    {
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var root = doc.RootElement;
            if (root.GetProperty("crv").GetString() != "P-256")
            {
                throw new FormatException("Not a P-256 key.");
            }
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Convert.FromBase64String(root.GetProperty("d").GetString()!),
                Q = new ECPoint
                {
                    X = Convert.FromBase64String(root.GetProperty("x").GetString()!),
                    Y = Convert.FromBase64String(root.GetProperty("y").GetString()!),
                },
            };
            var key = ECDsa.Create(parameters);
            Array.Clear(parameters.D, 0, parameters.D.Length);
            return new DeviceSigner(key, Convert.FromBase64String(root.GetProperty("certificate").GetString()!), persisted: true);
        }
        finally
        {
            Array.Clear(stored, 0, stored.Length);
        }
    }

    private static string Thumbprint(ECDsa key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray());
        return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// A self-signed X.509 v3 certificate of the key (CN "Fadenstack device signer", valid for 30 years): built with
    /// System.Formats.Asn1, since netstandard2.0 has no CertificateRequest. It only carries the public key: it vouches for nothing.
    /// </summary>
    private static byte[] SelfSigned(ECDsa key)
    {
        const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var serial = new byte[16];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(serial);
        }
        serial[0] &= 0x7F; // positive
        serial[0] |= 0x01; // and without a leading zero to strip
        var now = DateTimeOffset.UtcNow;
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        tbs.PushSequence();
        tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        tbs.WriteInteger(2); // v3
        tbs.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        tbs.WriteIntegerUnsigned(serial);
        Algorithm(tbs, EcdsaWithSha256);
        Name(tbs);
        tbs.PushSequence();
        tbs.WriteUtcTime(now.AddDays(-1));
        tbs.WriteGeneralizedTime(now.AddYears(30), omitFractionalSeconds: true);
        tbs.PopSequence();
        Name(tbs);
        tbs.PushSequence(); // SubjectPublicKeyInfo
        tbs.PushSequence();
        tbs.WriteObjectIdentifier("1.2.840.10045.2.1"); // id-ecPublicKey
        tbs.WriteObjectIdentifier("1.2.840.10045.3.1.7"); // prime256v1
        tbs.PopSequence();
        tbs.WriteBitString(new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray());
        tbs.PopSequence();
        tbs.PopSequence();
        var toBeSigned = tbs.Encode();

        var raw = key.SignData(toBeSigned, HashAlgorithmName.SHA256);
        var der = new AsnWriter(AsnEncodingRules.DER);
        der.PushSequence();
        der.WriteIntegerUnsigned(raw.Take(Coordinate).ToArray());
        der.WriteIntegerUnsigned(raw.Skip(Coordinate).ToArray());
        der.PopSequence();

        var certificate = new AsnWriter(AsnEncodingRules.DER);
        certificate.PushSequence();
        certificate.WriteEncodedValue(toBeSigned);
        Algorithm(certificate, EcdsaWithSha256);
        certificate.WriteBitString(der.Encode());
        certificate.PopSequence();
        return certificate.Encode();

        static void Algorithm(AsnWriter w, string oid)
        {
            w.PushSequence();
            w.WriteObjectIdentifier(oid);
            w.PopSequence();
        }

        static void Name(AsnWriter w)
        {
            w.PushSequence();
            w.PushSetOf();
            w.PushSequence();
            w.WriteObjectIdentifier("2.5.4.3"); // CN
            w.WriteCharacterString(UniversalTagNumber.UTF8String, "Fadenstack device signer");
            w.PopSequence();
            w.PopSetOf();
            w.PopSequence();
        }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    public void Dispose() => _key.Dispose();
}

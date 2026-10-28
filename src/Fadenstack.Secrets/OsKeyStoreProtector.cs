using System.Security.Cryptography;
using Fadenstack.Auth;

namespace Fadenstack.Secrets;

/// <summary>Where the data key is kept: the OS key store, or a fake in a test.</summary>
internal interface IKeyStore
{
    /// <summary>The stored key, or null when there is none yet.</summary>
    byte[]? Read();

    void Write(byte[] key);
}

/// <summary>
/// Encrypts with AES-256-GCM under a random data key that lives in the OS key store (Keychain, Secret Service), one
/// key per OS user. Layout: <c>0xFA 0xDE version(1) nonce(12) tag(16) ciphertext</c>; the header is authenticated.
/// Data without the header is read as it is: it was written by <see cref="OwnerOnlyFileProtector"/>, and the next
/// write encrypts it. (0xFA never starts UTF-8 text, so clear JSON cannot be mistaken for the header.)
/// </summary>
internal sealed class OsKeyStoreProtector : ISecretProtector
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Header = { 0xFA, 0xDE, 0x01 };

    private readonly IKeyStore _keys;
    private readonly object _gate = new();
    private byte[]? _key;

    internal OsKeyStoreProtector(IKeyStore keys) => _keys = keys;

    public bool Encrypts => true;

    public byte[] Protect(byte[] data)
    {
        var key = Key();
        var result = new byte[Header.Length + NonceSize + TagSize + data.Length];
        Header.CopyTo(result, 0);
        var nonce = result.AsSpan(Header.Length, NonceSize);
        var tag = result.AsSpan(Header.Length + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, result.AsSpan(Header.Length + NonceSize + TagSize), tag, Header);
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (!IsEncrypted(data))
        {
            return data; // clear text from before the key store was on
        }
        if (data.Length < Header.Length + NonceSize + TagSize)
        {
            throw new CryptographicException("The protected data is truncated.");
        }
        if (data[2] != Header[2])
        {
            throw new CryptographicException($"The protected data has an unknown version ({data[2]}).");
        }
        var plain = new byte[data.Length - Header.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(
            data.AsSpan(Header.Length, NonceSize),
            data.AsSpan(Header.Length + NonceSize + TagSize),
            data.AsSpan(Header.Length + NonceSize, TagSize),
            plain,
            Header);
        return plain;
    }

    private static bool IsEncrypted(byte[] data) => data.Length >= 2 && data[0] == Header[0] && data[1] == Header[1];

    private byte[] Key()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                return _key;
            }
            var stored = _keys.Read();
            if (stored is null || stored.Length != KeySize)
            {
                _keys.Write(RandomNumberGenerator.GetBytes(KeySize));
                // Read back: if another process of this user created a key meanwhile, both end up on the stored one.
                stored = _keys.Read();
            }
            return _key = stored is { Length: KeySize }
                ? stored
                : throw new CryptographicException("The OS key store did not keep the data key.");
        }
    }
}

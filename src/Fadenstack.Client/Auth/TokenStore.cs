using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Http;

namespace Fadenstack.Auth;

/// <summary>Keeps an agent's sign-in between runs.</summary>
public interface ITokenStore
{
    Task<TokenSet?> LoadAsync(string key, CancellationToken cancellationToken = default);

    Task SaveAsync(string key, TokenSet tokens, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Only in memory: a sign-in lasts as long as the process.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, TokenSet> _tokens = new();

    public Task<TokenSet?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_tokens)
        {
            return Task.FromResult(_tokens.TryGetValue(key, out var found) ? found : null);
        }
    }

    public Task SaveAsync(string key, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        lock (_tokens)
        {
            _tokens[key] = tokens;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_tokens)
        {
            _tokens.Remove(key);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// One file per server and agent, under the user's data folder, encrypted with DPAPI on Windows
/// (<see cref="SecretProtection.Default"/>).
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _directory;
    private readonly ISecretProtector _protector;

    public FileTokenStore(string? directory = null, ISecretProtector? protector = null)
    {
        _directory = directory ?? SecretProtection.DataDirectory("tokens");
        Directory.CreateDirectory(_directory);
        _protector = protector ?? SecretProtection.Default;
    }

    public Task<TokenSet?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<TokenSet?>(null);
        }
        try
        {
            var json = _protector.Unprotect(File.ReadAllBytes(path));
            return Task.FromResult(JsonSerializer.Deserialize<TokenSet>(json, FadenstackJson.Options));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return Task.FromResult<TokenSet?>(null); // another user's, or damaged: sign in again
        }
    }

    public Task SaveAsync(string key, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens, FadenstackJson.Options);
        SecretProtection.WriteOwnerOnly(PathOf(key), _protector.Protect(json));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant() + ".token");
    }
}

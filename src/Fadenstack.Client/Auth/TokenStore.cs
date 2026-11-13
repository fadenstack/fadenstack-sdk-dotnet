using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Http;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// One file per server and agent, under the data root (<see cref="FadenstackPaths.DataRoot"/>), encrypted with DPAPI on Windows
/// (<see cref="SecretProtection.Default"/>).
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly ILogger _log;

    /// <param name="directory">The folder; the data root's <c>tokens</c> by default.</param>
    /// <param name="protector">How the files are encrypted; <see cref="SecretProtection.Default"/> by default.</param>
    /// <param name="logger">Where a sign-in that cannot be read is logged (the kind of failure only); nowhere if not given.</param>
    public FileTokenStore(string? directory = null, ISecretProtector? protector = null, ILogger? logger = null)
    {
        _directory = directory ?? FadenstackPaths.Data("tokens");
        Directory.CreateDirectory(_directory);
        _protector = protector ?? SecretProtection.Default;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The kept sign-in, or null when there is none or it cannot be read: locked by another process for longer than a
    /// moment, another user's, or damaged. Then the user signs in again; the log names the kind of failure.
    /// </summary>
    public async Task<TokenSet?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await SecretProtection.ReadKeptAsync(PathOf(key), cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : JsonSerializer.Deserialize<TokenSet>(_protector.Unprotect(bytes), FadenstackJson.Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            Log.KeptSignInUnreadable(_log, e.GetType().Name);
            return null;
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

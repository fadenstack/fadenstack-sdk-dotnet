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

/// <summary>
/// A token store that lets one holder at a time renew a sign-in, across processes where the store is shared (a file, a
/// key store). The refresh token changes on every renewal, and the server ends the whole sign-in when an old one comes
/// back; so <see cref="AgentTokenProvider"/> renews under this lock and, if another process renewed meanwhile, takes the
/// renewed sign-in from the store instead. A store without it is renewed as before, per provider.
/// </summary>
public interface ITokenStoreLock
{
    /// <summary>Waits until nobody else renews the sign-in kept under <paramref name="key"/>, and holds it until disposed.</summary>
    Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Only in memory: a sign-in lasts as long as the process.</summary>
public sealed class MemoryTokenStore : ITokenStore, ITokenStoreLock
{
    private readonly Dictionary<string, TokenSet> _tokens = new();
    private readonly Dictionary<string, SemaphoreSlim> _renewals = new();

    /// <summary>One renewal at a time per sign-in, for the providers in this process that share this store.</summary>
    public async Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate;
        lock (_renewals)
        {
            if (!_renewals.TryGetValue(key, out gate!))
            {
                _renewals[key] = gate = new SemaphoreSlim(1, 1);
            }
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(gate);
    }

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Release(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

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
public sealed class FileTokenStore : ITokenStore, ITokenStoreLock
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

    /// <summary>
    /// One renewal at a time per sign-in, across every process that keeps it in this folder: the holder keeps
    /// <c>&lt;file&gt;.lock</c> open and shared with nobody (a lock the OS releases if the process ends). The lock file is
    /// empty and stays. Gives up after <see cref="LockWait"/>, when another process has held it that long.
    /// </summary>
    public async Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathOf(key) + ".lock";
        var waited = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < LockWait)
            {
                // Another process (or provider) renews this sign-in now.
                await Task.Delay(Math.Min(25 * attempt, 250), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                throw new FadenstackException(
                    $"Another process has been renewing this sign-in for {LockWait.TotalSeconds:0} s; try again.", e);
            }
        }
    }

    /// <summary>How long <see cref="LockAsync"/> waits for another process's renewal.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);

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

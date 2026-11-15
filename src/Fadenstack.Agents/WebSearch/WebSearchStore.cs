using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Auth;
using Fadenstack.Storage;

namespace Fadenstack.WebSearch;

/// <summary>Where the <c>web_search</c> tool asks (sdk/51 F06): none until the person picks one.</summary>
public enum WebSearchProvider
{
    /// <summary>No provider: the tool is not offered.</summary>
    None,

    /// <summary>The Brave Search API, with the person's own key.</summary>
    Brave,

    /// <summary>A SearXNG instance the person names, with its JSON format turned on.</summary>
    SearXng,
}

/// <summary>The person's choice of a web search provider. Never the key: that is kept apart (<see cref="WebSearchStore.SetBraveKey"/>).</summary>
public sealed record WebSearchSettings
{
    /// <summary>No provider: the tool is off.</summary>
    public static WebSearchSettings Off { get; } = new();

    public WebSearchProvider Provider { get; init; }

    /// <summary>The SearXNG instance, for <see cref="WebSearchProvider.SearXng"/>: https, or plain http on this computer only.</summary>
    public Uri? SearXngUrl { get; init; }

    /// <summary>
    /// Whether <paramref name="url"/> may be a SearXNG instance: absolute, https, or http to this computer (the query would cross
    /// the network in the clear otherwise), with no user or password in it.
    /// </summary>
    public static bool IsAllowedSearXngUrl(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.UserInfo.Length == 0
        && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));
}

/// <summary>
/// The person's web search settings on this device (sdk/51 F06), in their own folder: the provider and the SearXNG address in
/// <c>users/&lt;user&gt;/preferences/web-search.json</c> (no secret; exported with the person's data), and the Brave key in
/// <c>users/&lt;user&gt;/keys/web-search-brave.key</c>, encrypted with the SDK's protector (DPAPI on Windows, the OS key store
/// elsewhere) and never exported. Where nothing encrypts, the key is kept in memory for the run and never written. Deleting the
/// person's data deletes both. OWASP ASVS 5.0 V13 (secrets), V14 (data at rest).
/// </summary>
public sealed class WebSearchStore
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object _gate = new();
    private readonly string _settingsFile;
    private readonly string _keyFile;
    private readonly ISecretProtector _protector;
    private string? _keyInMemory;

    /// <param name="personFolder">The person's folder (<c>users/&lt;user&gt;</c>); its subfolders are made when something is saved.</param>
    /// <param name="protector">What encrypts the key; the SDK's default if not given.</param>
    public WebSearchStore(string personFolder, ISecretProtector? protector = null)
    {
        _settingsFile = Path.Combine(personFolder, "preferences", "web-search.json");
        _keyFile = Path.Combine(personFolder, "keys", "web-search-brave.key");
        _protector = protector ?? SecretProtection.Default;
    }

    /// <summary>The store of one Fadenstack user (standalone: <c>standalone</c>).</summary>
    public static WebSearchStore ForUser(string userId, ISecretProtector? protector = null) => new(FadenstackPaths.User(userId), protector);

    /// <summary>The settings or the key changed (the agent asks for its tools again at its next turn).</summary>
    public event EventHandler? Changed;

    /// <summary>The key is written to disk, encrypted; false: it lives in memory until the app closes.</summary>
    public bool KeepsKeyOnDisk => _protector.Encrypts;

    /// <summary>The person's settings; <see cref="WebSearchSettings.Off"/> when none are kept or the file cannot be read.</summary>
    public WebSearchSettings Settings
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_settingsFile))
                    {
                        return WebSearchSettings.Off;
                    }
                    var file = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllBytes(_settingsFile), Json);
                    var provider = file?.Provider switch
                    {
                        "brave" => WebSearchProvider.Brave,
                        "searxng" => WebSearchProvider.SearXng,
                        _ => WebSearchProvider.None,
                    };
                    var url = Uri.TryCreate(file?.SearXngUrl, UriKind.Absolute, out var parsed) && WebSearchSettings.IsAllowedSearXngUrl(parsed) ? parsed : null;
                    return new WebSearchSettings { Provider = provider, SearXngUrl = url };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    return WebSearchSettings.Off;
                }
            }
        }
    }

    /// <summary>Keeps the person's choice. A SearXNG address that is not allowed (<see cref="WebSearchSettings.IsAllowedSearXngUrl"/>) is refused.</summary>
    /// <exception cref="ArgumentException">SearXNG without an allowed address.</exception>
    public void Save(WebSearchSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        if (settings.Provider == WebSearchProvider.SearXng && !WebSearchSettings.IsAllowedSearXngUrl(settings.SearXngUrl))
        {
            throw new ArgumentException("A SearXNG address must be https, or http on this computer, without a user or password.", nameof(settings));
        }
        var dto = new SettingsDto
        {
            Provider = settings.Provider switch { WebSearchProvider.Brave => "brave", WebSearchProvider.SearXng => "searxng", _ => "none" },
            SearXngUrl = settings.Provider == WebSearchProvider.SearXng ? settings.SearXngUrl!.AbsoluteUri : null,
        };
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            SecretProtection.WriteOwnerOnly(_settingsFile, JsonSerializer.SerializeToUtf8Bytes(dto, Json));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a Brave key is kept (on disk or, without a key store, in memory).</summary>
    public bool HasBraveKey
    {
        get
        {
            lock (_gate)
            {
                return _keyInMemory is not null || (KeepsKeyOnDisk && File.Exists(_keyFile));
            }
        }
    }

    /// <summary>Keeps the person's Brave key, encrypted; null or empty removes it. The key is never logged or shown back.</summary>
    public void SetBraveKey(string? key)
    {
        var value = key?.Trim();
        lock (_gate)
        {
            _keyInMemory = null;
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(_keyFile))
                {
                    File.Delete(_keyFile);
                }
            }
            else if (KeepsKeyOnDisk)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
                SecretProtection.WriteOwnerOnly(_keyFile, _protector.Protect(Encoding.UTF8.GetBytes(value)));
            }
            else
            {
                _keyInMemory = value;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Brave key, for the request only; null when none is kept or it cannot be decrypted.</summary>
    internal string? BraveKey()
    {
        lock (_gate)
        {
            if (_keyInMemory is not null)
            {
                return _keyInMemory;
            }
            try
            {
                return KeepsKeyOnDisk && File.Exists(_keyFile)
                    ? Encoding.UTF8.GetString(_protector.Unprotect(SecretProtection.ReadShared(_keyFile)))
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        }
    }

    // The file's keys are pinned with their names: the release build is obfuscated.
    private sealed class SettingsDto
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "none";

        [JsonPropertyName("searxng_url")]
        public string? SearXngUrl { get; set; }
    }
}

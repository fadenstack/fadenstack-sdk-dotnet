using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Storage;

namespace Fadenstack.UI.Services;

/// <summary>
/// Where the user's choices for the panel are kept, such as the language picked in its settings: one value per name.
/// <see cref="FilePreferenceStore"/> by default; a host passes its own (its application's settings) or
/// <see cref="MemoryPreferenceStore"/> to keep nothing.
/// </summary>
public interface IPreferenceStore
{
    string? Get(string name);

    /// <summary><c>null</c> forgets the value.</summary>
    void Set(string name, string? value);
}

/// <summary>Choices kept for as long as the process runs (tests, a host that keeps nothing).</summary>
public sealed class MemoryPreferenceStore : IPreferenceStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string name)
    {
        lock (_values)
        {
            return _values.TryGetValue(name, out var value) ? value : null;
        }
    }

    public void Set(string name, string? value)
    {
        lock (_values)
        {
            if (value is null)
            {
                _values.Remove(name);
            }
            else
            {
                _values[name] = value;
            }
        }
    }
}

/// <summary>
/// The user's choices in a JSON file: by default <c>preferences.json</c> in <see cref="FadenstackPaths.SettingsRoot"/>
/// (<c>%APPDATA%\Fadenstack</c> on Windows), shared by every
/// Fadenstack panel of the user on this computer, so a language picked in Word is Excel's too. A file that cannot be
/// read or written keeps nothing; the panel works on.
/// </summary>
public sealed class FilePreferenceStore : IPreferenceStore
{
    private static readonly object Gate = new();

    public FilePreferenceStore(string? path = null) => Path = path ?? DefaultPath;

    /// <summary><c>preferences.json</c> in <see cref="FadenstackPaths.SettingsRoot"/>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(FadenstackPaths.SettingsRoot, "preferences.json");

    public string Path { get; }

    public string? Get(string name)
    {
        lock (Gate)
        {
            return Read()?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
    }

    public void Set(string name, string? value)
    {
        lock (Gate)
        {
            try
            {
                var values = Read() ?? new JsonObject();
                if (value is null)
                {
                    values.Remove(name);
                }
                else
                {
                    values[name] = value;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var written = Path + ".tmp";
                File.WriteAllText(written, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(Path))
                {
                    File.Replace(written, Path, null);
                }
                else
                {
                    File.Move(written, Path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // not kept: the choice still holds for this panel
            }
        }
    }

    private JsonObject? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // unreadable: as if empty
        }
    }
}

/// <summary>The store a chat uses when the host passes none (the tests swap it for memory).</summary>
internal static class PreferenceDefaults
{
    internal static Func<IPreferenceStore> Create { get; set; } = () => new FilePreferenceStore();
}

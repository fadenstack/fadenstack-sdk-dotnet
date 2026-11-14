using System.Text.Json;
using Fadenstack.Storage;

namespace Fadenstack.Skills;

/// <summary>
/// The person's on/off switch per skill, kept in their settings. A skill is on until it is switched off. The module does not list an
/// off skill for the model (<see cref="SkillsOptions.Switches"/>), and the editor shows and sets it.
/// </summary>
public interface ISkillSwitches
{
    bool IsEnabled(string name);

    /// <summary>Switches a skill on or off; true when that changed something.</summary>
    bool Set(string name, bool enabled);
}

/// <summary>Switches kept for as long as the process runs (tests, a host that keeps nothing).</summary>
public sealed class MemorySkillSwitches : ISkillSwitches
{
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);

    public bool IsEnabled(string name)
    {
        lock (_off)
        {
            return !_off.Contains(name);
        }
    }

    public bool Set(string name, bool enabled)
    {
        lock (_off)
        {
            return enabled ? _off.Remove(name) : _off.Add(name);
        }
    }
}

/// <summary>
/// The switches in a JSON file, by default <c>skills.json</c> in <see cref="FadenstackPaths.SettingsRoot"/> (the preferences, which hold no
/// personal content): <c>{"disabled": ["name", ...]}</c>. A file that cannot be read means every skill is on; one that cannot be written
/// keeps the change for this run only.
/// </summary>
public sealed class FileSkillSwitches : ISkillSwitches
{
    private static readonly object Gate = new();
    private HashSet<string>? _cache;
    private bool _unsaved;

    public FileSkillSwitches(string? path = null) => Path = path ?? DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(FadenstackPaths.SettingsRoot, "skills.json");

    public string Path { get; }

    public bool IsEnabled(string name)
    {
        lock (Gate)
        {
            return !Load().Contains(name);
        }
    }

    public bool Set(string name, bool enabled)
    {
        lock (Gate)
        {
            var off = Load();
            if (!(enabled ? off.Remove(name) : off.Add(name)))
            {
                return false;
            }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new { disabled = off.OrderBy(n => n, StringComparer.Ordinal).ToArray() }));
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
                File.Move(temp, Path);
                _unsaved = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _unsaved = true; // kept for this run
            }
            _cache = off;
            return true;
        }
    }

    private HashSet<string> Load()
    {
        // The file is read on every call so that a change another panel made is seen; it is small.
        if (_unsaved && _cache is not null)
        {
            return _cache;
        }
        var off = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length < 256 * 1024)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("disabled", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                    {
                        off.Add(item.GetString()!);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return _cache ?? off;
        }
        _cache = off;
        return off;
    }
}

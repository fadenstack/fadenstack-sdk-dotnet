using System.Text.Json;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>
/// The level each person picked for each agent, on this device: one small file in the signed-in user's own folder
/// (<c>users\&lt;user&gt;\preferences\reasoning.json</c>; the shared <c>preferences</c> folder when the server names no
/// user). It holds a preference, not content, so it is not encrypted. Anything unreadable counts as no pick.
/// </summary>
internal sealed class ReasoningChoiceStore
{
    private readonly string _path;
    private readonly object _gate = new();

    internal ReasoningChoiceStore(string folder) => _path = Path.Combine(folder, "reasoning.json");

    internal ReasoningLevel? Get(string agent)
    {
        lock (_gate)
        {
            return Read().TryGetValue(agent, out var name) ? ReasoningLevels.Parse(name) : null;
        }
    }

    /// <summary>Keeps the pick; null forgets it.</summary>
    internal void Set(string agent, ReasoningLevel? level)
    {
        lock (_gate)
        {
            var all = Read();
            if (level is { } picked)
            {
                all[agent] = picked.ToWire();
            }
            else
            {
                all.Remove(agent);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!); // the user's data may have been deleted (FadenstackAgent.DeleteMyDataAsync)
                File.WriteAllText(_path, JsonSerializer.Serialize(all));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the pick still holds for this run; it is just not remembered
            }
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } all)
            {
                return new Dictionary<string, string>(all, StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // damaged or unreadable: no pick
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>How long a model thinks before it answers: what the person picks in the agent's UI.</summary>
public enum ReasoningLevel
{
    /// <summary>The agent's own: nothing is sent, the model decides.</summary>
    Auto = 0,

    /// <summary>No thinking: the model answers at once (<c>reasoning_effort</c> <c>none</c>).</summary>
    Off,

    Low,

    Medium,

    /// <summary>Thinks longest.</summary>
    High,
}

/// <summary>The wire form of a <see cref="ReasoningLevel"/> and its relation to the client's <see cref="ReasoningEffort"/>.</summary>
public static class ReasoningLevels
{
    /// <summary>All levels, in the order a menu lists them.</summary>
    public static IReadOnlyList<ReasoningLevel> All { get; } = new[]
    {
        ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High,
    };

    /// <summary>The level's name on the wire and in the profile: <c>auto</c>, <c>none</c>, <c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public static string ToWire(this ReasoningLevel level) => level switch
    {
        ReasoningLevel.Off => "none",
        ReasoningLevel.Low => "low",
        ReasoningLevel.Medium => "medium",
        ReasoningLevel.High => "high",
        _ => "auto",
    };

    /// <summary>The level a name stands for (<c>off</c> too, any case); null for anything else.</summary>
    public static ReasoningLevel? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "auto" => ReasoningLevel.Auto,
        "none" or "off" => ReasoningLevel.Off,
        "low" => ReasoningLevel.Low,
        "medium" => ReasoningLevel.Medium,
        "high" => ReasoningLevel.High,
        _ => null,
    };

    /// <summary>What a request carries for the level: null for <see cref="ReasoningLevel.Auto"/>, which sends no field.</summary>
    public static ReasoningEffort? ToEffort(this ReasoningLevel level) => level switch
    {
        ReasoningLevel.Off => ReasoningEffort.None,
        ReasoningLevel.Low => ReasoningEffort.Low,
        ReasoningLevel.Medium => ReasoningEffort.Medium,
        ReasoningLevel.High => ReasoningEffort.High,
        _ => null,
    };

    /// <summary>The level an app's <see cref="AgentBuilder.UseReasoning"/> comes closest to (extra high is High).</summary>
    public static ReasoningLevel FromEffort(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.None => ReasoningLevel.Off,
        ReasoningEffort.Low => ReasoningLevel.Low,
        ReasoningEffort.Medium => ReasoningLevel.Medium,
        ReasoningEffort.High or ReasoningEffort.ExtraHigh => ReasoningLevel.High,
        _ => ReasoningLevel.Auto,
    };

    /// <summary>The wire value a request carries for <paramref name="effort"/>, or <c>auto</c> when it carries none (for the records).</summary>
    internal static string Record(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => "auto",
    };
}

/// <summary>
/// The levels an agent offers and where it starts, from its profile's <c>ui.reasoning</c>
/// (<c>{"default": "none", "levels": ["none", "high"]}</c>). The server does not say which levels a model tells apart, so
/// the agent's version does. Without it, or with something unreadable, all five are offered and the default is Auto.
/// </summary>
public sealed class ReasoningChoices
{
    /// <summary>All five levels and no default of its own (so Auto, unless the app set one): what an agent offers when its profile says nothing usable.</summary>
    public static ReasoningChoices Everything { get; } = new(ReasoningLevels.All, null);

    internal ReasoningChoices(IReadOnlyList<ReasoningLevel> levels, ReasoningLevel? @default)
    {
        Levels = levels;
        Default = @default;
    }

    /// <summary>The levels offered, in menu order.</summary>
    public IReadOnlyList<ReasoningLevel> Levels { get; }

    /// <summary>The level the profile starts at; null when it names none (or one that is not offered).</summary>
    public ReasoningLevel? Default { get; }

    /// <summary>One level or none: there is nothing to pick, so the UI shows no control.</summary>
    public bool IsFixed => Levels.Count < 2;

    /// <summary>Exactly Off and one other level: the UI calls them "Thinking: Off / On".</summary>
    public bool IsSwitch => Levels.Count == 2 && Levels.Contains(ReasoningLevel.Off);

    /// <summary>The level of a two-level switch that is not Off; null otherwise.</summary>
    public ReasoningLevel? OnLevel => IsSwitch ? Levels.First(l => l != ReasoningLevel.Off) : null;

    public bool Offers(ReasoningLevel level) => Levels.Contains(level);

    /// <summary>Reads <c>ui.reasoning</c> out of a profile's <c>ui</c>; never throws on what a server wrote there.</summary>
    public static ReasoningChoices FromUi(IReadOnlyDictionary<string, JsonElement>? ui)
    {
        if (ui is null || !ui.TryGetValue("reasoning", out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return Everything;
        }
        var levels = new List<ReasoningLevel>();
        if (node.TryGetProperty("levels", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var named = new HashSet<ReasoningLevel>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && ReasoningLevels.Parse(item.GetString()) is { } level)
                {
                    named.Add(level);
                }
            }
            levels.AddRange(ReasoningLevels.All.Where(named.Contains));
        }
        if (levels.Count == 0)
        {
            levels.AddRange(ReasoningLevels.All);
        }
        ReasoningLevel? start = node.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String
            ? ReasoningLevels.Parse(d.GetString()) : null;
        return new ReasoningChoices(levels, start is { } s && levels.Contains(s) ? s : null);
    }
}

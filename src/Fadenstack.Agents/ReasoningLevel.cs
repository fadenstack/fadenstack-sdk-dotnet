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
/// The levels an agent offers: always Auto and Off, and Low, Medium and High up to the maximum the server may set in the
/// agent's profile, <c>ui.reasoning</c> (<c>{"max": "low"}</c>; the gateway's scale none, minimal, low, medium, high, xhigh
/// or max: minimal caps at none, the nearest level the SDK can send, and max sets no cap). Without a maximum, or
/// with one that is not readable, all five are offered and Auto sends no field. With one, Auto means the maximum.
/// <c>ui.reasoning.offered: false</c> (a standalone agent on a dialect with no thinking switch, F7) offers no level at all.
/// <c>ui.reasoning.thinks_when_off</c> (<c>"lowest"</c>: Off sends the model's lowest level; <c>"always"</c>: the model thinks
/// even when Off, F10) only changes what the Off item says.
/// </summary>
public sealed class ReasoningChoices
{
    /// <summary>All five levels and no maximum: what an agent offers when its profile says nothing usable.</summary>
    public static ReasoningChoices Everything { get; } = new(ReasoningLevels.All, null);

    /// <summary>No level: the agent's dialect sends no thinking switch, so a pick would change nothing (F7).</summary>
    public static ReasoningChoices None { get; } = new([], null);

    internal ReasoningChoices(IReadOnlyList<ReasoningLevel> levels, ReasoningEffort? max, string? thinksWhenOff = null)
    {
        Levels = levels;
        Max = max;
        ThinksWhenOff = thinksWhenOff;
    }

    /// <summary>
    /// The model thinks although Off is sent (the check found it, F10): <c>lowest</c> when Off then sends the model's lowest
    /// level, <c>always</c> when it thinks even with the off switch (a chat template has no lower level); null when Off turns it off.
    /// </summary>
    public string? ThinksWhenOff { get; }

    /// <summary>The levels offered, in menu order.</summary>
    public IReadOnlyList<ReasoningLevel> Levels { get; }

    /// <summary>The most the server lets the agent think; null when it sets none.</summary>
    public ReasoningEffort? Max { get; }

    public bool Offers(ReasoningLevel level) => Levels.Contains(level);

    /// <summary>The effort a request carries for <paramref name="effort"/>: lowered to the maximum when above it.</summary>
    public ReasoningEffort? Cap(ReasoningEffort? effort) =>
        effort is { } e && Max is { } max && Rank(e) > Rank(max) ? max : effort;

    /// <summary>Reads <c>ui.reasoning.max</c> out of a profile's <c>ui</c>; never throws on what a server wrote there.</summary>
    public static ReasoningChoices FromUi(IReadOnlyDictionary<string, JsonElement>? ui)
    {
        if (ui is null || !ui.TryGetValue("reasoning", out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return Everything;
        }
        if (node.TryGetProperty("offered", out var offered) && offered.ValueKind == JsonValueKind.False)
        {
            return None;
        }
        var thinks = node.TryGetProperty("thinks_when_off", out var when) && when.ValueKind == JsonValueKind.String
            && when.GetString() is "lowest" or "always" ? when.GetString() : null;
        var all = thinks is null ? Everything : new ReasoningChoices(ReasoningLevels.All, null, thinks);
        if (!node.TryGetProperty("max", out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return all;
        }
        var name = raw.GetString()?.Trim().ToLowerInvariant();
        if (name == "max")
        {
            // The top of the gateway's scale: the server lets the agent think as long as it likes.
            return all;
        }
        ReasoningEffort? max = name switch
        {
            "none" => ReasoningEffort.None,
            // The gateway's scale has "minimal" between none and low; the SDK cannot send it, so the cap is the level
            // below it the SDK can send. The server still caps every request itself.
            "minimal" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.ExtraHigh,
            _ => null,
        };
        if (max is not { } cap)
        {
            return all;
        }
        return new ReasoningChoices(ReasoningLevels.All.Where(l => l.ToEffort() is not { } e || Rank(e) <= Rank(cap)).ToList(), cap, thinks);
    }

    private static int Rank(ReasoningEffort effort) => effort == ReasoningEffort.None ? 0
        : effort == ReasoningEffort.Low ? 1 : effort == ReasoningEffort.Medium ? 2 : effort == ReasoningEffort.High ? 3 : 4;
}

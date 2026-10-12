namespace Faden.Tools;

/// <summary>What a tool does. The same names as the gateway's.</summary>
public enum ToolClass
{
    /// <summary>Nobody vouched for it: it always asks, and Read only hides it.</summary>
    Unknown = 0,

    /// <summary>No side effects.</summary>
    Read,

    /// <summary>Changes something that can be changed back.</summary>
    Write,

    /// <summary>Deletes, sends, pays, or cannot be undone.</summary>
    Destructive,
}

/// <summary>What the user lets tools do in a session. Every session starts in <see cref="Ask"/>.</summary>
public enum ToolMode
{
    Off = 0,
    ReadOnly = 1,
    Ask = 2,
    Auto = 3,
}

/// <summary>What a mode does with a tool.</summary>
public enum ToolModeDecision
{
    /// <summary>Runs without asking.</summary>
    Run,

    /// <summary>Waits for the user's approval.</summary>
    Ask,

    /// <summary>Not offered to the model, and refused if called anyway.</summary>
    Hidden,
}

/// <summary>The table, the wire names, and the narrowing rule -- as the gateway applies them.</summary>
public static class ToolModes
{
    /// <summary>
    /// <c>open_world</c> (reaches outside the organisation) is carried and shown, and no mode acts on it
    /// yet -- like the gateway's <c>OPEN_WORLD_ENFORCED</c>.
    /// </summary>
    public const bool OpenWorldEnforced = false;

    /// <summary>The header a request carries its session's mode in.</summary>
    public const string Header = "X-Faden-Tool-Mode";

    /// <summary>
    ///     mode        read  write   destructive  unknown
    ///     off         -     -       -            -
    ///     read_only   run   -       -            -
    ///     ask         run   ask     ask          ask
    ///     auto        run   run     ask          ask
    /// </summary>
    public static ToolModeDecision Decide(ToolMode mode, ToolClass toolClass, bool? openWorld = null)
    {
        // if (OpenWorldEnforced && openWorld == true) ...  -- a mode or an EE policy on external calls, later.
        return mode switch
        {
            ToolMode.Off => ToolModeDecision.Hidden,
            ToolMode.ReadOnly => toolClass == ToolClass.Read ? ToolModeDecision.Run : ToolModeDecision.Hidden,
            ToolMode.Ask => toolClass == ToolClass.Read ? ToolModeDecision.Run : ToolModeDecision.Ask,
            ToolMode.Auto => toolClass is ToolClass.Read or ToolClass.Write ? ToolModeDecision.Run : ToolModeDecision.Ask,
            _ => ToolModeDecision.Hidden,
        };
    }

    /// <summary>The most restrictive of the modes given (a ceiling never widens).</summary>
    public static ToolMode Narrowest(ToolMode mode, ToolMode? ceiling) =>
        ceiling is { } c && c < mode ? c : mode;

    public static string ToWire(this ToolMode mode) => mode switch
    {
        ToolMode.Off => "off",
        ToolMode.ReadOnly => "read_only",
        ToolMode.Ask => "ask",
        ToolMode.Auto => "auto",
        _ => "ask",
    };

    public static ToolMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => ToolMode.Off,
        "read_only" => ToolMode.ReadOnly,
        "ask" => ToolMode.Ask,
        "auto" => ToolMode.Auto,
        _ => null,
    };

    public static string ToWire(this ToolClass toolClass) => toolClass switch
    {
        ToolClass.Read => "read",
        ToolClass.Write => "write",
        ToolClass.Destructive => "destructive",
        _ => "unknown",
    };

    /// <summary>A contract's <c>risk</c>, or a class name; anything else is <see cref="ToolClass.Unknown"/>.</summary>
    public static ToolClass ParseClass(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "read" => ToolClass.Read,
        "write" => ToolClass.Write,
        "destructive" => ToolClass.Destructive,
        _ => ToolClass.Unknown,
    };
}

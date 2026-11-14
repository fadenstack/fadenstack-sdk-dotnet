namespace Fadenstack.Tools;

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

    /// <summary>
    /// Auto, plus what the conversation's Bypass grant covers (sdk/38 §4.1): destructive and unclassified calls run
    /// without asking. Only while the server holds such a grant for the conversation; without one it is Auto.
    /// </summary>
    Bypass = 4,
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
    /// <c>open_world</c> (reaches outside the organisation) is acted on: below <see cref="ToolMode.Auto"/> a call to an
    /// open-world tool asks (<see cref="Decide"/>), like the gateway's <c>OPEN_WORLD_ENFORCED</c> from 0.7.1.
    /// </summary>
    public const bool OpenWorldEnforced = true;

    /// <summary>The header a request carries its session's mode in.</summary>
    public const string Header = "X-Faden-Tool-Mode";

    /// <summary>
    ///     mode        read  write   destructive  unknown
    ///     off         -     -       -            -
    ///     read_only   run   -       -            -
    ///     ask         run   ask     ask          ask
    ///     auto        run   run     ask          ask
    ///     bypass      run   run     ask*         ask*      * run under the conversation's Bypass grant
    ///
    /// An open-world tool (<paramref name="openWorld"/>: the stricter of the tool's own mark and the gateway's) asks below
    /// Auto where the table says run: what it is given leaves the organisation, and injected text can put private data
    /// into it (sdk/51 F08 §4; OWASP Top 10 for LLM Applications 2025, LLM02, LLM06). Auto and Bypass run it.
    /// </summary>
    public static ToolModeDecision Decide(ToolMode mode, ToolClass toolClass, bool? openWorld = null)
    {
        var decision = mode switch
        {
            ToolMode.Off => ToolModeDecision.Hidden,
            ToolMode.ReadOnly => toolClass == ToolClass.Read ? ToolModeDecision.Run : ToolModeDecision.Hidden,
            ToolMode.Ask => toolClass == ToolClass.Read ? ToolModeDecision.Run : ToolModeDecision.Ask,
            ToolMode.Auto or ToolMode.Bypass => toolClass is ToolClass.Read or ToolClass.Write ? ToolModeDecision.Run : ToolModeDecision.Ask,
            _ => ToolModeDecision.Hidden,
        };
        return openWorld == true && decision == ToolModeDecision.Run && mode < ToolMode.Auto ? ToolModeDecision.Ask : decision;
    }

    /// <summary>
    /// Whether an approval of an open-world call may stand only for the conversation: below Auto it asks because what it is
    /// given leaves the organisation, so neither "always" nor a time grant answers it (sdk/51 F08 §4).
    /// </summary>
    public static bool ConversationOnly(ToolMode mode, bool openWorld) => openWorld && mode < ToolMode.Auto;

    /// <summary>The most restrictive of the modes given (a ceiling never widens).</summary>
    public static ToolMode Narrowest(ToolMode mode, ToolMode? ceiling) =>
        ceiling is { } c && c < mode ? c : mode;

    public static string ToWire(this ToolMode mode) => mode switch
    {
        ToolMode.Off => "off",
        ToolMode.ReadOnly => "read_only",
        ToolMode.Ask => "ask",
        ToolMode.Auto => "auto",
        ToolMode.Bypass => "bypass",
        _ => "ask",
    };

    public static ToolMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => ToolMode.Off,
        "read_only" => ToolMode.ReadOnly,
        "ask" => ToolMode.Ask,
        "auto" => ToolMode.Auto,
        "bypass" => ToolMode.Bypass,
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

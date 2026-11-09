namespace Fadenstack.Agents;

/// <summary>
/// The grant card (desktop 37 §5.3, 38 §7): the app asks the person to let the agent work alongside them -- read and
/// write in these places, for a while -- or to extend a running grant, showing only the difference. The card is drawn
/// by the app from the places and the classes, never from the model's wording.
/// </summary>
public sealed record GrantOffer
{
    /// <summary>Where the agent would work; the person may remove places before allowing.</summary>
    public IReadOnlyList<GrantPlace> Places { get; init; } = Array.Empty<GrantPlace>();

    /// <summary>What it may do there without asking: <c>read</c>, <c>write</c> (the agent's grant classes).</summary>
    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    /// <summary>The durations to pick from, within the agent's longest grant.</summary>
    public IReadOnlyList<TimeSpan> Durations { get; init; } = Array.Empty<TimeSpan>();

    /// <summary>What the card starts at (30 min, desktop 37 D2, or less when the agent's longest is shorter).</summary>
    public TimeSpan Preselected { get; init; }

    /// <summary>The app's reason, if it gives one (its own text, never the model's).</summary>
    public string? Reason { get; init; }

    /// <summary>The running grant this card extends; null for a new grant.</summary>
    public Fadenstack.Models.AgentGrant? Extending { get; init; }

    /// <summary>
    /// A Bypass card (sdk/09 L-58): it says what will no longer be asked -- destructive and unclassified tools, in this
    /// conversation -- what still asks (an admin's rules, tools that ask every time), what the server records of every
    /// call (<see cref="Evidence"/>), and for how long, with no extension. No places, no classes. A card that cannot say
    /// this answers <see cref="GrantAnswer.NotNow"/>.
    /// </summary>
    public bool Bypass { get; init; }

    /// <summary>
    /// The conversation the card is for, when it has one: an app with several chats on one handler draws it in that chat
    /// only. Null for a card offered before its conversation exists.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// For a Bypass card: what the server records of each call under it, as the profile says
    /// (<c>approvals.bypass.evidence</c>: <c>decisions</c>, <c>calls</c> or <c>results</c>; sdk/40 A4 decision 6).
    /// </summary>
    public string? Evidence { get; init; }
}

/// <summary>What the person answered on the grant card.</summary>
public sealed record GrantAnswer
{
    public bool Allowed { get; init; }

    /// <summary>The places the person kept (all of the offer's, unless they removed some).</summary>
    public IReadOnlyList<GrantPlace> Places { get; init; } = Array.Empty<GrantPlace>();

    /// <summary>The time picked: the grant's length, or for an extension how much longer.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The card's text as the person saw it; its SHA-256 goes to the server as evidence.</summary>
    public string? ShownText { get; init; }

    /// <summary>
    /// For a Bypass card: the person's own words on why, if they gave any (at most <see cref="ReasonLength"/> characters).
    /// Kept with the grant's record; administrators see it (sdk/09 L-59, L-63).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The longest reason the server keeps.</summary>
    public const int ReasonLength = 500;

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="ReasonLength"/> at a character's end: never between the two halves of a
    /// surrogate pair (an emoji, say), which the server could not store.
    /// </summary>
    public static string? Cut(string? text)
    {
        if (text is null || text.Length <= ReasonLength)
        {
            return text;
        }
        var end = char.IsHighSurrogate(text[ReasonLength - 1]) ? ReasonLength - 1 : ReasonLength;
        return text.Substring(0, end);
    }

    public static GrantAnswer NotNow { get; } = new();
}

/// <summary>
/// Draws the grant card and returns the person's answer. A UI's approval handler that also implements this is used for
/// it (the chat's <c>ApprovalBroker</c> does); without one, no grant is offered.
/// </summary>
public interface IGrantHandler
{
    Task<GrantAnswer> RequestGrantAsync(GrantOffer offer, CancellationToken cancellationToken);
}

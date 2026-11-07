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

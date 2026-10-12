namespace Fadenstack.Agents;

/// <summary>
/// Context the user attached to a message: a selection, a range, a slide, a file's text. It is sent with the
/// message and kept with it in the local transcript, so later turns still see what the question was about.
/// </summary>
public sealed record MessageAttachment
{
    /// <summary>What the user sees on the chip: "Sheet1!A1:D20", "Selection", "Slide 3".</summary>
    public string Title { get; init; } = "";

    /// <summary>The text the model gets (Markdown by default).</summary>
    public string Content { get; init; } = "";

    /// <summary>Where it comes from: a context provider's id (<c>excel</c>, <c>word</c>).</summary>
    public string? Source { get; init; }

    /// <summary>What it is: <c>selection</c>, <c>range</c>, <c>slide</c>, <c>document</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>An id the agent's context tools can read again, e.g. for the rows after a truncated range.</summary>
    public string? Reference { get; init; }

    public string MediaType { get; init; } = "text/markdown";
}

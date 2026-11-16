namespace Fadenstack.Agents;

/// <summary>
/// An answer the person copies out of the chat: its Markdown as text, and the same as HTML when the host has it. The
/// chat's own Copy and an app's own view of answers both hand it to <see cref="FadenstackAgent.MarkCopyAsync"/>.
/// TS: <c>AnswerCopy</c>.
/// </summary>
/// <param name="Text">The answer's Markdown, or the table as tab-separated values.</param>
/// <param name="Html">The same as HTML, or null for text only.</param>
/// <param name="Chat">The id of the chat the answer is in, for the transcript entry; null when there is none.</param>
/// <param name="Turn">The id of the turn the answer came from, to link the record to the turn's receipt; null when unknown.</param>
public sealed record AnswerCopy(string Text, string? Html = null, string? Chat = null, string? Turn = null)
{
    /// <summary>The text is code (a code block): code is exempt from marking. Default false.</summary>
    public bool IsCode { get; init; }

    /// <summary>
    /// The text is a Markdown file the person saves (a chat export), not a copy: it is marked in its front matter, and
    /// <see cref="MarkedCopy.Text"/> is the file with it. <see cref="Html"/> is not used. TS: <c>file</c>.
    /// </summary>
    public bool IsFile { get; init; }
}

/// <summary>
/// What goes on the clipboard: the text as it was, and the HTML with its mark when it was marked. TS: <c>MarkedCopy</c>.
/// </summary>
/// <param name="Text">The text to copy: the text as it was given (for a file, the file with its front matter).</param>
/// <param name="Html">The HTML to copy, marked when <paramref name="Marked"/>; null when none was given.</param>
/// <param name="RecordId">The id of the record signed for this copy; null when there is none.</param>
/// <param name="Marked">The HTML carries the mark.</param>
public sealed record MarkedCopy(string Text, string? Html, string? RecordId, bool Marked)
{
    /// <summary>
    /// The agent's policy refuses a copy that cannot be marked, and this one could not be: nothing is to be copied, and the
    /// chat says so. TS: <c>refused</c>.
    /// </summary>
    public bool Refused { get; init; }

    /// <summary>Why the copy needs no mark (a short label, code, an exempt kind of edit); null when it was not exempt. TS: <c>exemptBecause</c>.</summary>
    public string? ExemptBecause { get; init; }
}

/// <summary>
/// Marks what is copied out of a chat. <c>Fadenstack.Provenance</c> registers one when its module is on the builder
/// (<see cref="AgentBuilder.UseAnswerCopyMarker"/>). TS: <c>AnswerCopyMarker</c>.
/// </summary>
public interface IAnswerCopyMarker
{
    /// <summary>Marks <paramref name="copy"/>, with the same policy, record and report as a tool's placement.</summary>
    Task<MarkedCopy> MarkAsync(AnswerCopy copy, CancellationToken cancellationToken = default);
}

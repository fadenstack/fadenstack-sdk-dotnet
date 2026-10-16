namespace Fadenstack.Agents;

/// <summary>What goes with a message besides its text (<see cref="AgentSession.SendAsync(string, SendOptions, CancellationToken)"/>).</summary>
public sealed record SendOptions
{
    /// <summary>Context the user attached (a selection, a range, a picture).</summary>
    public IReadOnlyList<MessageAttachment> Attachments { get; init; } = Array.Empty<MessageAttachment>();

    /// <summary>
    /// Skills the user picked for this message (<see cref="FadenstackAgent.ListSkillsAsync"/>): every model call of the
    /// turn gets them, and the transcript keeps them with the message. The next message has none unless picked again.
    /// </summary>
    public IReadOnlyList<PickedSkill> Skills { get; init; } = Array.Empty<PickedSkill>();
}

/// <summary>A skill the user picked for a message: its slug for the server, its name for the transcript.</summary>
public sealed record PickedSkill(string Slug, string? Name = null);

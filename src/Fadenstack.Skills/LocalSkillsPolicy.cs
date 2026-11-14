using Fadenstack.Models;

namespace Fadenstack.Skills;

/// <summary>Whether local skills are on for an agent, and why (shown to the user when they are not).</summary>
public sealed record LocalSkillsDecision(bool Allowed, string Reason);

/// <summary>
/// Decides whether an agent may use skills kept on the device. An extension with further modes replaces the core policy
/// with its own; local configuration can only tighten this, never widen it.
/// </summary>
public interface ILocalSkillsPolicy
{
    LocalSkillsDecision Decide(AgentProfile profile);
}

/// <summary>
/// The core rule, the shape of <c>Fadenstack.Mcp</c>'s: the profile's <c>local_skills.mode</c> is <c>allow</c>, or the user's
/// <c>developer_mode</c> is on. Absent or unknown means off. A standalone agent's profile says <c>allow</c> unless the person
/// switched local skills off, and the machine policy has already turned the profile off where it forbids skills.
/// </summary>
public sealed class CoreLocalSkillsPolicy : ILocalSkillsPolicy
{
    public static CoreLocalSkillsPolicy Instance { get; } = new();

    public LocalSkillsDecision Decide(AgentProfile profile)
    {
        var settings = profile.LocalSkills;
        return settings.Mode switch
        {
            "allow" => new LocalSkillsDecision(true, "The agent allows local skills."),
            _ when settings.DeveloperMode => new LocalSkillsDecision(true, "Developer mode."),
            null or "" or "disable" => new LocalSkillsDecision(false, "The agent does not allow local skills."),
            var other => new LocalSkillsDecision(false, $"The agent's local skills mode '{other}' needs a policy this app does not have."),
        };
    }
}

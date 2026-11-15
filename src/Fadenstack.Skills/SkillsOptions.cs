using Fadenstack.Storage;

namespace Fadenstack.Skills;

/// <summary>A skill that ships inside the app (signed with it), as text: the standard fields, the body and its resource files.</summary>
public sealed record BundledSkill(string Name, string Description, string Instructions)
{
    /// <summary>Resource files by relative name (<c>references/style.md</c>) and their text: what <c>read_skill_resource</c> returns.</summary>
    public IReadOnlyDictionary<string, string> Resources { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The text of the skill's <c>faden.yaml</c> (keywords, preferred tools, priority, activation), or null: the skill is then listed as before and never
    /// pre-loaded. Text that cannot be read is ignored (and logged); the skill still works.
    /// </summary>
    public string? FadenYaml { get; init; }
}

/// <summary>What the skills module offers: folders and bundled skills. Which of them an agent may use is decided by its policy.</summary>
public sealed class SkillsOptions
{
    /// <summary>
    /// Whether the module reads the person's own skills, in the folder of whoever the agent's profile names:
    /// <c>users/&lt;user&gt;/skills</c> in the SDK's data folder, <c>users/standalone/skills</c> for the person's own AI
    /// (<see cref="FolderOf"/>). So two people on one OS account never see each other's skills, and deleting a person's data deletes
    /// theirs. A server that names no user has no person's folder. Default true.
    /// </summary>
    public bool OwnSkills { get; set; } = true;

    /// <summary>A person's own skills folder: <c>users/&lt;user&gt;/skills</c> in the SDK's data folder, created, readable by its owner only.</summary>
    public static string FolderOf(string userId) => FadenstackPaths.User(userId, "skills");

    /// <summary>
    /// Further folders that hold skill folders (<c>&lt;folder&gt;/&lt;name&gt;/SKILL.md</c>): the ones the person adds in the settings.
    /// A folder that does not exist is skipped. Under a machine policy that lists folders only those stay, the person's own included.
    /// </summary>
    public IList<string> Folders { get; } = new List<string>();

    /// <summary>Skills bundled with the app. They come first: a folder's skill never takes a bundled skill's name.</summary>
    public IList<BundledSkill> Bundled { get; } = new List<BundledSkill>();

    /// <summary>
    /// The on/off switch per skill: the module does not list an off skill for the model. Null: the person's own, beside their skills
    /// (<see cref="FileSkillSwitches.ForUser"/>, <c>users/&lt;user&gt;/preferences/skills.json</c>), once the module knows whose; every skill
    /// is on where there is no person (<see cref="OwnSkills"/> off, or a server that names no user). Set it to keep the switches elsewhere.
    /// </summary>
    public ISkillSwitches? Switches { get; set; }

    /// <summary>Decides from the agent's profile whether local skills are on; null: <see cref="CoreLocalSkillsPolicy"/>.</summary>
    public ILocalSkillsPolicy? Policy { get; set; }

    /// <summary>Most skills a message matches by keyword, file type or tag (core's <c>max_skills</c>, 3), beside the ones it names.</summary>
    public int MaxMatched { get; set; } = SkillResolver.MaxMatched;

    /// <summary>Most skills a message may name with <c>$slug</c> (core's <c>max_explicit_skills</c>, 5).</summary>
    public int MaxNamed { get; set; } = SkillResolver.MaxNamed;

    /// <summary>Most skills whose instructions are put in a turn without the model asking (1; 0 turns pre-loading off).</summary>
    public int MaxPreloaded { get; set; } = SkillResolver.MaxPreloaded;
}

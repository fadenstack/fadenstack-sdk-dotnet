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
    /// <summary>The person's own skills: <c>&lt;DataRoot&gt;/skills</c>, per OS user. Add it to <see cref="Folders"/>.</summary>
    public static string UserFolder => Path.Combine(FadenstackPaths.DataRoot, "skills");

    /// <summary>
    /// Folders that hold skill folders (<c>&lt;folder&gt;/&lt;name&gt;/SKILL.md</c>): the person's own, and the ones they add in the
    /// settings. A folder that does not exist is skipped. Under a machine policy that lists folders only those stay.
    /// </summary>
    public IList<string> Folders { get; } = new List<string>();

    /// <summary>Skills bundled with the app. They come first: a folder's skill never takes a bundled skill's name.</summary>
    public IList<BundledSkill> Bundled { get; } = new List<BundledSkill>();

    /// <summary>Decides from the agent's profile whether local skills are on; null: <see cref="CoreLocalSkillsPolicy"/>.</summary>
    public ILocalSkillsPolicy? Policy { get; set; }

    /// <summary>Most skills a message matches by keyword, file type or tag (core's <c>max_skills</c>, 3), beside the ones it names.</summary>
    public int MaxMatched { get; set; } = SkillResolver.MaxMatched;

    /// <summary>Most skills a message may name with <c>$slug</c> (core's <c>max_explicit_skills</c>, 5).</summary>
    public int MaxNamed { get; set; } = SkillResolver.MaxNamed;

    /// <summary>Most skills whose instructions are put in a turn without the model asking (1; 0 turns pre-loading off).</summary>
    public int MaxPreloaded { get; set; } = SkillResolver.MaxPreloaded;
}

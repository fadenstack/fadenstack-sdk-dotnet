using System.Text;

namespace Fadenstack.SkillAuthoring;

/// <summary>
/// What the skill editor and the skills module share (sdk/51 F04). The layers keep the UI layer from referencing a module, and a
/// module from referencing the UI layer, so the contracts sit here, in the core: <c>Fadenstack.UI</c> builds its view models on
/// <see cref="ISkillWorkshop"/>, and <c>Fadenstack.Skills</c> implements it over the person's skill folders.
/// </summary>
public static class SkillLimits
{
    /// <summary>The name: 1 to 64 characters (core's <c>NAME_MAX</c>).</summary>
    public const int NameMax = 64;

    /// <summary>The description (core's <c>DESCRIPTION_MAX</c>).</summary>
    public const int DescriptionMax = 1024;

    public const int CompatibilityMax = 500;

    /// <summary>The body's advisory size in tokens (a warning, not a refusal); a token is counted as 4 characters.</summary>
    public const int BodyAdvisoryTokens = 5000;

    public const int CharactersPerToken = 4;

    /// <summary>A file of a skill, in bytes.</summary>
    public const int FileMaxBytes = 1024 * 1024;

    /// <summary>All the files of a skill, in bytes (what a zip may expand to).</summary>
    public const int TotalMaxBytes = 5 * 1024 * 1024;

    /// <summary>The files of a skill, SKILL.md and faden.yaml included.</summary>
    public const int FilesMax = 100;

    /// <summary>A zip that expands to more than this many times its size is refused.</summary>
    public const int ExpansionRatioMax = 100;

    /// <summary>The longest path of a resource, and of one of its parts.</summary>
    public const int PathMax = 200;

    public const int PathPartMax = 100;

    /// <summary>The longest zip taken in (the compressed file).</summary>
    public const int ZipMaxBytes = 6 * 1024 * 1024;
}

/// <summary>Where a skill comes from.</summary>
public enum SkillOrigin
{
    /// <summary>Inside the app, signed with it: read only.</summary>
    Bundled,

    /// <summary>The person's own, <c>&lt;DataRoot&gt;/skills/&lt;name&gt;/</c>: not reviewed by anyone.</summary>
    Own,

    /// <summary>The organisation's, from the server (governed mode): read only, never its instructions.</summary>
    Organisation,
}

/// <summary>One skill in the library.</summary>
public sealed record SkillEntry(string Name, string Description, SkillOrigin Origin, bool Enabled)
{
    public bool IsReadOnly => Origin != SkillOrigin.Own;

    /// <summary>The on/off switch is the person's for bundled and own skills; the organisation decides its own.</summary>
    public bool CanSwitch => Origin != SkillOrigin.Organisation;

    /// <summary>Own skills carry the label "not reviewed".</summary>
    public bool NotReviewed => Origin == SkillOrigin.Own;

    /// <summary>The folder's SKILL.md cannot be read or breaks a rule: the module does not list it; the person can open it to fix or delete it.</summary>
    public bool IsBroken { get; init; }
}

/// <summary>A file of a skill (a resource): its path relative to the skill's folder, with forward slashes, and its content.</summary>
public sealed record SkillFile(string Path, byte[] Bytes)
{
    public long Length => Bytes.Length;

    /// <summary>The content as UTF-8 text when it is text (no NUL byte, valid UTF-8); null for a binary file.</summary>
    public string? Text
    {
        get
        {
            if (Array.IndexOf(Bytes, (byte)0) >= 0)
            {
                return null;
            }
            try
            {
                return new UTF8Encoding(false, true).GetString(Bytes);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    public static SkillFile FromText(string path, string text) => new(path, new UTF8Encoding(false).GetBytes(text));
}

/// <summary>What a skill's <c>faden.yaml</c> says (Fadenstack's extras: filter, rank, pre-load, preferred tools). The defaults are what no file says.</summary>
public sealed record SkillExtensionFields
{
    public const int DefaultPriority = 50;

    public string Title { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>0 to 100.</summary>
    public int Priority { get; init; } = DefaultPriority;

    /// <summary><c>auto</c> (when the message matches), <c>always</c>, <c>manual</c> (only when named).</summary>
    public string Activation { get; init; } = "auto";

    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FileTypes { get; init; } = Array.Empty<string>();

    /// <summary>A hint in the prompt, nothing else: it grants nothing.</summary>
    public IReadOnlyList<string> PreferredTools { get; init; } = Array.Empty<string>();

    /// <summary>Read and kept, never used (core's fields).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenTools { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> KnowledgeSources { get; init; } = Array.Empty<string>();

    /// <summary>Keys of the file this editor does not know: a Save would drop them.</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = Array.Empty<string>();

    /// <summary>Whether the fields say nothing (no <c>faden.yaml</c> is written).</summary>
    public bool IsEmpty => Title.Length == 0 && Tags.Count == 0 && Priority == DefaultPriority && Activation == "auto" && Keywords.Count == 0
        && FileTypes.Count == 0 && PreferredTools.Count == 0 && AllowedTools.Count == 0 && ForbiddenTools.Count == 0 && KnowledgeSources.Count == 0;
}

/// <summary>A skill as the editor works on it: the six standard fields, the instructions, the <c>faden.yaml</c> fields and the resource files.</summary>
public sealed record SkillDocument
{
    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string License { get; init; } = "";

    public string Compatibility { get; init; } = "";

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>The experimental <c>allowed-tools</c> field, as written (space-separated).</summary>
    public string AllowedTools { get; init; } = "";

    /// <summary>The SKILL.md's body: the instructions, Markdown.</summary>
    public string Instructions { get; init; } = "";

    public SkillExtensionFields Extension { get; init; } = new();

    /// <summary>The files beside SKILL.md and faden.yaml.</summary>
    public IReadOnlyList<SkillFile> Resources { get; init; } = Array.Empty<SkillFile>();

    /// <summary>Frontmatter keys beyond the six: a document that has any cannot be saved (claude.ai rejects them, and a clean SKILL.md moves to any agent).</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = Array.Empty<string>();
}

public enum SkillSeverity
{
    /// <summary>Saving is refused.</summary>
    Error,

    /// <summary>Said, not refused.</summary>
    Warning,
}

/// <summary>What a check found.</summary>
public enum SkillIssueCode
{
    NameMissing,
    NameForm,
    NameTooLong,
    NameNotFolder,
    NameReserved,
    DescriptionMissing,
    DescriptionTooLong,
    CompatibilityTooLong,
    UnknownKey,
    InvisibleCharacter,
    ControlCharacter,
    BodyEmpty,
    BodyLarge,
    TotalTooLarge,
    FileTooLarge,
    TooManyFiles,
    ScriptFile,
    UnsafePath,
    DuplicatePath,
    LinkEntry,
    ZipBomb,
    NotAZip,
    SkillMdMissing,
    SkillMdUnreadable,
    FadenYamlUnreadable,
    FadenYamlUnknownKey,
    ReadFailed,
}

/// <summary>One finding of a check: what, how bad, where (a field or a file), and the numbers or text that go with it.</summary>
public sealed record SkillIssue(SkillIssueCode Code, SkillSeverity Severity, string? Subject = null, int Limit = 0, int Actual = 0, string? Detail = null)
{
    public bool IsError => Severity == SkillSeverity.Error;
}

/// <summary>The result of the checks before saving (one validator, used by the editor, the import and the draft).</summary>
public sealed record SkillCheck(IReadOnlyList<SkillIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.IsError);

    public bool Ok => !HasErrors;
}

public enum SkillSaveStatus
{
    Saved,

    /// <summary>A skill of that name exists and the person has not confirmed replacing it.</summary>
    Exists,

    /// <summary>The checks found errors; nothing was written.</summary>
    Refused,

    /// <summary>A bundled or organisation skill, or a name that is not the person's to write.</summary>
    NotAllowed,

    /// <summary>The folder could not be written; nothing was changed.</summary>
    Failed,
}

public sealed record SkillSaveResult(SkillSaveStatus Status, IReadOnlyList<SkillIssue> Issues, string? Name = null)
{
    public bool Saved => Status == SkillSaveStatus.Saved;
}

/// <summary>A skill read from the library, or why it could not be.</summary>
public sealed record SkillReadResult(SkillDocument? Document, SkillOrigin Origin, IReadOnlyList<SkillIssue> Issues);

/// <summary>What an import found; nothing is written until <see cref="ISkillWorkshop.CommitImportAsync"/> after the person confirms.</summary>
public sealed record ImportAnalysis(string Source, SkillDocument? Document, IReadOnlyList<SkillIssue> Issues, bool WouldReplace, int FileCount, long TotalBytes)
{
    /// <summary>The import can go ahead (it may still need the person's confirmation to replace).</summary>
    public bool Ok => Document is not null && Issues.All(i => !i.IsError);
}

/// <summary>A faden.yaml, read.</summary>
public sealed record SkillExtensionParse(SkillExtensionFields? Fields, IReadOnlyList<SkillIssue> Issues);

/// <summary>One skill the test step found for a prompt, and why.</summary>
public sealed record SkillTestMatch(string Name, string MatchedBy, int Score);

/// <summary>What the pre-selection does for a prompt (the module's own resolver, over the edited skill and the person's others).</summary>
public sealed record SkillTestResult(IReadOnlyList<string> Listed, IReadOnlyList<SkillTestMatch> Matches, IReadOnlyList<string> Preloaded,
    string? ModelInstructions, bool EditedListed)
{
    /// <summary>The name of <c>load_skill</c>, the one tool the optional model call is offered.</summary>
    public const string LoadSkillTool = "load_skill";
}

/// <summary>
/// The person's skills for the editor: list, read, check, save (atomically), delete, switch on and off, import, export and test. A module
/// implements it; nothing here saves on its own: <see cref="SaveAsync"/> is only called when the person pressed Save.
/// </summary>
public interface ISkillWorkshop
{
    /// <summary>The bundled, own and (governed mode) organisation skills.</summary>
    Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<SkillReadResult> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The checks before saving. <paramref name="folderName"/>: the folder the skill sits in, when it already does.</summary>
    SkillCheck Check(SkillDocument document, string? folderName = null);

    /// <summary>Checks, then writes the folder atomically (a temporary folder, then a move). <paramref name="previousName"/>: the skill it was renamed from.</summary>
    Task<SkillSaveResult> SaveAsync(SkillDocument document, string? previousName = null, bool replaceExisting = false, CancellationToken cancellationToken = default);

    /// <summary>Deletes an own skill's folder; false when there is none or it is not the person's.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Switches a bundled or own skill on or off in the person's settings; the module does not list an off skill.</summary>
    Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Reads and checks a folder or a zip; writes nothing.</summary>
    Task<ImportAnalysis> AnalyzeImportAsync(string path, CancellationToken cancellationToken = default);

    Task<ImportAnalysis> AnalyzeZipAsync(byte[] zip, string source, CancellationToken cancellationToken = default);

    /// <summary>Writes what <see cref="AnalyzeImportAsync"/> found, checking it again; an existing skill of the name is replaced only when <paramref name="replaceExisting"/>.</summary>
    Task<SkillSaveResult> CommitImportAsync(ImportAnalysis analysis, bool replaceExisting, CancellationToken cancellationToken = default);

    /// <summary>One skill as a zip (<c>&lt;name&gt;/SKILL.md</c>, <c>faden.yaml</c> and the resources); null when there is none.</summary>
    Task<byte[]?> ExportAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Reads the text of a <c>faden.yaml</c> into fields.</summary>
    SkillExtensionParse ParseExtension(string text);

    /// <summary>For a prompt: which skills the pre-selection lists and pre-loads, with <paramref name="edited"/> in place of the saved skill it came from (<paramref name="editedFrom"/>, when it was renamed).</summary>
    SkillTestResult Test(string prompt, SkillDocument? edited, string? editedFrom = null);
}

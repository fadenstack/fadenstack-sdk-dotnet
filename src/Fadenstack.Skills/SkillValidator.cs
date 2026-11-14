using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fadenstack.SkillAuthoring;

namespace Fadenstack.Skills;

/// <summary>
/// The checks before a skill is saved (sdk/51 F04): one validator, used by the editor, the import and the draft. A skill's text is
/// untrusted input (someone's folder, a zip, a model's draft), so every rule here is about what may come in: the six standard fields
/// only, the name as the folder, the lengths, invisible and control characters, the sizes and the number of files, safe resource
/// paths, no script kept as runnable (scripts never run here), and a <c>faden.yaml</c> that the module's own reader accepts.
/// </summary>
internal static class SkillValidator
{
    private static readonly Regex NameForm = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Files that are, or look like, programs or scripts: refused, since no script ever runs here and a kept one invites running it by hand.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish", ".vbs", ".vbe", ".wsf", ".wsh", ".js", ".mjs", ".cjs", ".jse", ".py", ".pyw",
        ".rb", ".pl", ".php", ".lua", ".exe", ".dll", ".com", ".msi", ".scr", ".so", ".dylib", ".jar", ".app", ".lnk", ".reg", ".hta", ".cpl",
    };

    private static readonly string[] ScriptFolders = { "scripts" };

    /// <summary>Characters the eye does not see: zero width, direction controls, soft hyphen, invisible operators, line and paragraph separators.</summary>
    private static bool IsInvisible(int c) =>
        c == 0x200B || c == 0x200C || c == 0x200D || c == 0x2060 || c == 0xFEFF || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069)
        || c == 0x200E || c == 0x200F || c == 0x061C || c == 0x00AD || c == 0x180E || (c >= 0x2061 && c <= 0x2064) || c == 0x2028 || c == 0x2029
        || c == 0x034F || (c >= 0xFFF9 && c <= 0xFFFB) || (c >= 0xE0000 && c <= 0xE007F);

    private static bool IsControl(int c) => (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F || (c >= 0x80 && c <= 0x9F);

    /// <summary>Notes the invisible and control characters of <paramref name="text"/> (each distinct one once, at most five).</summary>
    internal static void Scan(string? text, string subject, List<SkillIssue> issues, bool allowLeadingBom = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var seen = new HashSet<int>();
        for (var i = 0; i < text!.Length && seen.Count < 5; i++)
        {
            var c = (int)text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                c = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            if (i == 0 && c == 0xFEFF && allowLeadingBom)
            {
                continue;
            }
            if (!seen.Contains(c) && (IsInvisible(c) || IsControl(c)))
            {
                seen.Add(c);
                issues.Add(new SkillIssue(IsInvisible(c) ? SkillIssueCode.InvisibleCharacter : SkillIssueCode.ControlCharacter, SkillSeverity.Error, subject,
                    Detail: "U+" + c.ToString("X4", CultureInfo.InvariantCulture)));
            }
        }
    }

    /// <summary>Why a resource path is not safe (null when it is): relative, forward slashes, no <c>..</c>, no drive, no empty or hidden-by-dots part, not a name SKILL.md or faden.yaml keeps.</summary>
    internal static string? PathProblem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "empty";
        }
        var text = path!;
        if (text.Length > SkillLimits.PathMax)
        {
            return "too long";
        }
        if (text.IndexOf('\\') >= 0 || text[0] == '/' || text.IndexOf(':') >= 0 || text.IndexOf('\0') >= 0)
        {
            return "absolute or not a forward-slash relative path";
        }
        var parts = text.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.Length > SkillLimits.PathPartMax || part.TrimEnd(' ', '.').Length != part.Length)
            {
                return "has an empty, dot or too long part";
            }
            if (part.Any(c => IsInvisible(c) || IsControl(c) || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|'))
            {
                return "has a character a file name may not have";
            }
        }
        if (parts.Length == 1 && (string.Equals(text, "SKILL.md", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "faden.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return "reserved";
        }
        return null;
    }

    internal static bool IsScript(string path)
    {
        var parts = path.Split('/');
        return ScriptExtensions.Contains(Path.GetExtension(path)) || (parts.Length > 1 && ScriptFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The checks; <paramref name="folderName"/> is the folder the skill sits in (null for a new one), <paramref name="reserved"/> the bundled names a folder skill may not take.</summary>
    internal static SkillCheck Check(SkillDocument document, string? folderName, ISet<string>? reserved = null)
    {
        var issues = new List<SkillIssue>();
        var name = document.Name ?? "";
        if (name.Length == 0)
        {
            issues.Add(new SkillIssue(SkillIssueCode.NameMissing, SkillSeverity.Error, "name"));
        }
        else
        {
            if (name.Length > SkillLimits.NameMax)
            {
                issues.Add(new SkillIssue(SkillIssueCode.NameTooLong, SkillSeverity.Error, "name", SkillLimits.NameMax, name.Length));
            }
            if (!NameForm.IsMatch(name))
            {
                issues.Add(new SkillIssue(SkillIssueCode.NameForm, SkillSeverity.Error, "name"));
            }
            else if (folderName is not null && !string.Equals(name, folderName, StringComparison.Ordinal))
            {
                issues.Add(new SkillIssue(SkillIssueCode.NameNotFolder, SkillSeverity.Error, "name", Detail: folderName));
            }
            if (reserved is not null && reserved.Contains(name))
            {
                issues.Add(new SkillIssue(SkillIssueCode.NameReserved, SkillSeverity.Error, "name"));
            }
        }
        var description = document.Description ?? "";
        if (description.Trim().Length == 0)
        {
            issues.Add(new SkillIssue(SkillIssueCode.DescriptionMissing, SkillSeverity.Error, "description"));
        }
        else if (description.Length > SkillLimits.DescriptionMax)
        {
            issues.Add(new SkillIssue(SkillIssueCode.DescriptionTooLong, SkillSeverity.Error, "description", SkillLimits.DescriptionMax, description.Length));
        }
        if ((document.Compatibility ?? "").Length > SkillLimits.CompatibilityMax)
        {
            issues.Add(new SkillIssue(SkillIssueCode.CompatibilityTooLong, SkillSeverity.Error, "compatibility", SkillLimits.CompatibilityMax, document.Compatibility!.Length));
        }
        foreach (var key in document.UnknownKeys ?? Array.Empty<string>())
        {
            issues.Add(new SkillIssue(SkillIssueCode.UnknownKey, SkillSeverity.Error, "SKILL.md", Detail: key));
        }

        Scan(name, "name", issues);
        Scan(description, "description", issues);
        Scan(document.License, "license", issues);
        Scan(document.Compatibility, "compatibility", issues);
        Scan(document.AllowedTools, "allowed-tools", issues);
        foreach (var pair in document.Metadata ?? new Dictionary<string, string>())
        {
            Scan(pair.Key, "metadata", issues);
            Scan(pair.Value, "metadata", issues);
        }
        Scan(document.Instructions, "instructions", issues);

        var body = (document.Instructions ?? "").Trim();
        if (body.Length == 0)
        {
            issues.Add(new SkillIssue(SkillIssueCode.BodyEmpty, SkillSeverity.Warning, "instructions"));
        }
        else if (body.Length / SkillLimits.CharactersPerToken > SkillLimits.BodyAdvisoryTokens)
        {
            issues.Add(new SkillIssue(SkillIssueCode.BodyLarge, SkillSeverity.Warning, "instructions", SkillLimits.BodyAdvisoryTokens, body.Length / SkillLimits.CharactersPerToken));
        }

        CheckExtension(document.Extension ?? new SkillExtensionFields(), issues);
        CheckFiles(document, issues);
        return new SkillCheck(issues);
    }

    private static void CheckExtension(SkillExtensionFields fields, List<SkillIssue> issues)
    {
        foreach (var key in fields.UnknownKeys)
        {
            issues.Add(new SkillIssue(SkillIssueCode.FadenYamlUnknownKey, SkillSeverity.Warning, "faden.yaml", Detail: key));
        }
        var yaml = SkillMarkdown.WriteExtension(fields);
        if (yaml is null)
        {
            return;
        }
        Scan(yaml, "faden.yaml", issues);
        if (issues.Any(i => i.Subject == "faden.yaml" && i.IsError))
        {
            return;
        }
        try
        {
            SkillExtension.Parse(yaml);
        }
        catch (FadenYamlException e)
        {
            issues.Add(new SkillIssue(SkillIssueCode.FadenYamlUnreadable, SkillSeverity.Error, "faden.yaml", Detail: e.Message));
        }
        catch (RegexMatchTimeoutException)
        {
            issues.Add(new SkillIssue(SkillIssueCode.FadenYamlUnreadable, SkillSeverity.Error, "faden.yaml", Detail: "a keyword could not be prepared"));
        }
    }

    private static void CheckFiles(SkillDocument document, List<SkillIssue> issues)
    {
        var skillMd = Encoding.UTF8.GetByteCount(SkillMarkdown.Write(document));
        var yaml = SkillMarkdown.WriteExtension(document.Extension ?? new SkillExtensionFields());
        long total = skillMd + (yaml is null ? 0 : Encoding.UTF8.GetByteCount(yaml));
        var count = 1 + (yaml is null ? 0 : 1);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Resources ?? Array.Empty<SkillFile>())
        {
            count++;
            total += file.Bytes.Length;
            if (PathProblem(file.Path) is { } problem)
            {
                issues.Add(new SkillIssue(SkillIssueCode.UnsafePath, SkillSeverity.Error, Shorten(file.Path), Detail: problem));
                continue;
            }
            if (!paths.Add(file.Path))
            {
                issues.Add(new SkillIssue(SkillIssueCode.DuplicatePath, SkillSeverity.Error, file.Path));
            }
            if (file.Bytes.Length > SkillLimits.FileMaxBytes)
            {
                issues.Add(new SkillIssue(SkillIssueCode.FileTooLarge, SkillSeverity.Error, file.Path, SkillLimits.FileMaxBytes, file.Bytes.Length));
            }
            else if (file.Text is { } text)
            {
                Scan(text, file.Path, issues, allowLeadingBom: true);
            }
            if (IsScript(file.Path))
            {
                issues.Add(new SkillIssue(SkillIssueCode.ScriptFile, SkillSeverity.Error, file.Path));
            }
        }
        if (count > SkillLimits.FilesMax)
        {
            issues.Add(new SkillIssue(SkillIssueCode.TooManyFiles, SkillSeverity.Error, Limit: SkillLimits.FilesMax, Actual: count));
        }
        if (total > SkillLimits.TotalMaxBytes)
        {
            issues.Add(new SkillIssue(SkillIssueCode.TotalTooLarge, SkillSeverity.Error, Limit: SkillLimits.TotalMaxBytes, Actual: (int)Math.Min(total, int.MaxValue)));
        }
    }

    private static string Shorten(string? path) => path is null ? "" : path.Length > 60 ? path.Substring(0, 60) : path;
}

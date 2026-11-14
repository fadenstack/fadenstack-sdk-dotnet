using System.Text;
using System.Text.RegularExpressions;
using Fadenstack.SkillAuthoring;

namespace Fadenstack.Skills;

/// <summary>
/// The person's skill folders, <c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c> and the files beside it: list, read, write, delete. A write is atomic:
/// the folder is built in a staging folder next to the root (so that the module, which walks the root, never sees a half-written skill),
/// then moved into place; an existing skill is moved aside first and put back if the move fails. Links are never followed or written.
/// A name that is not <c>[a-z0-9]+(-[a-z0-9]+)*</c> never reaches the file system.
/// </summary>
internal sealed class SkillFolderStore
{
    private static readonly Regex NameForm = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private const int MaxDepth = 8;

    internal SkillFolderStore(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal string Root { get; }

    /// <summary>Where a skill is built before it is moved into <see cref="Root"/>: beside it, on the same volume.</summary>
    internal string Staging => Root + ".staging";

    internal static bool ValidName(string? name) => name is { Length: > 0 and <= SkillLimits.NameMax } && NameForm.IsMatch(name);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The skill folders under the root (one level), links and hidden folders left out, by name.</summary>
    internal IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        try
        {
            if (!Directory.Exists(Root))
            {
                return names;
            }
            foreach (var directory in Directory.GetDirectories(Root))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith(".", StringComparison.Ordinal) && !IsLink(directory) && File.Exists(Path.Combine(directory, "SKILL.md")))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unreadable folder lists nothing
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    internal bool Exists(string name) => ValidName(name) && Directory.Exists(Path.Combine(Root, name));

    /// <summary>The SKILL.md text and the faden.yaml text of a skill (the head, without its resources), or why not.</summary>
    internal (string? SkillMd, string? Yaml, SkillIssue? Problem) ReadHead(string name)
    {
        if (!ValidName(name))
        {
            return (null, null, new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, name));
        }
        var directory = Path.Combine(Root, name);
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory))
            {
                return (null, null, new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, name));
            }
            var skillMd = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillMd) || IsLink(skillMd))
            {
                return (null, null, new SkillIssue(SkillIssueCode.SkillMdMissing, SkillSeverity.Error, name));
            }
            if (new FileInfo(skillMd).Length > SkillLimits.FileMaxBytes)
            {
                return (null, null, new SkillIssue(SkillIssueCode.FileTooLarge, SkillSeverity.Error, "SKILL.md", SkillLimits.FileMaxBytes));
            }
            var text = ReadText(skillMd);
            string? yaml = null;
            var yamlPath = Path.Combine(directory, "faden.yaml");
            if (File.Exists(yamlPath) && !IsLink(yamlPath) && new FileInfo(yamlPath).Length <= FadenYaml.MaxBytes + 3)
            {
                yaml = ReadText(yamlPath);
            }
            return (text, yaml, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, name));
        }
    }

    /// <summary>The files beside SKILL.md and faden.yaml, never through a link; problems (a file too large, a link, too many) come back as issues.</summary>
    internal (IReadOnlyList<SkillFile> Files, IReadOnlyList<SkillIssue> Issues) ReadResources(string name)
    {
        var files = new List<SkillFile>();
        var issues = new List<SkillIssue>();
        var root = Path.Combine(Root, name);
        long total = 0;
        try
        {
            Walk(root, "", 0, files, issues, ref total);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, name));
        }
        return (files, issues);
    }

    private static void Walk(string directory, string prefix, int depth, List<SkillFile> files, List<SkillIssue> issues, ref long total)
    {
        if (depth > MaxDepth)
        {
            issues.Add(new SkillIssue(SkillIssueCode.UnsafePath, SkillSeverity.Error, prefix.TrimEnd('/'), Detail: "too deep"));
            return;
        }
        foreach (var entry in Directory.GetFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var relative = prefix + name;
            if (IsLink(entry))
            {
                issues.Add(new SkillIssue(SkillIssueCode.LinkEntry, SkillSeverity.Warning, relative));
                continue;
            }
            if (Directory.Exists(entry))
            {
                Walk(entry, relative + "/", depth + 1, files, issues, ref total);
                continue;
            }
            if (depth == 0 && (name == "SKILL.md" || name == "faden.yaml"))
            {
                continue;
            }
            if (files.Count >= SkillLimits.FilesMax)
            {
                issues.Add(new SkillIssue(SkillIssueCode.TooManyFiles, SkillSeverity.Error, Limit: SkillLimits.FilesMax));
                return;
            }
            var length = new FileInfo(entry).Length;
            if (length > SkillLimits.FileMaxBytes)
            {
                issues.Add(new SkillIssue(SkillIssueCode.FileTooLarge, SkillSeverity.Error, relative, SkillLimits.FileMaxBytes, (int)Math.Min(length, int.MaxValue)));
                continue;
            }
            total += length;
            if (total > SkillLimits.TotalMaxBytes)
            {
                issues.Add(new SkillIssue(SkillIssueCode.TotalTooLarge, SkillSeverity.Error, Limit: SkillLimits.TotalMaxBytes));
                return;
            }
            files.Add(new SkillFile(relative, File.ReadAllBytes(entry)));
        }
    }

    private static string ReadText(string path) => new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path));

    /// <summary>
    /// Writes the skill: staged, then moved into place. <paramref name="renamedFrom"/> is the folder of the skill this one was renamed from
    /// (removed after the move). Returns <see cref="SkillSaveStatus.Exists"/> when another skill has the name and <paramref name="replace"/> is false.
    /// </summary>
    internal SkillSaveStatus Write(SkillDocument document, string? renamedFrom, bool replace)
    {
        var name = document.Name;
        if (!ValidName(name))
        {
            return SkillSaveStatus.Refused;
        }
        var target = Path.Combine(Root, name);
        var exists = Directory.Exists(target) || File.Exists(target);
        var update = renamedFrom is null ? false : renamedFrom == name;
        if (exists && !update && !replace)
        {
            return SkillSaveStatus.Exists;
        }
        if (exists && IsLink(target))
        {
            return SkillSaveStatus.NotAllowed; // never write through a link
        }
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Staging, id);
        var aside = Path.Combine(Staging, id + ".old");
        try
        {
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "SKILL.md"), SkillMarkdown.Write(document), new UTF8Encoding(false));
            if (SkillMarkdown.WriteExtension(document.Extension) is { } yaml)
            {
                File.WriteAllText(Path.Combine(stage, "faden.yaml"), yaml, new UTF8Encoding(false));
            }
            var fullStage = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            foreach (var file in document.Resources)
            {
                if (SkillValidator.PathProblem(file.Path) is not null)
                {
                    return SkillSaveStatus.Refused;
                }
                var full = Path.GetFullPath(Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                {
                    return SkillSaveStatus.Refused; // would leave the folder
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, file.Bytes);
            }
            Directory.CreateDirectory(Root);
            var movedAside = false;
            if (exists)
            {
                Directory.Move(target, aside);
                movedAside = true;
            }
            try
            {
                Directory.Move(stage, target);
            }
            catch
            {
                if (movedAside)
                {
                    Directory.Move(aside, target); // the old skill stays as it was
                }
                throw;
            }
            if (movedAside)
            {
                TryDelete(aside);
            }
            if (renamedFrom is not null && renamedFrom != name && ValidName(renamedFrom))
            {
                var old = Path.Combine(Root, renamedFrom);
                if (Directory.Exists(old) && !IsLink(old))
                {
                    TryDelete(old);
                }
            }
            return SkillSaveStatus.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SkillSaveStatus.Failed;
        }
        finally
        {
            TryDelete(stage);
            TryDeleteEmpty(Staging);
        }
    }

    internal bool Delete(string name)
    {
        if (!ValidName(name))
        {
            return false;
        }
        var directory = Path.Combine(Root, name);
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory))
            {
                return false;
            }
            Directory.Delete(directory, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left for the next time
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // another save is using it
        }
    }
}

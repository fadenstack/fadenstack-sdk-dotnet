using System.IO.Compression;
using System.Text;
using Fadenstack.SkillAuthoring;

namespace Fadenstack.Skills;

/// <summary>
/// Import of a folder or a zip, and export of a skill as a zip. What comes in is untrusted: a zip is checked entry by entry before any
/// byte is expanded (path traversal and zip-slip, absolute and drive paths, backslashes, links, the number of files, each file's size and
/// the total, the expansion ratio), and read through a counter that stops at the limit whatever the zip claims. Nothing is written here:
/// the result is a document in memory, checked by <see cref="SkillValidator"/>, which the person confirms before the store writes it.
/// </summary>
internal static class SkillImport
{
    private const int MaxDepth = 8;
    private const int BombFloor = 64 * 1024;
    private static readonly string[] Junk = { ".git", "__MACOSX", ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static ImportAnalysis Refuse(string source, SkillIssueCode code, string? subject = null, int limit = 0, int actual = 0, string? detail = null) =>
        new(source, null, new[] { new SkillIssue(code, SkillSeverity.Error, subject, limit, actual, detail) }, false, 0, 0);

    internal static ImportAnalysis FromPath(string path, Func<string, SkillExtensionParse> extension, Func<string, bool> exists, ISet<string> reserved)
    {
        var source = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            if (Directory.Exists(path))
            {
                return FromFolder(path, source, extension, exists, reserved);
            }
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    return Refuse(source, SkillIssueCode.LinkEntry, source);
                }
                var length = new FileInfo(path).Length;
                if (length > SkillLimits.ZipMaxBytes)
                {
                    return Refuse(source, SkillIssueCode.FileTooLarge, source, SkillLimits.ZipMaxBytes, (int)Math.Min(length, int.MaxValue));
                }
                return FromZip(File.ReadAllBytes(path), source, extension, exists, reserved);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse(source, SkillIssueCode.ReadFailed, source);
        }
        return Refuse(source, SkillIssueCode.ReadFailed, source);
    }

    internal static ImportAnalysis FromZip(byte[] zip, string source, Func<string, SkillExtensionParse> extension, Func<string, bool> exists, ISet<string> reserved)
    {
        if (zip.Length > SkillLimits.ZipMaxBytes)
        {
            return Refuse(source, SkillIssueCode.FileTooLarge, source, SkillLimits.ZipMaxBytes, zip.Length);
        }
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip, false), ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            return Refuse(source, SkillIssueCode.NotAZip, source);
        }
        using (archive)
        {
            var issues = new List<SkillIssue>();
            List<ZipArchiveEntry> entries;
            try
            {
                entries = archive.Entries.ToList();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
            {
                return Refuse(source, SkillIssueCode.NotAZip, source);
            }
            if (entries.Count > SkillLimits.FilesMax * 2)
            {
                return Refuse(source, SkillIssueCode.TooManyFiles, limit: SkillLimits.FilesMax, actual: entries.Count);
            }
            // The entries first: names and the sizes the zip says, before a byte is expanded.
            long declared = 0;
            var files = new List<ZipArchiveEntry>();
            foreach (var entry in entries)
            {
                var name = entry.FullName;
                var isDirectory = name.EndsWith("/", StringComparison.Ordinal);
                var trimmed = isDirectory ? name.TrimEnd('/') : name;
                if (trimmed.Length == 0)
                {
                    continue;
                }
                if (IsJunk(trimmed))
                {
                    continue;
                }
                if (SkillValidator.PathProblem(trimmed) is { } problem && !(problem == "reserved"))
                {
                    issues.Add(new SkillIssue(SkillIssueCode.UnsafePath, SkillSeverity.Error, Shorten(name), Detail: problem));
                    continue;
                }
                if (IsLink(entry))
                {
                    issues.Add(new SkillIssue(SkillIssueCode.LinkEntry, SkillSeverity.Error, Shorten(name)));
                    continue;
                }
                if (isDirectory)
                {
                    continue;
                }
                files.Add(entry);
                declared += entry.Length;
                if (entry.Length > SkillLimits.FileMaxBytes)
                {
                    issues.Add(new SkillIssue(SkillIssueCode.FileTooLarge, SkillSeverity.Error, Shorten(name), SkillLimits.FileMaxBytes, (int)Math.Min(entry.Length, int.MaxValue)));
                }
                else if (entry.Length > BombFloor && entry.CompressedLength * SkillLimits.ExpansionRatioMax < entry.Length)
                {
                    issues.Add(new SkillIssue(SkillIssueCode.ZipBomb, SkillSeverity.Error, Shorten(name), SkillLimits.ExpansionRatioMax));
                }
            }
            if (files.Count > SkillLimits.FilesMax)
            {
                issues.Add(new SkillIssue(SkillIssueCode.TooManyFiles, SkillSeverity.Error, Limit: SkillLimits.FilesMax, Actual: files.Count));
            }
            if (declared > SkillLimits.TotalMaxBytes)
            {
                issues.Add(new SkillIssue(declared > (long)zip.Length * SkillLimits.ExpansionRatioMax ? SkillIssueCode.ZipBomb : SkillIssueCode.TotalTooLarge, SkillSeverity.Error,
                    Limit: SkillLimits.TotalMaxBytes, Actual: (int)Math.Min(declared, int.MaxValue)));
            }
            else if (declared > BombFloor && declared > (long)zip.Length * SkillLimits.ExpansionRatioMax)
            {
                issues.Add(new SkillIssue(SkillIssueCode.ZipBomb, SkillSeverity.Error, Limit: SkillLimits.ExpansionRatioMax));
            }
            if (issues.Count > 0)
            {
                return new ImportAnalysis(source, null, issues, false, files.Count, declared); // nothing was expanded
            }

            // Then the content, through a counter that stops at the limit whatever the zip claims.
            var bag = new List<(string Path, byte[] Bytes)>();
            long actual = 0;
            foreach (var entry in files)
            {
                var read = ReadLimited(entry, (int)Math.Min(entry.Length, SkillLimits.FileMaxBytes), ref actual);
                if (read is null)
                {
                    return Refuse(source, SkillIssueCode.ZipBomb, Shorten(entry.FullName), SkillLimits.ExpansionRatioMax);
                }
                bag.Add((entry.FullName, read));
            }
            return Assemble(bag, source, null, extension, exists, reserved);
        }
    }

    /// <summary>The entry's bytes, at most its declared length; null when it holds more than it says or the total passes the limit.</summary>
    private static byte[]? ReadLimited(ZipArchiveEntry entry, int declared, ref long total)
    {
        using var stream = entry.Open();
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (output.Length + read > declared || total > SkillLimits.TotalMaxBytes)
            {
                return null;
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        var property = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
        if (property is null || property.GetValue(entry) is not int attributes)
        {
            return false; // the content of a link entry would be written as a plain file, never followed
        }
        var unixType = (attributes >> 16) & 0xF000;
        return unixType == 0xA000 || (attributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsJunk(string path) => path.Split('/').Any(part => Junk.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static string Shorten(string text) => text.Length > 60 ? text.Substring(0, 60) : text;

    internal static ImportAnalysis FromFolder(string path, string source, Func<string, SkillExtensionParse> extension, Func<string, bool> exists, ISet<string> reserved)
    {
        var issues = new List<SkillIssue>();
        var bag = new List<(string Path, byte[] Bytes)>();
        long total = 0;
        var root = Path.GetFullPath(path);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            return Refuse(source, SkillIssueCode.LinkEntry, source);
        }
        Walk(root, "", 0, bag, issues, ref total);
        if (issues.Count > 0)
        {
            return new ImportAnalysis(source, null, issues, false, bag.Count, total);
        }
        return Assemble(bag, source, source, extension, exists, reserved);
    }

    private static void Walk(string directory, string prefix, int depth, List<(string Path, byte[] Bytes)> bag, List<SkillIssue> issues, ref long total)
    {
        if (depth > MaxDepth)
        {
            issues.Add(new SkillIssue(SkillIssueCode.UnsafePath, SkillSeverity.Error, Shorten(prefix), Detail: "too deep"));
            return;
        }
        foreach (var entry in Directory.GetFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (Junk.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = prefix + name;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                issues.Add(new SkillIssue(SkillIssueCode.LinkEntry, SkillSeverity.Error, Shorten(relative)));
                continue;
            }
            if (Directory.Exists(entry))
            {
                Walk(entry, relative + "/", depth + 1, bag, issues, ref total);
                continue;
            }
            if (bag.Count >= SkillLimits.FilesMax)
            {
                issues.Add(new SkillIssue(SkillIssueCode.TooManyFiles, SkillSeverity.Error, Limit: SkillLimits.FilesMax));
                return;
            }
            var length = new FileInfo(entry).Length;
            if (length > SkillLimits.FileMaxBytes)
            {
                issues.Add(new SkillIssue(SkillIssueCode.FileTooLarge, SkillSeverity.Error, Shorten(relative), SkillLimits.FileMaxBytes, (int)Math.Min(length, int.MaxValue)));
                continue;
            }
            total += length;
            if (total > SkillLimits.TotalMaxBytes)
            {
                issues.Add(new SkillIssue(SkillIssueCode.TotalTooLarge, SkillSeverity.Error, Limit: SkillLimits.TotalMaxBytes));
                return;
            }
            bag.Add((relative, File.ReadAllBytes(entry)));
        }
    }

    /// <summary>
    /// The files as a skill: SKILL.md at the root (or under the one folder every entry shares), its faden.yaml, the rest as resources. The
    /// whole is checked as a save would check it; a zip's top folder that is not the skill's name is said, not refused (the skill is kept under its name).
    /// </summary>
    private static ImportAnalysis Assemble(List<(string Path, byte[] Bytes)> bag, string source, string? folder, Func<string, SkillExtensionParse> extension,
        Func<string, bool> exists, ISet<string> reserved)
    {
        string? top = folder;
        if (!bag.Any(f => f.Path == "SKILL.md"))
        {
            var firsts = bag.Select(f => f.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (firsts.Count == 1 && bag.All(f => f.Path.Contains("/")))
            {
                top = firsts[0];
                bag = bag.Select(f => (f.Path.Substring(top.Length + 1), f.Bytes)).ToList();
            }
        }
        var head = bag.FirstOrDefault(f => f.Path == "SKILL.md");
        if (head.Bytes is null)
        {
            return new ImportAnalysis(source, null, new[] { new SkillIssue(SkillIssueCode.SkillMdMissing, SkillSeverity.Error, source) }, false, bag.Count, bag.Sum(f => (long)f.Bytes.Length));
        }
        var issues = new List<SkillIssue>();
        var skillMd = new UTF8Encoding(false, false).GetString(head.Bytes);
        if (!SkillMarkdown.TryParse(skillMd, out var document, out var error))
        {
            issues.Add(new SkillIssue(SkillIssueCode.SkillMdUnreadable, SkillSeverity.Error, "SKILL.md", Detail: error));
            return new ImportAnalysis(source, null, issues, false, bag.Count, bag.Sum(f => (long)f.Bytes.Length));
        }
        var fields = new SkillExtensionFields();
        var yamlFile = bag.FirstOrDefault(f => f.Path == "faden.yaml");
        if (yamlFile.Bytes is not null)
        {
            var yamlText = new UTF8Encoding(false, false).GetString(yamlFile.Bytes);
            var parsed = extension(yamlText);
            issues.AddRange(parsed.Issues);
            if (parsed.Fields is { } read)
            {
                fields = read;
            }
        }
        var resources = bag.Where(f => f.Path != "SKILL.md" && f.Path != "faden.yaml").Select(f => new SkillFile(f.Path, f.Bytes)).ToList();
        document = document with { Extension = fields, Resources = resources };
        var check = SkillValidator.Check(document, null, reserved);
        issues.AddRange(check.Issues);
        if (top is not null && document.Name.Length > 0 && !string.Equals(top, document.Name, StringComparison.Ordinal))
        {
            issues.Add(new SkillIssue(SkillIssueCode.NameNotFolder, SkillSeverity.Warning, "name", Detail: top));
        }
        var replace = document.Name.Length > 0 && SkillFolderStore.ValidName(document.Name) && exists(document.Name);
        return new ImportAnalysis(source, document, Dedupe(issues), replace, bag.Count, bag.Sum(f => (long)f.Bytes.Length));
    }

    private static List<SkillIssue> Dedupe(List<SkillIssue> issues) => issues.Distinct().ToList();

    /// <summary>The skill as a zip: <c>&lt;name&gt;/SKILL.md</c>, <c>faden.yaml</c> and the resources.</summary>
    internal static byte[] ToZip(SkillDocument document)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add(archive, document.Name + "/SKILL.md", new UTF8Encoding(false).GetBytes(SkillMarkdown.Write(document)));
            if (SkillMarkdown.WriteExtension(document.Extension) is { } yaml)
            {
                Add(archive, document.Name + "/faden.yaml", new UTF8Encoding(false).GetBytes(yaml));
            }
            foreach (var file in document.Resources)
            {
                Add(archive, document.Name + "/" + file.Path, file.Bytes);
            }
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}

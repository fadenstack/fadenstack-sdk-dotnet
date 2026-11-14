namespace Fadenstack.Skills;

/// <summary>
/// The six-field rule (sdk/51 F04, H6): SKILL.md keeps the standard fields <c>name</c>, <c>description</c>, <c>license</c>,
/// <c>compatibility</c>, <c>metadata</c> and <c>allowed-tools</c>; the name is the folder's name and
/// <c>[a-z0-9]+(-[a-z0-9]+)*</c> up to 64 characters; the description is up to 1,024. A skill that breaks it is skipped (and
/// logged), never repaired. Microsoft Agent Framework 1.28 checks the name (its form, its length and that it equals the
/// folder's) and the description itself when it reads the file, and logs the skill it skips; it ignores other keys, and a
/// compatibility over 500 characters throws out of its whole source (the module reads one skill per source for that reason). So
/// this adds one check: a key beyond the six.
/// </summary>
internal static class SkillRules
{
    internal static readonly string[] Fields = { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };

    /// <summary>
    /// The folders under <paramref name="root"/> that hold a SKILL.md, down to two levels as MAF searches, without following a
    /// symbolic link or reparse point (MAF checks again before every read). The root itself counts when it holds one.
    /// </summary>
    internal static IEnumerable<string> SkillFolders(string root)
    {
        var found = new List<string>();
        Walk(root, 0, found);
        return found;
    }

    private static void Walk(string directory, int depth, List<string> found)
    {
        if (File.Exists(Path.Combine(directory, "SKILL.md")))
        {
            found.Add(directory);
            return;
        }
        if (depth >= 2)
        {
            return;
        }
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(children, StringComparer.Ordinal);
        foreach (var child in children)
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
            {
                Walk(child, depth + 1, found);
            }
        }
    }

    /// <summary>The first top-level key of the frontmatter that is not one of the six; null when there is none or it cannot be read.</summary>
    internal static string? ExtraKey(string skillFile)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(skillFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // MAF could not read it either: it does not list it
        }
        var start = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (start < 0 || lines[start].Trim() != "---")
        {
            return null;
        }
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd() == "---")
            {
                return null;
            }
            // A key at the left edge (a nested one is indented, a list item starts with '-', a comment with '#').
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '-' or '#')
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var key = line.Substring(0, colon).Trim().Trim('"', '\'');
            if (!Fields.Contains(key, StringComparer.Ordinal))
            {
                return key.Length > 40 ? key.Substring(0, 40) : key;
            }
        }
        return null;
    }
}

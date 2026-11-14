using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.SkillAuthoring;

/// <summary>
/// SKILL.md as text: reads the frontmatter and the body, and writes them back with the six standard fields only. The frontmatter is
/// untrusted input, so the reader is small and strict: top-level <c>key: value</c> lines, plain, quoted and block scalars, the
/// <c>metadata</c> map and a list for <c>allowed-tools</c>; no anchors, aliases or tags, and nothing is expanded. Keys beyond the six
/// are listed in <see cref="SkillDocument.UnknownKeys"/>, never dropped silently.
/// </summary>
public static class SkillMarkdown
{
    private static readonly string[] Standard = { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };
    private static readonly Regex Key = new(@"^([A-Za-z_][\w.-]*)\s*:(?:\s+(.*)|(.*))$", RegexOptions.CultureInvariant);
    private static readonly Regex NameForm = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainSafe = new(@"^[A-Za-z][A-Za-z0-9 ._,/()+=-]*$", RegexOptions.CultureInvariant);
    private static readonly string[] Words = { "true", "false", "yes", "no", "on", "off", "null", "y", "n" };

    /// <summary>A skill name made from any text: lower case, letters and digits with single hyphens, up to 64 characters, else <c>new-skill</c> (core's <c>slugify</c>).</summary>
    public static string Slugify(string? text)
    {
        var builder = new StringBuilder();
        var hyphen = false;
        foreach (var c in (text ?? "").ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                builder.Append(c);
                hyphen = false;
            }
            else if (!hyphen && builder.Length > 0)
            {
                builder.Append('-');
                hyphen = true;
            }
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > SkillLimits.NameMax)
        {
            slug = slug.Substring(0, SkillLimits.NameMax).TrimEnd('-');
        }
        return NameForm.IsMatch(slug) ? slug : "new-skill";
    }

    /// <summary>Reads a SKILL.md. False with <paramref name="error"/> when there is no frontmatter or it is not readable.</summary>
    public static bool TryParse(string? text, out SkillDocument document, out string? error)
    {
        document = new SkillDocument();
        error = null;
        text ??= "";
        if (text.Length > 0 && text[0] == (char)0xFEFF)
        {
            text = text.Substring(1);
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0)
        {
            start++;
        }
        if (start >= lines.Length || lines[start].TrimEnd() != "---")
        {
            error = "There is no frontmatter (the file must begin with ---).";
            return false;
        }
        var end = -1;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---")
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            error = "The frontmatter is not closed with ---.";
            return false;
        }

        string name = "", description = "", license = "", compatibility = "", allowed = "";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = start + 1;
        while (index < end)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                index++;
                continue;
            }
            var match = line.Length > 0 && !char.IsWhiteSpace(line[0]) ? Key.Match(line) : Match.Empty;
            if (!match.Success)
            {
                error = "A line of the frontmatter is not a key: " + Shorten(line.Trim());
                return false;
            }
            var key = match.Groups[1].Value;
            var inline = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
            var children = new List<string>();
            index++;
            while (index < end && (lines[index].Trim().Length == 0 || char.IsWhiteSpace(lines[index][0]) || lines[index].StartsWith("- ", StringComparison.Ordinal)))
            {
                children.Add(lines[index]);
                index++;
            }
            if (!seen.Add(key))
            {
                error = "The key " + Shorten(key) + " is there twice.";
                return false;
            }
            if (key == "metadata")
            {
                if (inline.Length > 0 && inline != "{}")
                {
                    error = "metadata is a map of text to text.";
                    return false;
                }
                foreach (var child in children.Where(c => c.Trim().Length > 0 && !c.Trim().StartsWith("#", StringComparison.Ordinal)))
                {
                    var entry = Key.Match(child.Trim());
                    if (!entry.Success)
                    {
                        error = "metadata is a map of text to text.";
                        return false;
                    }
                    if (!TryScalar((entry.Groups[2].Success ? entry.Groups[2].Value : entry.Groups[3].Value).Trim(), out var value))
                    {
                        error = "A value in metadata is not readable.";
                        return false;
                    }
                    metadata[entry.Groups[1].Value] = value;
                }
                continue;
            }
            if (!TryValue(inline, children, out var text1, out error))
            {
                return false;
            }
            switch (key)
            {
                case "name": name = text1; break;
                case "description": description = text1; break;
                case "license": license = text1; break;
                case "compatibility": compatibility = text1; break;
                case "allowed-tools": allowed = text1; break;
                default: unknown.Add(Shorten(key)); break;
            }
        }
        var body = string.Join("\n", lines, end + 1, lines.Length - end - 1);
        document = new SkillDocument
        {
            Name = name.Trim(),
            Description = description.Trim(),
            License = license.Trim(),
            Compatibility = compatibility.Trim(),
            Metadata = metadata,
            AllowedTools = allowed.Trim(),
            Instructions = body.Trim('\n', ' ', '\t').Length == 0 ? "" : body.TrimStart('\n').TrimEnd(),
            UnknownKeys = unknown,
        };
        return true;
    }

    private static string Shorten(string text) => text.Length > 40 ? text.Substring(0, 40) : text;

    /// <summary>The value of a key: an inline scalar (with continuation lines for a plain one), a block scalar, or a list (joined with spaces).</summary>
    private static bool TryValue(string inline, List<string> children, out string value, out string? error)
    {
        value = "";
        error = null;
        if (inline.Length > 0 && (inline[0] == '|' || inline[0] == '>') && inline.Substring(1).Trim('-', '+', ' ').Length == 0)
        {
            var literal = inline[0] == '|';
            var paragraphs = new List<string>();
            var current = new List<string>();
            foreach (var child in children)
            {
                var trimmed = child.Trim();
                if (trimmed.Length == 0)
                {
                    if (current.Count > 0)
                    {
                        paragraphs.Add(string.Join(literal ? "\n" : " ", current));
                        current.Clear();
                    }
                    continue;
                }
                current.Add(trimmed);
            }
            if (current.Count > 0)
            {
                paragraphs.Add(string.Join(literal ? "\n" : " ", current));
            }
            value = string.Join(literal ? "\n\n" : "\n", paragraphs);
            return true;
        }
        var parts = new List<string>();
        if (inline.Length > 0)
        {
            parts.Add(inline);
        }
        foreach (var child in children.Select(c => c.Trim()).Where(c => c.Length > 0 && !c.StartsWith("#", StringComparison.Ordinal)))
        {
            parts.Add(child.StartsWith("- ", StringComparison.Ordinal) ? child.Substring(2).Trim() : child);
        }
        if (parts.Count == 0)
        {
            return true;
        }
        if (inline.Length > 0 && (inline[0] == '"' || inline[0] == '\''))
        {
            if (parts.Count > 1)
            {
                error = "A quoted value runs over several lines.";
                return false;
            }
            if (!TryScalar(inline, out value))
            {
                error = "A quoted value is not closed.";
                return false;
            }
            return true;
        }
        var items = new List<string>();
        foreach (var part in parts)
        {
            if (!TryScalar(part, out var item))
            {
                error = "A value is not readable.";
                return false;
            }
            items.Add(item);
        }
        value = string.Join(" ", items);
        return true;
    }

    private static bool TryScalar(string text, out string value)
    {
        value = text;
        if (text.Length == 0)
        {
            return true;
        }
        if (text[0] == '"')
        {
            var builder = new StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    value = builder.ToString();
                    return text.Substring(i + 1).Trim().Length == 0 || text.Substring(i + 1).TrimStart().StartsWith("#", StringComparison.Ordinal);
                }
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (++i >= text.Length)
                {
                    return false;
                }
                switch (text[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case '0': builder.Append('\0'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'u' when i + 4 < text.Length && int.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        builder.Append((char)code);
                        i += 4;
                        break;
                    default: return false;
                }
            }
            return false;
        }
        if (text[0] == '\'')
        {
            var builder = new StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] != '\'')
                {
                    builder.Append(text[i]);
                }
                else if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i++;
                }
                else
                {
                    value = builder.ToString();
                    return text.Substring(i + 1).Trim().Length == 0 || text.Substring(i + 1).TrimStart().StartsWith("#", StringComparison.Ordinal);
                }
            }
            return false;
        }
        var hash = text.IndexOf(" #", StringComparison.Ordinal);
        value = (hash >= 0 ? text.Substring(0, hash) : text).Trim();
        return true;
    }

    /// <summary>The document as a SKILL.md: the six standard fields (those that are set), then the instructions. Never any other key.</summary>
    public static string Write(SkillDocument document)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(Scalar(document.Name)).Append('\n');
        builder.Append("description: ").Append(Scalar(document.Description)).Append('\n');
        if (document.License.Length > 0)
        {
            builder.Append("license: ").Append(Scalar(document.License)).Append('\n');
        }
        if (document.Compatibility.Length > 0)
        {
            builder.Append("compatibility: ").Append(Scalar(document.Compatibility)).Append('\n');
        }
        if (document.Metadata.Count > 0)
        {
            builder.Append("metadata:\n");
            foreach (var pair in document.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                builder.Append("  ").Append(Scalar(pair.Key)).Append(": ").Append(Scalar(pair.Value)).Append('\n');
            }
        }
        if (document.AllowedTools.Length > 0)
        {
            builder.Append("allowed-tools: ").Append(Scalar(document.AllowedTools)).Append('\n');
        }
        builder.Append("---\n");
        var body = (document.Instructions ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        if (body.Length > 0)
        {
            builder.Append('\n').Append(body).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>The <c>faden.yaml</c> for the fields; null when they say nothing (the file is left out).</summary>
    public static string? WriteExtension(SkillExtensionFields fields)
    {
        if (fields.IsEmpty)
        {
            return null;
        }
        var builder = new StringBuilder();
        if (fields.Title.Length > 0)
        {
            builder.Append("title: ").Append(Scalar(fields.Title)).Append('\n');
        }
        List(builder, "tags", fields.Tags, 0);
        if (fields.Priority != SkillExtensionFields.DefaultPriority)
        {
            builder.Append("priority: ").Append(fields.Priority.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        if (fields.Activation != "auto")
        {
            builder.Append("activation: ").Append(Scalar(fields.Activation)).Append('\n');
        }
        if (fields.Keywords.Count > 0 || fields.FileTypes.Count > 0)
        {
            builder.Append("trigger_rules:\n");
            List(builder, "keywords", fields.Keywords, 2);
            List(builder, "file_types", fields.FileTypes, 2);
        }
        List(builder, "preferred_tools", fields.PreferredTools, 0);
        List(builder, "allowed_tools", fields.AllowedTools, 0);
        List(builder, "forbidden_tools", fields.ForbiddenTools, 0);
        List(builder, "knowledge_sources", fields.KnowledgeSources, 0);
        return builder.ToString();
    }

    private static void List(StringBuilder builder, string key, IReadOnlyList<string> items, int indent)
    {
        if (items.Count == 0)
        {
            return;
        }
        var pad = new string(' ', indent);
        builder.Append(pad).Append(key).Append(":\n");
        foreach (var item in items)
        {
            builder.Append(pad).Append("  - ").Append(Scalar(item)).Append('\n');
        }
    }

    /// <summary>A text as a YAML scalar: plain when that reads back as the same text, else double-quoted.</summary>
    internal static string Scalar(string? text)
    {
        text ??= "";
        if (text.Length > 0 && text.Length <= 2000 && PlainSafe.IsMatch(text) && text == text.Trim() && text.IndexOf(" #", StringComparison.Ordinal) < 0
            && !Words.Contains(text.ToLowerInvariant(), StringComparer.Ordinal))
        {
            return text;
        }
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ' || c == '\u007f' || (c >= '\u0080' && c <= '\u009f') || c == (char)0x2028 || c == (char)0x2029)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}

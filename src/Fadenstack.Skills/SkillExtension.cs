using System.Text.RegularExpressions;

namespace Fadenstack.Skills;

/// <summary>
/// What a skill's <c>faden.yaml</c> says (sdk/51 F04, D16), read by the rules of core's <c>skills/faden_skills/services/bundle.py</c>
/// (<c>split_extension</c>, <c>normalize_manifest</c>): <c>title</c>, <c>tags</c>, <c>priority</c> (a whole number from 0 to 100,
/// default 50), <c>activation</c> (<c>always</c>, <c>auto</c>, <c>manual</c>; default <c>auto</c>), <c>trigger_rules</c> with
/// <c>keywords</c>, <c>file_types</c> and <c>intents</c>, and <c>preferred_tools</c>. <c>allowed_tools</c>, <c>forbidden_tools</c> and
/// <c>knowledge_sources</c> are checked as core checks them and then left alone: a skill's text never changes a tool's class, a mode or
/// a grant, so no field narrows or widens the tools. A field of the wrong type refuses the whole file (as core refuses the bundle).
/// </summary>
internal sealed class SkillExtension
{
    internal const int DefaultPriority = 50;
    internal const int TitleMax = 255;

    /// <summary>More than core has, for a file that is read on every turn: a keyword list a person writes is a few dozen words.</summary>
    internal const int MaxKeywords = 200;

    internal const int MaxKeywordLength = 100;

    /// <summary>The keyword matchers wait this long at most; a keyword is escaped text, so they are fast.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private SkillExtension()
    {
    }

    internal static SkillExtension Empty { get; } = new();

    internal string? Title { get; private set; }

    internal IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();

    internal int Priority { get; private set; } = DefaultPriority;

    internal string Activation { get; private set; } = "auto";

    internal IReadOnlyList<string> Keywords { get; private set; } = Array.Empty<string>();

    internal IReadOnlyList<string> FileTypes { get; private set; } = Array.Empty<string>();

    internal IReadOnlyList<string> PreferredTools { get; private set; } = Array.Empty<string>();

    /// <summary>One matcher per keyword (built once, with the file): the keyword as a whole word or phrase, any case.</summary>
    internal IReadOnlyList<Regex> KeywordMatchers { get; private set; } = Array.Empty<Regex>();

    /// <summary>Reads the text of a <c>faden.yaml</c>; throws <see cref="FadenYamlException"/> when it is not accepted.</summary>
    internal static SkillExtension Parse(string text)
    {
        var data = FadenYaml.Parse(text);
        if (data is null)
        {
            return Empty;
        }
        if (data is not Dictionary<string, object?> map)
        {
            throw new FadenYamlException("faden.yaml must be a mapping of fields.");
        }
        var extension = new SkillExtension();
        foreach (var key in new[] { "allowed_tools", "forbidden_tools", "knowledge_sources" })
        {
            if (Value(map, key) is { } value && Truthy(value))
            {
                StringList(value, key); // checked, never used
            }
        }
        if (Value(map, "preferred_tools") is { } preferred && Truthy(preferred))
        {
            extension.PreferredTools = StringList(preferred, "preferred_tools");
        }
        if (Value(map, "trigger_rules") is { } rules && Truthy(rules))
        {
            if (rules is not Dictionary<string, object?> trigger)
            {
                throw new FadenYamlException("trigger_rules is a mapping (keywords, file_types).");
            }
            foreach (var key in new[] { "keywords", "file_types", "intents" })
            {
                if (Value(trigger, key) is { } value && Truthy(value))
                {
                    var list = StringList(value, "trigger_rules." + key);
                    if (key == "keywords")
                    {
                        extension.Keywords = list;
                    }
                    else if (key == "file_types")
                    {
                        extension.FileTypes = list;
                    }
                }
            }
        }
        if (Value(map, "title") is { } title)
        {
            var text1 = Text(title).Trim();
            extension.Title = text1.Length > TitleMax ? text1.Substring(0, TitleMax) : text1;
        }
        if (Value(map, "tags") is { } tags)
        {
            extension.Tags = StringList(tags, "tags");
        }
        if (Value(map, "priority") is { } priority)
        {
            if (priority is not long number || number < 0 || number > 100)
            {
                throw new FadenYamlException("priority is a whole number from 0 to 100.");
            }
            extension.Priority = (int)number;
        }
        if (Value(map, "activation") is { } activation)
        {
            var name = Text(activation).Trim();
            if (name != "always" && name != "auto" && name != "manual")
            {
                throw new FadenYamlException("activation is one of always, auto, manual.");
            }
            extension.Activation = name;
        }
        if (extension.Keywords.Count > MaxKeywords || extension.Keywords.Any(k => k.Length > MaxKeywordLength))
        {
            throw new FadenYamlException("keywords are at most " + MaxKeywords + " words of " + MaxKeywordLength + " characters.");
        }
        extension.KeywordMatchers = extension.Keywords.Select(k => new Regex(@"(?<!\w)" + Regex.Escape(k) + @"(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout)).ToList();
        return extension;
    }

    private static object? Value(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    /// <summary>Python's truth: what <c>if manifest.get(key)</c> skips.</summary>
    private static bool Truthy(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        string s => s.Length > 0,
        List<object?> list => list.Count > 0,
        Dictionary<string, object?> dict => dict.Count > 0,
        _ => true,
    };

    private static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>core's <c>_string_list</c>: a list of strings, or one string split at commas and white space; each stripped, empties dropped.</summary>
    private static IReadOnlyList<string> StringList(object? value, string what)
    {
        if (value is string text)
        {
            return Regex.Split(text, @"[,\s]+").Where(p => p.Length > 0).ToList();
        }
        if (value is not List<object?> list || list.Any(v => v is not string))
        {
            throw new FadenYamlException(what + " is a list of strings.");
        }
        return list.Select(v => ((string)v!).Trim()).Where(v => v.Length > 0).ToList();
    }
}

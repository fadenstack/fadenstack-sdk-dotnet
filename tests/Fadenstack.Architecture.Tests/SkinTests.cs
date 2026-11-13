using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Fadenstack.Architecture.Tests;

/// <summary>
/// A5 and A6 (fadenstack-dev sdk/52 §6, strict MVVM): a skin's code-behind holds view-only behaviour (focus, scrolling,
/// animation), never logic, so each code-behind type's members are pinned in <c>codebehind-allowlist.json</c> and reviewed
/// when they change; and a skin's XAML holds no text of its own -- every text comes from <c>UiStrings</c> through a binding.
/// </summary>
public class SkinTests
{
    private static readonly string[] Skins = { "Fadenstack.UI.Wpf", "Fadenstack.UI.Avalonia" };

    private static List<string> CodeBehind() =>
        Skins.SelectMany(s => Repo.Sources(s, ".xaml.cs", ".axaml.cs")).Select(Repo.Relative).ToList();

    public static TheoryData<string> CodeBehindFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in CodeBehind())
        {
            data.Add(file);
        }
        return data;
    }

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Skins.SelectMany(s => Repo.Sources(s, ".xaml", ".axaml")))
        {
            data.Add(Repo.Relative(file));
        }
        return data;
    }

    // ---- A5: code-behind ------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, List<string>> Allowed =
        JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codebehind-allowlist.json")))!;

    [Fact]
    public void Every_code_behind_file_is_in_the_allow_list_and_every_entry_is_a_file()
    {
        var files = CodeBehind();
        Assert.NotEmpty(files);
        Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), Allowed.Keys.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CodeBehindFiles))]
    public void A_code_behind_holds_only_its_reviewed_members(string file)
    {
        var skin = Skins.Single(s => file.StartsWith($"src/{s}/", StringComparison.Ordinal));
        if (!Repo.Readable(skin))
        {
            return; // the WPF skin is read on Windows
        }
        var source = File.ReadAllText(Path.Combine(Repo.Root, file));
        var ns = Regex.Match(source, @"^namespace\s+([\w.]+)", RegexOptions.Multiline).Groups[1].Value;
        var name = Regex.Match(source, @"^(?:public |internal )?(?:sealed )?(?:partial )?class (\w+)", RegexOptions.Multiline).Groups[1].Value;
        var type = Repo.Assembly(skin).GetType($"{ns}.{name}", throwOnError: true)!;

        var members = Members(type);
        if (Environment.GetEnvironmentVariable("FADENSTACK_ARCH_ALLOWLIST") == "write")
        {
            Rewrite(file, members); // then review the change in git before committing it
        }

        Assert.True(Allowed.TryGetValue(file, out var allowed) && allowed.SequenceEqual(members),
            $"The members of {type.FullName} changed. Review that they are view-only (focus, scrolling, animation; no logic), "
            + $"then write them into codebehind-allowlist.json as \"{file}\":\n"
            + JsonSerializer.Serialize(members, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Writes a file's members into the allow-list in the source tree (<c>FADENSTACK_ARCH_ALLOWLIST=write dotnet test</c>);
    /// the change is then reviewed in git like any other.
    /// </summary>
    private static void Rewrite(string file, List<string> members)
    {
        var path = Path.Combine(Repo.Root, "tests", "Fadenstack.Architecture.Tests", "codebehind-allowlist.json");
        lock (Allowed)
        {
            var list = JsonSerializer.Deserialize<SortedDictionary<string, List<string>>>(File.ReadAllText(path))!;
            list[file] = members;
            File.WriteAllText(path, JsonSerializer.Serialize(list, Indented) + "\n");
        }
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What the code-behind declares, the XAML compilers' own members left out.</summary>
    private static List<string> Members(Type type) => type
        .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(m => m is not MethodBase { IsSpecialName: true } || m is ConstructorInfo) // accessors are their property's or event's
        .Where(m => !m.IsDefined(typeof(CompilerGeneratedAttribute)) && !m.IsDefined(typeof(GeneratedCodeAttribute)))
        .Where(m => !m.Name.Contains('<') && !m.Name.Contains('!')) // local functions, lambdas, compiled XAML
        .Select(m => $"{m.MemberType} {m}")
        .OrderBy(m => m, StringComparer.Ordinal)
        .ToList();

    // ---- A6: no literal text in XAML ------------------------------------------------------------------------------

    /// <summary>The attributes whose value a person reads or hears.</summary>
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "ToolTip", "ToolTip.Tip", "Title", "Watermark", "PlaceholderText",
        "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    /// <summary>Elements whose own text a person reads.</summary>
    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "Label", "Button", "ToggleButton", "CheckBox",
        "RadioButton", "MenuItem", "ToolTip", "TextBox", "SelectableTextBlock",
    };

    /// <summary>
    /// No text written in a skin's XAML: a text attribute (or a style's setter of one) holds a binding or a resource
    /// (<c>{...}</c>), and a text element holds no words. Values without a letter -- an icon glyph, a separator -- are not text.
    /// </summary>
    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void A_skins_xaml_holds_no_text_of_its_own(string file)
    {
        var xaml = XDocument.Load(Path.Combine(Repo.Root, file), LoadOptions.SetLineInfo);
        var found = new List<string>();
        foreach (var element in xaml.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => TextAttributes.Contains(a.Name.LocalName)))
            {
                Check(attribute.Value, attribute, $"{element.Name.LocalName}.{attribute.Name.LocalName}");
            }
            if (element.Name.LocalName == "Setter" && TextAttributes.Contains((string?)element.Attribute("Property") ?? "")
                && element.Attribute("Value") is { } value)
            {
                Check(value.Value, value, $"Setter {element.Attribute("Property")!.Value}");
            }
            if (TextElements.Contains(element.Name.LocalName))
            {
                foreach (var text in element.Nodes().OfType<XText>())
                {
                    Check(text.Value, text, element.Name.LocalName);
                }
            }
        }
        Assert.True(found.Count == 0, $"Text written in {file}:\n" + string.Join("\n", found));

        void Check(string value, IXmlLineInfo at, string where)
        {
            if (!value.TrimStart().StartsWith("{", StringComparison.Ordinal) && value.Any(char.IsLetter))
            {
                found.Add($"{file}:{at.LineNumber}: {where} = \"{value.Trim()}\"");
            }
        }
    }
}

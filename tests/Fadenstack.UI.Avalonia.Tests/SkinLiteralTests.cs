using System.Text.RegularExpressions;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// Design 22, sections 3 and 4: the chat's XAML and the control styles use theme keys only. No font size, radius or colour
/// is written as a literal outside Colors.axaml, no icon is a Path literal or a text glyph, and the Markdown view sets no
/// font size in code.
/// </summary>
public class SkinLiteralTests
{
    private static string Skin
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fadenstack.slnx")))
            {
                root = root.Parent;
            }
            Assert.NotNull(root);
            return Path.Combine(root!.FullName, "src", "Fadenstack.UI.Avalonia");
        }
    }

    /// <summary>The chat's XAML (the views, Chat.axaml, Parts.axaml) and the control styles.</summary>
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Skin, "Views"), "*.axaml"))
        {
            data.Add(Path.GetRelativePath(Skin, file));
        }
        data.Add(Path.Combine("Themes", "Chat.axaml"));
        data.Add(Path.Combine("Themes", "Parts.axaml"));
        data.Add(Path.Combine("Themes", "Controls.axaml"));
        return data;
    }

    private static readonly (string What, Regex Pattern)[] Literals =
    {
        ("a font size", new Regex(@"\bFontSize=""\s*[0-9.]")),
        ("a font size", new Regex(@"Property=""(TextElement\.)?FontSize""\s+Value=""\s*[0-9.]")),
        ("a radius", new Regex(@"\bCornerRadius=""\s*[0-9.]")),
        ("a radius", new Regex(@"Property=""CornerRadius""\s+Value=""\s*[0-9.]")),
        ("a colour", new Regex(@"=""\s*#[0-9A-Fa-f]{3,8}\s*""")),
        ("a colour", new Regex(@"<(SolidColorBrush|Color)\b")),
        ("a colour", new Regex(@"\b(Foreground|Background|BorderBrush|Fill|Stroke|CaretBrush|SelectionBrush)=""(?!\{|Transparent"")[A-Za-z]")),
        ("a colour", new Regex(@"Property=""[\w.]*(Foreground|Background|Brush|Fill|Stroke)""\s+Value=""(?!\{|Transparent"")[A-Za-z#]")),
        ("a Path icon", new Regex(@"<Path\b")),
        ("a glyph as an icon", new Regex(@"(Content|Text)=""\s*[✕▾▸×+⋮›‹]\s*""")),
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void The_skins_xaml_has_no_font_size_radius_or_colour_literal_and_no_icon_literal(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(Skin, file));
        var found = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (what, pattern) in Literals)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    found.Add($"{file}:{i + 1}: {what}: {lines[i].Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    [Fact]
    public void The_markdown_view_sets_no_font_size_and_draws_no_path_icon()
    {
        var code = File.ReadAllText(Path.Combine(Skin, "Views", "MarkdownView.cs"));
        Assert.DoesNotMatch(new Regex(@"FontSize\s*="), code);
        Assert.DoesNotMatch(new Regex(@"\bShapes\.Path\b|new\s+Path\b|IconPath"), code);
    }

    [Fact]
    public void The_scan_finds_what_it_looks_for()
    {
        string[] bad =
        {
            "<TextBlock FontSize=\"13\" />", "<Setter Property=\"CornerRadius\" Value=\"6\" />", "<Border Background=\"#F0F0F0\" />",
            "<Border BorderBrush=\"Red\" />", "<Path Data=\"M0,0 L1,1\" />", "<TextBlock Text=\"✕\" />", "<Setter Property=\"Foreground\" Value=\"#333\" />",
        };
        foreach (var line in bad)
        {
            Assert.Contains(Literals, l => l.Pattern.IsMatch(line));
        }
        string[] good =
        {
            "<TextBlock FontSize=\"{DynamicResource Fadenstack.FontSize.Body}\" />", "<Border Background=\"Transparent\" />",
            "<Setter Property=\"CornerRadius\" Value=\"{DynamicResource Fadenstack.Radius}\" />", "<c:FsIcon Data=\"{DynamicResource Icon.Add}\" />",
        };
        foreach (var line in good)
        {
            Assert.DoesNotContain(Literals, l => l.Pattern.IsMatch(line));
        }
    }
}

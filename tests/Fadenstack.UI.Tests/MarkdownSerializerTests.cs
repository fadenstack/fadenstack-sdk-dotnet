using System.Text.Json;
using Fadenstack.UI.Markdown;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The Markdown-to-HTML export gives what the TypeScript SDK's <c>markdownToHtml</c> gives, for the cases both test.</summary>
public class MarkdownSerializerTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    private static List<JsonElement> Cases() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "markdown-html.json"))).RootElement.EnumerateArray().ToList();

    [Theory]
    [MemberData(nameof(Names))]
    public void The_same_text_gives_the_same_html_in_both_SDKs(string name)
    {
        var shared = Cases().Single(c => c.GetProperty("name").GetString() == name);

        Assert.Equal(shared.GetProperty("html").GetString(), MarkdownSerializer.MarkdownToHtml(shared.GetProperty("markdown").GetString()!));
    }

    [Fact]
    public void A_table_is_tab_separated_with_its_cells_in_their_cells()
    {
        var tables = MarkdownSerializer.TablesIn(MarkdownParser.Parse("Intro\n\n| A | B |\n|---|---|\n| 1 | **2** |\n\n> | C |\n> |---|\n> | 3 |\n"));

        Assert.Equal(2, tables.Count);
        Assert.Equal("A\tB\n1\t2", MarkdownSerializer.TableToTsv(tables[0]));
        Assert.Equal("C\n3", MarkdownSerializer.TableToTsv(tables[1]));
        var piped = MarkdownSerializer.TablesIn(MarkdownParser.Parse("| A | B |\n|---|---|\n| x \\| y | z |")).Single();
        Assert.Equal("A\tB\nx | y\tz", MarkdownSerializer.TableToTsv(piped));
    }
}

using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>A grant's place in a short line: the same name on every operating system, whichever made the place.</summary>
public class PlaceNamesTests
{
    [Theory]
    [InlineData(@"C:\Docs\report.docx", "report.docx")]
    [InlineData("/home/anna/Docs/report.docx", "report.docx")]
    [InlineData(@"C:\Docs\Reports\", "Reports")]
    [InlineData("/home/anna/Docs/", "Docs")]
    [InlineData(@"\\server\share\plan.xlsx", "plan.xlsx")]
    [InlineData("https://intranet.example/wiki/Page", "Page")]
    [InlineData("report.docx", "report.docx")]
    [InlineData("Word", "Word")]
    [InlineData("/", "/")]
    public void A_place_is_shown_by_its_last_part_whatever_its_separator(string identifier, string shown) =>
        Assert.Equal(shown, PlaceNames.Short(identifier));
}

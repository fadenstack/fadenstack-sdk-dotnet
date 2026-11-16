using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// A program that changed since the person added it (sdk/51 5.5): the card says so in a warning with the reason, "Review the change"
/// opens the drawer, the drawer shows the reason and "Use the new program", which asks the catalog and nothing else; Remove stays.
/// </summary>
public class ToolsProgramChangedTests
{
    private static FakeLocalToolsCatalog WithChanged(LocalProgramChange change = LocalProgramChange.Hash)
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.Servers.Add(FakeLocalToolsCatalog.ChangedProgram(change));
        return catalog;
    }

    [Fact]
    public void The_card_says_the_program_changed_as_a_warning_and_offers_to_review_it()
    {
        using var page = Page(WithChanged());
        var card = Server(page, "files");

        Assert.Equal(("The program of files changed since you added it.", ServerStatusKind.Warning), (card.StatusText, card.StatusKind));
        Assert.True(card.HasFix);
        Assert.Equal("Review the change", card.FixText);
    }

    [Fact]
    public void Review_opens_the_drawer_which_shows_the_reason_and_the_new_program_action()
    {
        using var page = Page(WithChanged(LocalProgramChange.Missing));
        var card = Server(page, "files");

        card.FixCommand.Execute(null);

        var details = page.Details!;
        Assert.True(details.HasProgramChange);
        Assert.Equal("The program changed", details.ProgramChangedTitle);
        Assert.StartsWith("This server does not run.", details.ProgramChangedText);
        Assert.Equal("Use the new program of files", details.UseNewProgramName);
        Assert.True(details.UseNewProgramCommand.CanExecute(null));
        Assert.True(details.RemoveCommand.CanExecute(null)); // the other way out
    }

    [Fact]
    public async Task Using_the_new_program_asks_the_catalog_once_and_the_banner_goes()
    {
        var catalog = WithChanged();
        using var page = Page(catalog);
        Server(page, "files").OpenCommand.Execute(null);
        var details = page.Details!;

        await details.UseNewProgramCommand.ExecuteAsync(null);

        Assert.Equal(["use-new-program files"], catalog.Calls);
        Assert.False(details.HasProgramChange);
        Assert.Equal("", details.ProgramError);
    }

    [Fact]
    public async Task A_program_that_cannot_be_read_now_says_why_in_fixed_words_and_the_banner_stays()
    {
        var catalog = WithChanged();
        catalog.ProgramAnswer = ServerProblem.CommandInvalid;
        using var page = Page(catalog);
        Server(page, "files").OpenCommand.Execute(null);
        var details = page.Details!;

        await details.UseNewProgramCommand.ExecuteAsync(null);

        Assert.True(details.HasProgramError);
        Assert.True(details.HasProgramChange);
        Assert.Equal(ServerSheetViewModel.ProblemText(page.Strings, ServerProblem.CommandInvalid), details.ProgramError);
    }

    [Fact]
    public void The_drawer_tells_that_only_a_runner_is_checked_and_that_an_old_entry_is_pinned_at_its_next_start()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        var demo = catalog.Servers.First(s => s.Label == "demo");
        demo.IsProgram = true;
        demo.OnlyRunnerChecked = true;
        using var page = Page(catalog);
        Server(page, "demo").OpenCommand.Execute(null);

        var fact = Assert.Single(page.Details!.Facts, f => f.Label == "Program check");
        Assert.Equal("Only the runner is checked: the package it fetches can change.", fact.Value);

        demo.OnlyRunnerChecked = false;
        demo.ProgramPinned = false;
        page.Refresh();
        fact = Assert.Single(page.Details!.Facts, f => f.Label == "Program check");
        Assert.Equal("The program is pinned at its next start.", fact.Value);

        demo.ProgramPinned = true;
        page.Refresh();
        Assert.Equal("The program file is checked before every start.", Assert.Single(page.Details!.Facts, f => f.Label == "Program check").Value);
    }

    [Fact]
    public void A_test_of_a_changed_program_says_it_did_not_start_in_fixed_words()
    {
        var line = ServerSheetViewModel.TestLine(UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("en")),
            new ServerTestInfo(TestOutcome.ProgramChanged, [], "/home/anna/secret/server"));

        Assert.Equal("Not started: the program changed since you added it. Use the new program first.", line);
        Assert.DoesNotContain("anna", line);
    }
}

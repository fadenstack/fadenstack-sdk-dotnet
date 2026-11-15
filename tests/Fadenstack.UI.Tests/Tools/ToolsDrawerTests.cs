using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The details drawer (sdk/51 F05): the facts, the tools with the server's hints, the class each has now and the person's raise,
/// the "Trusted" switch, Test, Restart and Remove.
/// </summary>
public class ToolsDrawerTests
{
    private static ServerDetailsViewModel Open(ToolsViewModel page, string label)
    {
        Server(page, label).OpenCommand.Execute(null);
        return page.Details!;
    }

    [Fact]
    public void The_drawer_has_the_facts_of_a_program_with_its_folders_secrets_and_longest_call()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        var details = Open(page, "notes-db");

        Assert.Equal("notes-db, MCP server", details.DrawerName);
        Assert.Equal(
            [("Added", "From Claude Desktop"), ("Runs", "npx @acme/notes-mcp"), ("Told folders", "Fadenstack tests"), ("Secrets", "NOTES_DIR"), ("Longest call", "45 seconds")],
            details.Facts.Select(f => (f.Label, f.Value)));
        Assert.Equal("Tools · 2", details.ToolsTitle);
    }

    [Fact]
    public void An_address_has_no_folders_and_a_server_without_secrets_says_none_and_the_calls_time_is_in_minutes_when_round()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        var details = Open(page, "demo");
        Assert.Equal(("Longest call", "2 minutes"), (details.Facts.Last().Label, details.Facts.Last().Value));
        Assert.Equal(("Secrets", "None", true), (details.Facts[3].Label, details.Facts[3].Value, details.Facts[3].IsMuted));
        Assert.StartsWith("Running since", details.StatusText);

        var calendar = Open(page, "calendar");
        Assert.DoesNotContain(calendar.Facts, f => f.Label == "Told folders");
        Assert.Equal(("Address", "127.0.0.1:8123/mcp", true), (calendar.Facts[1].Label, calendar.Facts[1].Value, calendar.Facts[1].IsMono));
    }

    [Fact]
    public void Each_tool_shows_the_servers_own_word_labelled_as_such_and_its_class()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);

        var details = Open(page, "notes-db");

        var search = details.Tools[0];
        Assert.Equal(["Says: reads only"], search.Hints);
        Assert.Equal(("Asks every time", false, true), (search.ClassText, search.IsDestructive, search.CanRaise));
        var delete = details.Tools[1];
        Assert.Equal(["Says: may delete"], delete.Hints);
        Assert.Equal(("Destructive", true, false), (delete.ClassText, delete.IsDestructive, delete.CanRaise));
        Assert.Empty(delete.RaiseOptions);
        Assert.Equal("The server says this tool may delete or overwrite. It always asks, and nothing can lower that.", delete.DestructiveHelp);

        catalog.Servers[1].Trusted = true;
        catalog.Servers[1].Tools = new[]
        {
            new LocalToolInfo("search_notes", "Searches the notes.", null, true, null, null, null, LocalToolClass.Read),
            new LocalToolInfo("delete_note", "Deletes a note.", "Delete", false, true, false, null, LocalToolClass.Destructive),
        };
        page.Refresh();
        Assert.Equal("Reads", details.Tools[0].ClassText);
        Assert.Same(search, details.Tools[0]); // the row stays: so does its picker
    }

    [Fact]
    public void A_destructive_hint_is_a_floor_no_picker_is_offered_below_it_even_when_the_servers_class_has_not_caught_up()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        // The server's hint says destructive; the class the catalog gave is still Unknown (it must not be taken as a way to a picker).
        catalog.Servers[3].Tools = new[] { new LocalToolInfo("remove_event", "Removes.", null, false, true, null, null, LocalToolClass.Unknown) };
        using var page = Page(catalog);

        var details = Open(page, "calendar");

        Assert.True(details.Tools[0].IsDestructive);
        Assert.False(details.Tools[0].CanRaise);
        Assert.Empty(details.Tools[0].RaiseOptions);
        Assert.Equal("Destructive", details.Tools[0].ClassText);
    }

    [Fact]
    public void A_raise_picker_offers_none_read_and_write_and_choosing_one_goes_to_the_catalog()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        var details = Open(page, "demo");
        var tool = details.Tools.Single(t => t.Name == "plan_meeting");

        Assert.Equal(["As it arrives", "Reads only", "Changes things"], tool.RaiseOptions.Select(o => o.Text));
        Assert.Equal(LocalToolRaise.None, tool.SelectedRaise!.Raise);
        Assert.Equal("Class of plan_meeting", tool.RaiseName);

        tool.SelectedRaise = tool.RaiseOptions[2];

        Assert.Equal(["raise demo plan_meeting Write"], catalog.Calls);
    }

    [Fact]
    public void The_picker_shows_the_raise_the_person_made_and_a_refresh_does_not_send_it_back()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Servers[0].Tools = new[] { new LocalToolInfo("plan_meeting", "Plans.", null, false, false, null, true, LocalToolClass.Write, LocalToolRaise.Write) };
        using var page = Page(catalog);
        var details = Open(page, "demo");

        Assert.Equal(LocalToolRaise.Write, details.Tools[0].SelectedRaise!.Raise);
        page.Refresh();

        Assert.Empty(catalog.Calls);
    }

    [Fact]
    public void The_trusted_switch_goes_to_the_catalog_and_shows_the_trust_the_catalog_reports()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        var details = Open(page, "demo");

        Assert.False(details.IsTrusted);
        Assert.True(details.CanChangeMarks);
        Assert.StartsWith("Only a trusted server’s", details.TrustedNote);

        details.IsTrusted = true;

        Assert.Equal(["trust demo True"], catalog.Calls);
        Assert.True(details.IsTrusted);
        Assert.True(catalog.Servers[0].Trusted);
        Assert.Equal("Trust demo", details.TrustedName);
    }

    [Fact]
    public void Where_an_administrators_marks_count_the_switch_and_the_pickers_change_nothing()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.MarksAreYours = false;
        catalog.Servers[0].Trusted = true;
        using var page = Page(catalog);
        var details = Open(page, "demo");

        Assert.Equal((true, false, "Marked by your administrator"), (details.IsTrusted, details.CanChangeMarks, details.TrustedNote));
        details.IsTrusted = false;
        details.Tools[0].SelectedRaise = details.Tools[0].RaiseOptions[2];

        Assert.Empty(catalog.Calls);
        Assert.False(details.Tools[0].CanRaise);
    }

    [Fact]
    public async Task Test_restart_and_remove_go_through_the_catalog_and_remove_asks_first()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Drafts["demo"] = new ServerDraft { Label = "demo", Command = "faden-mcp-demo.exe" };
        using var page = Page(catalog);
        var details = Open(page, "demo");

        await details.TestCommand.ExecuteAsync(null);
        Assert.Equal("Started. Tools: add, now", details.TestText);
        await details.RestartCommand.ExecuteAsync(null);
        Assert.Equal("Restarted.", details.TestText);

        details.RemoveCommand.Execute(null);
        Assert.True(details.IsConfirmingRemove);
        Assert.Equal("Remove demo?", details.RemoveConfirmText);
        details.KeepCommand.Execute(null);
        Assert.False(details.IsConfirmingRemove);
        Assert.DoesNotContain("remove demo", catalog.Calls);

        details.RemoveCommand.Execute(null);
        await details.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Contains("remove demo", catalog.Calls);
        Assert.False(page.IsDetailsOpen);
        Assert.DoesNotContain(page.Servers, s => s.Label == "demo");
    }

    [Fact]
    public async Task A_test_that_was_refused_for_plain_http_says_why_in_its_own_words()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Servers.Add(FakeLocalToolsCatalog.Nas());
        catalog.Drafts["nas"] = new ServerDraft { Label = "nas", IsProgram = false, Url = "http://192.168.1.5:9/mcp" };
        catalog.TestAnswer = new ServerTestInfo(TestOutcome.Failed, Array.Empty<string>(), "x", LocalPlainHttpRefusal.NotLocal);
        using var page = Page(catalog);
        var details = Open(page, "nas");

        await details.TestCommand.ExecuteAsync(null);

        Assert.Equal("This address is not on the local network.", details.TestText);
    }

    [Fact]
    public void The_drawer_closes_when_its_server_is_gone()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        Open(page, "demo");

        catalog.Servers.RemoveAt(0);
        page.Refresh();

        Assert.False(page.IsDetailsOpen);
    }
}

using System.ComponentModel;
using System.Globalization;
using Fadenstack.LocalTools;
using Fadenstack.Tools;
using Fadenstack.UI.Tools;
using Xunit;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The Tools page's places: "In this app" (the app's own tools, read only), "On this computer" and "On your server", which exists only
/// while the agent is connected to an organisation's server.
/// </summary>
public class ToolsAppTabTests
{
    private static ToolsViewModel Page(FakeLocalToolsCatalog catalog, Func<UiStrings>? strings = null) =>
        new(catalog, new FakeFiles(), new FakeUi(), strings);

    [Fact]
    public void The_app_tab_lists_the_apps_tools_with_class_labels_and_the_computers_tab_stays_the_default()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        Assert.Equal(ToolsTab.Computer, page.Tab);
        Assert.Equal("In this app · 4", page.AppTabText);
        Assert.Equal(["read_page", "fill_field", "click_button", "wait"], page.AppTools.Select(t => t.Name));
        Assert.Equal(["Read", "Changes data", "Cannot be undone", "Unclassified"], page.AppTools.Select(t => t.ClassLabel));
        Assert.Equal([(true, false, false), (false, true, false), (false, false, true), (false, false, false)],
            page.AppTools.Select(t => (t.IsRead, t.IsWrite, t.IsDestructive)));
        Assert.True(page.AppTools[3].IsUnmarked);
        Assert.False(page.AppTools[3].HasDescription);
        Assert.True(page.AppTools[0].HasDescription);
    }

    [Fact]
    public void A_description_is_plain_text_with_the_white_space_collapsed_and_cut_to_300_characters_ending_in_an_ellipsis()
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.App.Add(new AppToolInfo("a", "Reads\n\n  the   page.\t", ToolClass.Read));
        catalog.App.Add(new AppToolInfo("b", new string('x', 300), ToolClass.Read));
        catalog.App.Add(new AppToolInfo("c", new string('y', 301), ToolClass.Read));
        catalog.App.Add(new AppToolInfo("d", new string('z', 299) + "\n\n" + new string('z', 50), ToolClass.Read));
        using var page = Page(catalog);

        Assert.Equal("Reads the page.", page.AppTools[0].Description);
        Assert.Equal(new string('x', 300), page.AppTools[1].Description);
        Assert.Equal(300, page.AppTools[2].Description.Length);
        Assert.EndsWith("…", page.AppTools[2].Description);
        Assert.Equal(new string('y', 299) + "…", page.AppTools[2].Description);
        Assert.Equal(300, page.AppTools[3].Description.Length);
        Assert.EndsWith("…", page.AppTools[3].Description);
    }

    [Fact]
    public void The_filter_narrows_the_app_tab_by_name_and_says_so_when_nothing_matches()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        page.Filter = "FIELD";
        Assert.Equal(["fill_field"], page.AppTools.Select(t => t.Name));
        Assert.False(page.ShowsAppNoMatch);
        Assert.False(page.ShowsAppEmpty);
        Assert.Equal("In this app · 4", page.AppTabText); // the tab counts the tools, not the matches

        page.Filter = "zzz";
        Assert.Empty(page.AppTools);
        Assert.True(page.ShowsAppNoMatch);
        Assert.False(page.ShowsAppEmpty);

        page.Filter = "";
        Assert.Equal(4, page.AppTools.Count);
        Assert.False(page.ShowsAppNoMatch);
    }

    [Fact]
    public void An_app_without_tools_has_one_short_line()
    {
        using var page = Page(new FakeLocalToolsCatalog());

        Assert.Empty(page.AppTools);
        Assert.True(page.ShowsAppEmpty);
        Assert.False(page.ShowsAppNoMatch);
        Assert.Equal("This app gives the assistant no tools of its own.", page.Strings.ToolsAppNone);
        Assert.Equal("In this app · 0", page.AppTabText);
        page.Filter = "x";
        Assert.False(page.ShowsAppNoMatch); // there was nothing to hide
    }

    [Fact]
    public void Each_tab_can_be_picked_through_its_flag_and_the_flags_follow()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());
        var raised = new List<string>();
        ((INotifyPropertyChanged)page).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        page.ShowsApp = true;
        Assert.Equal(ToolsTab.App, page.Tab);
        Assert.True(page.ShowsApp);
        Assert.False(page.ShowsLocal);
        Assert.False(page.ShowsServer);
        Assert.Contains(nameof(ToolsViewModel.ShowsApp), raised);
        Assert.Contains(nameof(ToolsViewModel.ShowsLocal), raised);
        Assert.Contains(nameof(ToolsViewModel.ShowsLocalChip), raised);

        // An unchecked radio button reports false after the picked one reported true: that changes nothing.
        page.ShowsLocal = false;
        Assert.Equal(ToolsTab.App, page.Tab);

        // The header's buttons and the policy chip belong to the computer's tab; the managed chip to the server's.
        Assert.False(page.ShowsAddButton);
        Assert.False(page.ShowsScanButton);
        Assert.False(page.ShowsLocalChip);
        Assert.False(page.ShowsManagedChip);

        page.ShowsServer = true;
        Assert.Equal(ToolsTab.Server, page.Tab);
        Assert.True(page.ShowsManagedChip);
        Assert.False(page.ShowsAddButton);

        page.ShowsLocal = true;
        Assert.Equal(ToolsTab.Computer, page.Tab);
        Assert.True(page.ShowsAddButton);
        Assert.True(page.ShowsLocalChip);
        Assert.False(page.ShowsManagedChip);
    }

    [Fact]
    public async Task A_standalone_agent_has_no_server_tab_and_the_cloud_tools_are_never_asked_for()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.IsGoverned = false;
        using var page = Page(catalog);
        await page.OpenAsync();
        await page.LoadCloudAsync();
        page.RetryCommand.Execute(null);

        Assert.False(page.ShowsServerTab);
        Assert.Equal(0, catalog.CloudCalls);
        Assert.Empty(page.CloudGroups);
        page.ShowsServer = true; // there is no such tab to pick
        Assert.Equal(ToolsTab.Computer, page.Tab);
    }

    [Fact]
    public async Task A_connection_that_is_not_known_yet_has_no_server_tab_and_the_tab_comes_when_the_agent_is_governed()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.IsGoverned = false;
        using var page = Page(catalog);
        Assert.False(page.ShowsServerTab);
        Assert.Equal(0, catalog.CloudCalls);

        catalog.IsGoverned = true;
        catalog.Raise();
        await page.OpenAsync();

        Assert.True(page.ShowsServerTab);
        Assert.True(catalog.CloudCalls > 0);
        Assert.NotEmpty(page.CloudGroups);
    }

    [Fact]
    public void A_governed_agent_has_the_server_tab_and_loads_the_cloud_tools()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);

        Assert.True(page.ShowsServerTab);
        Assert.True(catalog.CloudCalls > 0);
        Assert.Equal("On your server · 9", page.ServerTabText);
        page.ShowsServer = true;
        Assert.Equal(ToolsTab.Server, page.Tab);
    }

    [Fact]
    public void Nobody_signed_in_has_no_server_tab_even_for_a_governed_catalog()
    {
        using var page = Page(new FakeLocalToolsCatalog { SignedIn = false, Policy = ToolsPolicy.Unknown });

        Assert.False(page.ShowsServerTab);
    }

    [Fact]
    public void The_server_tab_selected_and_then_the_connection_goes_standalone_selects_the_computers_tab()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.ShowsServer = true;
        Assert.Equal(ToolsTab.Server, page.Tab);

        catalog.IsGoverned = false;
        catalog.Raise();

        Assert.False(page.ShowsServerTab);
        Assert.Equal(ToolsTab.Computer, page.Tab);
        Assert.True(page.ShowsLocal);
        Assert.Empty(page.CloudGroups);
    }

    [Fact]
    public void A_language_change_says_the_new_texts_again()
    {
        var language = "en";
        using var page = Page(FakeLocalToolsCatalog.Scene(), () => UiStrings.For(CultureInfo.GetCultureInfo(language)));
        Assert.Equal("In this app · 4", page.AppTabText);
        Assert.Equal("Read", page.AppTools[0].ClassLabel);

        language = "de";
        page.Retext();

        Assert.Equal("In dieser App · 4", page.AppTabText);
        Assert.Equal(page.Strings.ApprovalClassLabel(ToolClass.Read), page.AppTools[0].ClassLabel);
        Assert.NotEqual("Read", page.AppTools[0].ClassLabel);
        Assert.Equal("Diese App gibt dem Assistenten keine eigenen Werkzeuge.", page.Strings.ToolsAppNone);
    }
}

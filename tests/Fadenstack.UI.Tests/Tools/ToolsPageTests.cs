using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The Tools page's view model (sdk/51 F05; the desktop's page tests without the built-in list and the assistants): the bar with the
/// tabs, the policy chip and the filter, the server cards in every state, the empty state and the server's tab.
/// </summary>
public class ToolsPageTests
{
    internal static ToolsViewModel Page(FakeLocalToolsCatalog catalog, FakeFiles? files = null, FakeUi? ui = null) => new(catalog, files ?? new FakeFiles(), ui ?? new FakeUi());

    internal static LocalServerViewModel Server(ToolsViewModel page, string label) => page.Servers.Single(s => s.Label == label);

    [Theory]
    [InlineData(ToolsPolicy.Allowed, "Allowed", "This assistant may use MCP servers on this computer.", false, false)]
    [InlineData(ToolsPolicy.DeveloperMode, "Developer mode", "Developer mode: any server, for you only.", true, false)]
    [InlineData(ToolsPolicy.NotAllowed, "Not allowed for this assistant",
        "This assistant may not use MCP servers on this computer.", true, true)]
    public void The_policy_is_a_chip_with_the_full_sentence_as_its_tooltip_and_a_banner_only_when_not_allowed(
        ToolsPolicy policy, string chip, string tip, bool warning, bool banner)
    {
        using var page = Page(new FakeLocalToolsCatalog { Policy = policy });

        Assert.Equal((chip, tip, warning, true), (page.PolicyText, page.PolicyTip, page.PolicyIsWarning, page.HasPolicy));
        Assert.Equal(banner, page.ShowsBanner);
        if (banner)
        {
            Assert.Equal(tip, page.BannerText);
        }
        Assert.True(page.ShowsLocalChip);
    }

    [Fact]
    public void An_unknown_policy_has_no_chip_and_no_banner()
    {
        using var page = Page(new FakeLocalToolsCatalog { Policy = ToolsPolicy.Unknown });

        Assert.False(page.HasPolicy);
        Assert.False(page.ShowsLocalChip);
        Assert.False(page.ShowsBanner);
    }

    [Fact]
    public void Without_a_sign_in_a_short_line_replaces_the_page()
    {
        using var page = Page(new FakeLocalToolsCatalog { SignedIn = false, Policy = ToolsPolicy.Unknown });

        Assert.True(page.NeedsSignIn);
        Assert.False(page.ShowsLists);
        Assert.Equal("Sign in to see the tools of this assistant.", page.Strings.ToolsSignInNeeded);
        Assert.False(page.ShowsBanner);
    }

    [Fact]
    public void The_tabs_count_the_tools_of_each_place_and_the_chip_follows_the_tab()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        Assert.Equal("On this computer · 9", page.LocalTabText); // 4 + 2 + 0 + 3
        Assert.Equal("On your server · 9", page.ServerTabText); // the page asks for the list when it is made
        Assert.True(page.ShowsLocal);
        Assert.True(page.ShowsLocalChip);
        Assert.False(page.ShowsManagedChip);
        page.ShowsServer = true;
        Assert.False(page.ShowsLocal);
        Assert.False(page.ShowsLocalChip);
        Assert.True(page.ShowsManagedChip);
    }

    [Fact]
    public void Scan_is_offered_only_when_the_app_has_a_scanner_and_Add_is_then_the_main_action_of_the_empty_state()
    {
        using var without = Page(new FakeLocalToolsCatalog { CanScan = false });
        Assert.False(without.CanScan);
        Assert.False(without.ScanIsPrimary);
        Assert.True(without.AddIsPrimary);
        without.ScanCommand.Execute(null);
        Assert.Null(without.Sheet);

        using var with = Page(new FakeLocalToolsCatalog { CanScan = true });
        Assert.True(with.ScanIsPrimary);
        Assert.False(with.AddIsPrimary);
    }

    [Fact]
    public void The_header_actions_are_for_the_computers_tab_and_the_servers_tab_is_read_only()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        Assert.Equal((true, true), (page.ShowsScanButton, page.ShowsAddButton));
        page.ShowsServer = true;
        Assert.Equal((false, false), (page.ShowsScanButton, page.ShowsAddButton));
        page.ShowsLocal = true;
        Assert.Equal((true, true), (page.ShowsScanButton, page.ShowsAddButton));
        using var without = Page(new FakeLocalToolsCatalog { CanScan = false });
        Assert.Equal((false, true), (without.ShowsScanButton, without.ShowsAddButton));
    }

    [Fact]
    public void Every_card_has_its_icon_origin_line_status_and_tool_chips()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        Assert.Equal(["demo", "notes-db", "github-local", "calendar"], page.Servers.Select(s => s.Label));
        var demo = Server(page, "demo");
        Assert.Equal(("Added by you · faden-mcp-demo.exe", "Running · 4 tools · told 1 folder", ServerStatusKind.Good, true, true),
            (demo.OriginText, demo.StatusText, demo.StatusKind, demo.IsProgram, demo.IsOn));
        Assert.Equal(["world_time", "allowed_folders", "long_report", "+1"], demo.Chips.Select(c => c.Name));
        Assert.Equal((true, "+1"), (demo.HasMore, demo.MoreText));
        Assert.True(demo.Chips[3].IsMore);

        var notes = Server(page, "notes-db");
        Assert.Equal("From Claude Desktop · npx @acme/notes-mcp", notes.OriginText);
        Assert.Equal([false, true], notes.Chips.Select(c => c.MayDelete)); // the server says delete_note may delete

        var broken = Server(page, "github-local");
        Assert.Equal(("Can’t start: GITHUB_TOKEN has no value", ServerStatusKind.Problem, true, "Add the value"),
            (broken.StatusText, broken.StatusKind, broken.HasFix, broken.FixText));

        var calendar = Server(page, "calendar");
        Assert.Equal(("Found by a scan · 127.0.0.1:8123/mcp", "Off · 3 tools", ServerStatusKind.Neutral, true, false),
            (calendar.OriginText, calendar.StatusText, calendar.StatusKind, calendar.IsAddress, calendar.IsOn));
    }

    [Fact]
    public void The_other_states_of_a_card_have_their_words()
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.Roots.Clear();
        catalog.Servers.AddRange(new[]
        {
            new LocalServerInfo { Label = "starting", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Starting },
            new LocalServerInfo { Label = "refused", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.NotAllowed },
            new LocalServerInfo { Label = "failed", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Failed, Detail = "spawn node ENOENT" },
            new LocalServerInfo { Label = "silent", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Failed },
            new LocalServerInfo
            {
                Label = "changed", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Running, ToolsChanged = true,
                Tools = new[] { new LocalToolInfo("t1", "", null, null, null, null, null) },
            },
            new LocalServerInfo
            {
                Label = "one", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Running,
                Tools = new[] { new LocalToolInfo("t1", "", null, null, null, null, null) },
            },
        });
        using var page = Page(catalog);

        Assert.Equal(("Starting", ServerStatusKind.Neutral), (Server(page, "starting").StatusText, Server(page, "starting").StatusKind));
        Assert.Equal(("Not allowed", ServerStatusKind.Warning), (Server(page, "refused").StatusText, Server(page, "refused").StatusKind));
        Assert.Equal("Not allowed: this assistant may not use MCP servers on this computer", Server(page, "refused").StatusTip);
        Assert.Equal(("Can’t start: spawn node ENOENT", ServerStatusKind.Problem, false), (Server(page, "failed").StatusText, Server(page, "failed").StatusKind, Server(page, "failed").HasFix));
        Assert.Equal("Can’t start: no reason given", Server(page, "silent").StatusText);
        Assert.Equal(("Changed its tools", ServerStatusKind.Warning), (Server(page, "changed").StatusText, Server(page, "changed").StatusKind));
        Assert.Equal("Running · 1 tool", Server(page, "one").StatusText); // no folder is allowed: the line does not say "told"
    }

    [Fact]
    public void A_card_opens_its_drawer_and_the_fix_opens_the_edit_sheet()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Drafts["github-local"] = new ServerDraft { Label = "github-local", Command = "docker" };
        using var page = Page(catalog);

        Server(page, "demo").OpenCommand.Execute(null);

        Assert.True(page.IsDetailsOpen);
        Assert.Equal("demo", page.Details!.Label);
        Assert.True(Server(page, "demo").IsSelected);
        Assert.False(Server(page, "notes-db").IsSelected);
        Assert.Equal("Show details of demo", Server(page, "demo").OpenName);

        Server(page, "github-local").FixCommand.Execute(null);
        Assert.False(page.IsDetailsOpen);
        Assert.Equal("Edit github-local", Assert.IsType<ServerSheetViewModel>(page.Sheet).Title);
    }

    [Fact]
    public async Task The_switch_goes_to_the_catalog_and_a_refresh_does_not_send_it_back()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);

        Server(page, "notes-db").IsOn = false;
        await Task.Delay(50);
        catalog.Servers[3].Enabled = true;
        page.Refresh();

        Assert.Equal(["enable notes-db False"], catalog.Calls);
        Assert.True(Server(page, "calendar").IsOn);
        Assert.False(Server(page, "notes-db").IsOn);
        Assert.Equal("Switch notes-db on or off", Server(page, "notes-db").SwitchName);
    }

    [Fact]
    public void The_servers_kept_in_memory_or_unreadable_are_said()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ServersKeptOnDisk = false;
        using var page = Page(catalog);

        Assert.Equal("This computer has no key store, so your servers are kept only until you close the app.", page.ListNote);
        catalog.ServersUnreadable = true;
        page.Refresh();
        Assert.StartsWith("The saved list of servers could not be read.", page.ListNote);
        catalog.ServersUnreadable = false;
        catalog.ServersKeptOnDisk = true;
        page.Refresh();
        Assert.False(page.HasListNote);
    }

    [Fact]
    public void A_change_of_the_catalog_from_another_thread_is_brought_to_the_ui_thread_and_refreshes_the_page_once()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        var ui = new FakeUi { Hold = true };
        using var page = new ToolsViewModel(catalog, new FakeFiles(), ui);

        catalog.Servers.RemoveAt(3);
        catalog.Raise();
        catalog.Raise();

        Assert.Equal(1, ui.Pending);
        Assert.Equal(4, page.Servers.Count);
        ui.Flush();
        Assert.Equal(["demo", "notes-db", "github-local"], page.Servers.Select(s => s.Label));
        Assert.Equal("On this computer · 6", page.LocalTabText);
    }

    [Fact]
    public async Task The_filter_narrows_the_servers_and_the_cloud_list_by_a_tool_or_a_server_name()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());
        await page.LoadCloudAsync();

        page.Filter = "delete";
        Assert.Equal(["notes-db"], page.Servers.Select(s => s.Label));
        Assert.Equal(["github"], page.CloudGroups.Select(g => g.Server));
        Assert.Equal(["delete_repository"], page.CloudGroups[0].Tools.Select(t => t.Name));
        Assert.False(page.ShowsNoMatch);

        page.Filter = "nothing-like-this";
        Assert.Empty(page.Servers);
        Assert.True(page.ShowsNoMatch);

        page.Filter = "everything"; // a cloud server's own name lets all its tools through
        Assert.Equal(["everything"], page.CloudGroups.Select(g => g.Server));
        Assert.Equal(6, page.CloudGroups[0].Tools.Count);

        page.Filter = "";
        Assert.Equal(4, page.Servers.Count);
        Assert.False(page.ShowsNoMatch);
        Assert.Equal(2, page.CloudGroups.Count);
        Assert.Equal("On your server · 9", page.ServerTabText); // the count is of the tools, not of the filter
    }

    [Fact]
    public void With_no_server_the_empty_state_shows_and_the_first_server_replaces_it()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Servers.Clear();
        using var page = Page(catalog);

        Assert.True(page.ShowsEmpty);
        Assert.False(page.ShowsServerCards);
        Assert.False(page.HasServers);
        Assert.Equal("Give your assistant tools from this computer", page.Strings.ToolsEmptyTitle);

        catalog.Servers.Add(new LocalServerInfo { Label = "x", Where = "node", IsProgram = true, Enabled = true, State = LocalServerState.Off });
        page.Refresh();
        Assert.False(page.ShowsEmpty);
        Assert.True(page.ShowsServerCards);
    }

    [Fact]
    public async Task The_server_tab_has_a_card_per_server_with_what_this_assistant_may_do()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());

        await page.OpenAsync();

        Assert.Equal(["everything", "github"], page.CloudGroups.Select(g => g.Server));
        var everything = page.CloudGroups[0];
        Assert.Equal("6 tools · 4 for this assistant · 1 switched off", everything.Summary);
        Assert.Equal(["For this assistant", "For this assistant", "For this assistant", "For this assistant", "Not for this assistant", "Switched off by your administrator"],
            everything.Tools.Select(t => t.StateText));
        Assert.True(everything.Tools[5].IsOff);
        Assert.Equal("On your server · 9", page.ServerTabText);
        Assert.False(page.CanRetry);
    }

    [Fact]
    public async Task A_cloud_card_collapses_and_expands()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());
        await page.OpenAsync();
        var group = page.CloudGroups[0];

        Assert.True(group.IsExpanded);
        group.ToggleCommand.Execute(null);
        Assert.True(group.IsCollapsed);
        group.ToggleCommand.Execute(null);
        Assert.True(group.IsExpanded);
        Assert.Equal(("Collapse everything", "Expand everything"), (group.CollapseName, group.ExpandName));
    }

    [Fact]
    public async Task A_server_without_the_list_says_so_an_error_offers_try_again_and_an_empty_list_says_that()
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.CloudAnswers.Clear();
        catalog.CloudAnswers.Add(new CloudToolsResult(CloudToolsStatus.NotListed, Array.Empty<CloudToolInfo>()));
        catalog.CloudAnswers.Add(new CloudToolsResult(CloudToolsStatus.Unavailable, Array.Empty<CloudToolInfo>()));
        catalog.CloudAnswers.Add(new CloudToolsResult(CloudToolsStatus.Listed, Array.Empty<CloudToolInfo>()));
        using var page = Page(catalog);

        catalog.ResetCloud(); // the page asked once when it was made
        await page.LoadCloudAsync();
        Assert.Equal(("This server does not list its tools yet.", false), (page.CloudMessage, page.CanRetry));
        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal(("The server’s tools could not be loaded right now.", true), (page.CloudMessage, page.CanRetry));
        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal(("Your server offers no tools to this assistant.", false), (page.CloudMessage, page.CanRetry));
    }

    [Fact]
    public async Task Without_a_sign_in_the_cloud_list_is_not_asked_for()
    {
        var catalog = new FakeLocalToolsCatalog { SignedIn = false };
        using var page = Page(catalog);

        await page.LoadCloudAsync();

        Assert.Equal(0, catalog.CloudCalls);
        Assert.Equal("On your server · 0", page.ServerTabText);
    }

    [Fact]
    public void Esc_closes_the_sheet_first_and_then_the_drawer()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());
        Server(page, "demo").OpenCommand.Execute(null);
        page.AddServerCommand.Execute(null);

        Assert.False(page.IsDetailsOpen); // adding closes the drawer
        Server(page, "demo").OpenCommand.Execute(null);
        page.ImportServersCommand.Execute(null);
        Assert.True(page.HasSheet && page.IsDetailsOpen);

        page.CloseTopCommand.Execute(null);
        Assert.False(page.HasSheet);
        Assert.True(page.IsDetailsOpen);
        page.CloseTopCommand.Execute(null);
        Assert.False(page.IsDetailsOpen);
    }

    [Fact]
    public void The_dates_and_numbers_follow_the_language()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var german = new ToolsViewModel(catalog, new FakeFiles(), new FakeUi(), () => UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("de")));

        Server(german, "demo").OpenCommand.Execute(null);

        Assert.StartsWith("Läuft seit", german.Details!.StatusText);
        Assert.Equal("Aus", Server(german, "calendar").StatusText.Split(' ')[0]);
    }

    [Fact]
    public void Retext_says_the_texts_again_in_the_new_language()
    {
        var language = "en";
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = new ToolsViewModel(catalog, new FakeFiles(), new FakeUi(), () => UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language)));
        var seen = new List<string?>();
        page.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.Equal("Allowed", page.PolicyText);

        language = "de";
        page.Retext();

        Assert.Contains(nameof(ToolsViewModel.Strings), seen);
        Assert.Equal("Erlaubt", page.PolicyText);
    }
}

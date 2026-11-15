using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>The scan sheet over a fake catalog: where it looks, the five states of a finding, Add n, Cancel.</summary>
public class ToolsScanTests
{
    internal static ScanResultInfo Result() => new()
    {
        Seconds = 4.2,
        PortsChecked = 2000,
        Apps = new[]
        {
            new ScanAppInfo("Claude Desktop", true, 2), new ScanAppInfo("VS Code", true, 1), new ScanAppInfo("Cursor", true, 1), new ScanAppInfo("Claude Code", true, 0),
            new ScanAppInfo("Windsurf", false, 0), new ScanAppInfo("Gemini CLI", false, 0), new ScanAppInfo("LM Studio", false, 0),
        },
        Findings = new[]
        {
            new ScanFindingInfo { Id = "0", Label = "notes-db", FoundIn = "Claude Desktop", FromApp = true, IsProgram = true, Where = "npx @acme/notes-mcp", State = ScanFindingState.AlreadyAdded },
            new ScanFindingInfo
            {
                Id = "1", Label = "github", FoundIn = "Claude Desktop", FromApp = true, IsProgram = true, Where = "docker run -i ghcr.io/github/github-mcp-server",
                State = ScanFindingState.New, SecretNames = new[] { "GITHUB_TOKEN" },
            },
            new ScanFindingInfo { Id = "2", Label = "playwright", FoundIn = "VS Code", FromApp = true, IsProgram = true, Where = "npx @playwright/mcp@latest", State = ScanFindingState.New },
            new ScanFindingInfo { Id = "3", Label = "linear", FoundIn = "Cursor", FromApp = true, IsProgram = false, Where = "https://mcp.linear.app/sse", State = ScanFindingState.NetworkLater },
            new ScanFindingInfo
            {
                Id = "4", Label = "calendar", FoundIn = "", FromApp = false, IsProgram = false, Where = "http://127.0.0.1:8123/mcp", State = ScanFindingState.New,
                ServerName = "calendar-server 1.2", ToolCount = 3,
            },
        },
    };

    private static (ToolsViewModel Page, ScanSheetViewModel Sheet, FakeLocalToolsCatalog Catalog) Open(ScanResultInfo? result = null, Action<FakeLocalToolsCatalog>? arrange = null)
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanAnswer = result ?? Result();
        arrange?.Invoke(catalog);
        var page = Page(catalog);
        page.ScanCommand.Execute(null);
        return (page, Assert.IsType<ScanSheetViewModel>(page.Sheet), catalog);
    }

    [Fact]
    public void A_scan_never_starts_by_itself_and_the_click_opens_the_sheet_and_scans()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanAnswer = Result();
        using var page = Page(catalog);

        Assert.Null(page.Sheet);
        Assert.DoesNotContain(catalog.Calls, c => c.StartsWith("scan", StringComparison.Ordinal));
        page.ScanCommand.Execute(null);

        Assert.IsType<ScanSheetViewModel>(page.Sheet);
        Assert.Equal(["scan apps=True computer=True"], catalog.Calls);
    }

    [Fact]
    public void The_sheet_says_where_it_looked_the_apps_with_their_counts_and_the_ports()
    {
        var (page, sheet, _) = Open();
        using var _p = page;

        Assert.Equal(["Claude Desktop · 2", "VS Code · 1", "Cursor · 1", "Claude Code · 0"], sheet.Apps.Select(a => a.Text));
        Assert.Equal(("Windsurf, Gemini CLI, LM Studio: not installed", true), (sheet.NotInstalledText, sheet.HasNotInstalled));
        Assert.Equal("localhost, ports 3000–3999 and 8000–8999. It only says hello in MCP; nothing is sent.", sheet.PortsText);
        Assert.Equal(("Done in 4 s · 5 servers found", true, false, false), (sheet.StatusText, sheet.IsDone, sheet.IsProblem, sheet.IsScanning));
        Assert.Equal("Nothing leaves this computer. A server starts only after you add it, and only where this assistant may use MCP servers.", sheet.Strings.ToolsScanFooter);
    }

    [Fact]
    public void Before_the_result_the_apps_are_listed_by_name_and_the_progress_follows_the_steps()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanAnswer = Result();
        catalog.ScanGate = new TaskCompletionSource<bool>();
        using var page = Page(catalog);

        page.ScanCommand.Execute(null);
        var sheet = (ScanSheetViewModel)page.Sheet!;

        Assert.Equal(["Claude Desktop", "VS Code", "Cursor", "Claude Code", "Windsurf", "Gemini CLI", "LM Studio"], sheet.Apps.Select(a => a.Text));
        Assert.True(sheet.IsScanning);
        Assert.False(sheet.CanScanAgain);
        Assert.False(sheet.AddCommand.CanExecute(null));
        Assert.Equal((1d, 4d, "Looking… 1 of 4 done"), (sheet.ProgressDone, sheet.ProgressTotal, sheet.ProgressText));
        catalog.ScanGate.SetResult(true);
        Assert.False(sheet.IsScanning);
        Assert.Equal((4d, 4d, true), (sheet.ProgressDone, sheet.ProgressTotal, sheet.IsDone));
    }

    [Fact]
    public void The_progress_is_brought_to_the_ui_thread()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanAnswer = Result();
        var ui = new FakeUi { Hold = true };
        using var page = new ToolsViewModel(catalog, new FakeFiles(), ui);

        page.ScanCommand.Execute(null);
        var sheet = (ScanSheetViewModel)page.Sheet!;

        Assert.True(ui.Pending > 0); // the steps wait for the UI thread
        ui.Flush();
        Assert.Equal("Looking… 4 of 4 done", sheet.ProgressText);
    }

    [Fact]
    public void The_findings_are_grouped_by_where_they_were_found()
    {
        var (page, sheet, _) = Open();
        using var _p = page;

        Assert.Equal(["Found in Claude Desktop", "Found in VS Code", "Found in Cursor", "Running on this computer"], sheet.Groups.Select(g => g.Title));
        Assert.Equal([2, 1, 1, 1], sheet.Groups.Select(g => g.Rows.Count));
        Assert.True(sheet.HasFindings);
    }

    [Fact]
    public void Each_of_the_five_states_has_its_tick_text_and_notes()
    {
        var result = Result();
        result.Findings = result.Findings.Concat(new[]
        {
            new ScanFindingInfo { Id = "5", Label = "needs", FoundIn = "Cursor", FromApp = true, IsProgram = true, Where = "node x.js", State = ScanFindingState.NeedsValue, SecretNames = new[] { "API_KEY", "OTHER" } },
            new ScanFindingInfo { Id = "6", Label = "odd", FoundIn = "Cursor", FromApp = true, IsProgram = false, Where = "?", State = ScanFindingState.NotUnderstood, Detail = "It has neither a program nor an address." },
        }).ToList();
        var (page, sheet, _) = Open(result);
        using var _p = page;
        var rows = sheet.Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Label);

        var already = rows["notes-db"];
        Assert.Equal((true, false, true, "Already added", false), (already.IsTicked, already.CanTick, already.IsDimmed, already.SideText, already.SideIsWarning));
        Assert.Equal("notes-db, already added", already.TickName);
        Assert.False(already.IsChosen);

        var github = rows["github"];
        Assert.Equal((true, true, "Program", "Add github"), (github.IsTicked, github.CanTick, github.SideText, github.TickName));
        var note = Assert.Single(github.Notes);
        Assert.Equal(("Takes GITHUB_TOKEN from Claude Desktop’s settings; kept encrypted here", true), (note.Text, note.IsSecret));

        var linear = rows["linear"];
        Assert.Equal((false, false, true, "On another computer: add it yourself", true), (linear.IsTicked, linear.CanTick, linear.IsMutedName, linear.SideText, linear.SideIsWarning));
        Assert.Equal("linear, not added by a scan", linear.TickName);

        var calendar = rows["calendar"];
        Assert.Equal(("Address", "Answered as calendar-server 1.2 · 3 tools"), (calendar.SideText, Assert.Single(calendar.Notes).Text));

        var needs = rows["needs"];
        Assert.Equal((true, true), (needs.IsTicked, needs.CanTick));
        Assert.Contains(needs.Notes, n => n.IsWarning && n.Text == "Needs a value; added switched off");

        var odd = rows["odd"];
        Assert.Equal((false, false, "Not understood"), (odd.IsTicked, odd.CanTick, odd.SideText));
        Assert.Equal("It has neither a program nor an address.", Assert.Single(odd.Notes).Text);
    }

    [Fact]
    public async Task Add_counts_what_is_ticked_and_addable_and_adds_those_and_closes_the_sheet()
    {
        var (page, sheet, catalog) = Open();
        using var _p = page;

        Assert.Equal(("Add 3 servers", 3), (sheet.AddText, sheet.ChosenCount)); // github, playwright, calendar
        sheet.Groups[1].Rows[0].IsTicked = false;
        Assert.Equal("Add 2 servers", sheet.AddText);

        await sheet.AddCommand.ExecuteAsync(null);

        Assert.Equal(["1", "4"], catalog.AddedFound);
        Assert.Null(page.Sheet);
    }

    [Fact]
    public void One_server_says_so_and_nothing_found_says_that()
    {
        var one = new ScanResultInfo { Apps = Array.Empty<ScanAppInfo>(), Findings = new[] { Result().Findings[1] } };
        var (page, sheet, _) = Open(one);
        using var _p = page;
        Assert.Equal(("Add 1 server", "Done in 0 s · 1 server found"), (sheet.AddText, sheet.StatusText));

        var (page2, sheet2, _) = Open(new ScanResultInfo());
        using var _p2 = page2;
        Assert.Equal(("Nothing found", "No MCP servers were found. You can add one yourself."), (sheet2.StatusText.Split('·').Last().Trim(), sheet2.EmptyText));
        Assert.False(sheet2.AddCommand.CanExecute(null));
    }

    [Fact]
    public void The_person_ticks_where_to_look_and_without_a_place_nothing_is_scanned()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanAnswer = Result();
        using var page = Page(catalog);
        page.ScanCommand.Execute(null);
        var sheet = (ScanSheetViewModel)page.Sheet!;
        catalog.Calls.Clear();

        sheet.OtherApps = false;
        sheet.StartCommand.Execute(null);
        Assert.Equal((false, true), (catalog.ScanOptionsSeen.Last().OtherApps, catalog.ScanOptionsSeen.Last().ThisComputer));

        sheet.ThisComputer = false;
        catalog.Calls.Clear();
        sheet.StartCommand.Execute(null);
        Assert.Empty(catalog.Calls);
        Assert.Equal(("Tick at least one place to look in.", true), (sheet.StatusText, sheet.IsProblem));
    }

    [Fact]
    public void Cancel_stops_a_running_scan_and_closes_the_sheet_and_a_failed_scan_says_so_without_its_error()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ScanGate = new TaskCompletionSource<bool>();
        using var page = Page(catalog);
        page.ScanCommand.Execute(null);
        var sheet = (ScanSheetViewModel)page.Sheet!;
        Assert.True(sheet.IsScanning);

        sheet.CancelCommand.Execute(null);

        Assert.Null(page.Sheet);

        var failing = FakeLocalToolsCatalog.Scene();
        failing.ScanFault = new InvalidOperationException("C:\\Users\\anna\\secret-path");
        using var page2 = Page(failing);
        page2.ScanCommand.Execute(null);
        var sheet2 = (ScanSheetViewModel)page2.Sheet!;
        Assert.Equal(("The scan could not finish. Try again.", true), (sheet2.StatusText, sheet2.IsProblem));
        Assert.DoesNotContain("secret-path", sheet2.StatusText);
    }
}

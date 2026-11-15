using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>The modal sheets of the Tools page: add or edit a server (and how its secret values travel), and import a file.</summary>
public class ToolsSheetTests
{
    [Fact]
    public void The_add_sheet_says_Add_offers_the_import_and_the_edit_sheet_says_Save()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Drafts["demo"] = new ServerDraft { Label = "demo", Command = "faden-mcp-demo.exe" };
        using var page = Page(catalog);

        page.AddServerCommand.Execute(null);
        var add = Assert.IsType<ServerSheetViewModel>(page.Sheet);
        Assert.Equal(("Add a server", "Add", true, true), (add.Title, add.PrimaryText, add.IsAdding, add.IsProgram));
        add.ImportCommand.Execute(null);
        Assert.IsType<ImportSheetViewModel>(page.Sheet);

        Server(page, "demo").OpenCommand.Execute(null);
        page.Details!.EditCommand.Execute(null);
        var edit = Assert.IsType<ServerSheetViewModel>(page.Sheet);
        Assert.Equal(("Edit demo", "Save", false), (edit.Title, edit.PrimaryText, edit.IsAdding));
    }

    [Fact]
    public void The_kind_is_a_program_or_an_address_and_the_address_help_says_what_http_may_do()
    {
        using var page = Page(FakeLocalToolsCatalog.Scene());
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;

        Assert.Equal(("Program", "Address"), (sheet.Strings.ToolsKindProgram, sheet.Strings.ToolsKindAddress));
        sheet.IsAddress = true;
        Assert.False(sheet.IsProgram);
        sheet.IsProgram = true;
        Assert.False(sheet.IsAddress);
        Assert.Contains("https", sheet.Strings.ToolsFieldUrlHelp);
        Assert.Contains("Advanced", sheet.Strings.ToolsFieldUrlHelp);
    }

    [Fact]
    public async Task Adding_a_server_checks_it_saves_it_with_its_lists_and_closes_the_sheet()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = Assert.IsType<ServerSheetViewModel>(page.Sheet);
        sheet.Label = "files";
        sheet.Command = "node";
        sheet.ArgumentsText = "server.js\r\n--port\n8123\n";
        sheet.AddEnvironmentCommand.Execute(null);
        sheet.EnvironmentRows[0].Name = "API_KEY";
        sheet.EnvironmentRows[0].Value = "s3cret";

        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Null(page.Sheet);
        Assert.Equal(["save new"], catalog.Calls);
        var saved = catalog.Saved!;
        Assert.Equal(("files", true, "node"), (saved.Label, saved.IsProgram, saved.Command));
        Assert.Equal(["server.js", "--port", "8123"], saved.Arguments);
        Assert.Equal(("API_KEY", "s3cret", false), (saved.Environment[0].Name, saved.Environment[0].Value, saved.Environment[0].IsKept));
        Assert.Equal(120, saved.CallTimeoutSeconds);
    }

    [Theory]
    [InlineData(ServerProblem.LabelTaken, "You have a server with that name already.")]
    [InlineData(ServerProblem.LabelInvalid, "Use lower case letters, digits and hyphens, up to 32.")]
    [InlineData(ServerProblem.CommandMissing, "Enter the program to start.")]
    [InlineData(ServerProblem.AddressInvalid, "That is not an address (for example http://localhost:8123/mcp).")]
    [InlineData(ServerProblem.TimeoutOutOfRange, "Enter a number of seconds from 5 to 600.")]
    public void A_problem_keeps_the_sheet_open_and_says_what_is_wrong(ServerProblem problem, string text)
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ValidateAnswer = problem;
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.IsProgram = false;
        sheet.Url = "https://mcp.example.com/";

        sheet.SaveCommand.Execute(null);

        Assert.NotNull(page.Sheet);
        Assert.Equal(text, sheet.ErrorText);
        Assert.DoesNotContain("save new", catalog.Calls);
    }

    [Fact]
    public async Task A_problem_the_catalog_finds_on_saving_is_shown_too_and_the_sheet_stays()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.SaveAnswer = ServerProblem.LabelTaken;
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;

        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(page.Sheet);
        Assert.Equal("You have a server with that name already.", sheet.ErrorText);
    }

    [Fact]
    public async Task A_timeout_that_is_no_number_is_refused()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.TimeoutText = "two";

        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Enter a number of seconds from 5 to 600.", sheet.ErrorText);
        Assert.Empty(catalog.Calls);
    }

    [Fact]
    public async Task Editing_shows_saved_values_masked_and_replace_sets_a_new_one()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Drafts["notes-db"] = new ServerDraft
        {
            Label = "notes-db", Command = "node", Arguments = new List<string> { "a.js" },
            Environment = new List<SecretPairDraft> { new() { Name = "API_KEY", IsKept = true }, new() { Name = "OTHER", IsKept = true } },
            CallTimeoutSeconds = 90,
        };
        using var page = Page(catalog);
        Server(page, "notes-db").OpenCommand.Execute(null);
        page.Details!.EditCommand.Execute(null);
        var sheet = Assert.IsType<ServerSheetViewModel>(page.Sheet);

        Assert.Equal(("Edit notes-db", "notes-db", "node", "a.js", "90"), (sheet.Title, sheet.Label, sheet.Command, sheet.ArgumentsText, sheet.TimeoutText));
        Assert.All(sheet.EnvironmentRows, r => Assert.Equal(("", true, false), (r.Value, r.IsKept, r.ShowsInput)));
        Assert.Equal(("••••••••", "Saved value (hidden)"), (sheet.Strings.ToolsValueSaved, sheet.Strings.ToolsValueSavedName));

        sheet.EnvironmentRows[0].ReplaceCommand.Execute(null);
        sheet.EnvironmentRows[0].Value = "new-secret";
        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Equal("notes-db", catalog.SavedOriginal);
        var env = catalog.Saved!.Environment;
        Assert.Equal(("API_KEY", "new-secret", false), (env[0].Name, env[0].Value, env[0].IsKept));
        Assert.Equal(("OTHER", "", true), (env[1].Name, env[1].Value, env[1].IsKept));
    }

    // ---- secrets (OWASP ASVS 5.0 V13) --------------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_save_no_typed_value_stays_in_the_sheet_the_rows_or_the_draft_given_to_the_catalog()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.Label = "api";
        sheet.IsProgram = false;
        sheet.Url = "https://mcp.example.com/mcp";
        sheet.AddHeaderCommand.Execute(null);
        sheet.HeaderRows[0].Name = "Authorization";
        sheet.HeaderRows[0].Value = "Bearer TOP-SECRET";
        var row = sheet.HeaderRows[0];

        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Bearer TOP-SECRET", catalog.SecretValuesSeen); // the catalog got the value to store it
        Assert.Equal("", row.Value); // and nothing is left in the row
        Assert.All(catalog.DraftsGiven.SelectMany(d => d.Environment.Concat(d.Headers)), p => Assert.Equal("", p.Value)); // nor in the draft
        Assert.Null(page.Sheet);
    }

    [Fact]
    public void Cancelling_forgets_the_typed_values_and_so_does_closing_the_sheet_any_other_way()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.AddEnvironmentCommand.Execute(null);
        sheet.EnvironmentRows[0].Value = "typed-secret";
        var row = sheet.EnvironmentRows[0];

        sheet.CancelCommand.Execute(null);

        Assert.Equal("", row.Value);
        Assert.Null(page.Sheet);

        // Replaced by another sheet (the import), disposed with the page: the same.
        page.AddServerCommand.Execute(null);
        var second = (ServerSheetViewModel)page.Sheet!;
        second.AddHeaderCommand.Execute(null);
        second.HeaderRows[0].Value = "another-secret";
        var other = second.HeaderRows[0];
        page.ImportServersCommand.Execute(null);
        Assert.Equal("", other.Value);

        page.AddServerCommand.Execute(null);
        var third = (ServerSheetViewModel)page.Sheet!;
        third.AddHeaderCommand.Execute(null);
        third.HeaderRows[0].Value = "third-secret";
        var last = third.HeaderRows[0];
        page.Dispose();
        Assert.Equal("", last.Value);
    }

    [Fact]
    public async Task A_test_clears_the_draft_but_keeps_what_the_person_typed_so_that_they_can_save_it_after()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.Label = "files";
        sheet.Command = "node";
        sheet.AddEnvironmentCommand.Execute(null);
        sheet.EnvironmentRows[0].Name = "API_KEY";
        sheet.EnvironmentRows[0].Value = "typed-secret";

        await sheet.TestCommand.ExecuteAsync(null);

        Assert.Contains("typed-secret", catalog.SecretValuesSeen);
        Assert.All(catalog.DraftsGiven.SelectMany(d => d.Environment), p => Assert.Equal("", p.Value));
        Assert.Equal("typed-secret", sheet.EnvironmentRows[0].Value);
    }

    [Fact]
    public async Task A_kept_value_never_reaches_the_view_model_and_a_saved_value_is_not_in_any_text()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.Drafts["notes-db"] = new ServerDraft
        {
            Label = "notes-db", Command = "node", Environment = new List<SecretPairDraft> { new() { Name = "API_KEY", IsKept = true } }, CallTimeoutSeconds = 90,
        };
        using var page = Page(catalog);
        Server(page, "notes-db").OpenCommand.Execute(null);
        page.Details!.EditCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;

        await sheet.TestCommand.ExecuteAsync(null);
        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Equal([""], catalog.SecretValuesSeen.Distinct());
        Assert.True(catalog.DraftsGiven.All(d => d.Environment[0].IsKept));
    }

    [Fact]
    public async Task The_sheet_chooses_a_folder_and_cancel_closes_it()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog, new FakeFiles { Answer = "/home/anna/tools" });
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.Label = "files";
        sheet.Command = "node";

        await sheet.ChooseFolderCommand.ExecuteAsync(null);
        await sheet.TestCommand.ExecuteAsync(null);

        Assert.Equal("/home/anna/tools", sheet.WorkingFolder);
        Assert.Equal("Started. Tools: add, now", sheet.TestText);
        Assert.Contains("test files", catalog.Calls);
        sheet.CancelCommand.Execute(null);
        Assert.Null(page.Sheet);
    }

    // ---- the import sheet ----------------------------------------------------------------------------------------------

    private static ImportPreview Preview() => new()
    {
        Path = "/home/anna/.cursor/mcp.json",
        Servers = new[]
        {
            new ImportCandidateInfo { Label = "docs", OriginalName = "docs", Where = "python", Notes = new[] { ImportNote.NeedsValue, ImportNote.IgnoredFields }, Detail = "envFile" },
            new ImportCandidateInfo { Label = "brave-search", OriginalName = "Brave Search", Where = "npx", Notes = new[] { ImportNote.Renamed } },
        },
        Skipped = new[]
        {
            new ImportSkipInfo { Name = "remote", Reason = ImportSkipReason.NetworkServer },
            new ImportSkipInfo { Name = "empty", Reason = ImportSkipReason.NothingToStart },
            new ImportSkipInfo { Name = "odd", Reason = ImportSkipReason.NotValid, Detail = "label" },
        },
    };

    [Fact]
    public async Task The_import_sheet_lists_servers_with_tick_boxes_notes_and_the_skipped_with_reasons()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.PreviewAnswer = Preview();
        using var page = Page(catalog);
        page.ImportServersCommand.Execute(null);
        var sheet = Assert.IsType<ImportSheetViewModel>(page.Sheet);

        await sheet.ReadCursorCommand.ExecuteAsync(null);

        Assert.Equal("Read mcp.json", sheet.StatusText);
        Assert.Equal(["docs", "brave-search"], sheet.Candidates.Select(c => c.Label));
        Assert.All(sheet.Candidates, c => Assert.False(c.IsTicked)); // nothing is copied without the step
        Assert.Equal(["Needs a value you must fill in; it is added switched off.", "Left out: envFile"], sheet.Candidates[0].Notes);
        Assert.Equal("Import docs", sheet.Candidates[0].TickName);
        Assert.Equal(
            [("remote", "A server on another computer is not imported. Add it yourself if you need it."), ("empty", "It has no program or address."), ("odd", "It breaks a rule: label")],
            sheet.Skipped.Select(s => (s.Name, s.Reason)));
        Assert.False(sheet.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_the_ticked_servers_are_imported()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.PreviewAnswer = Preview();
        using var page = Page(catalog);
        page.ImportServersCommand.Execute(null);
        var sheet = (ImportSheetViewModel)page.Sheet!;
        await sheet.ReadCursorCommand.ExecuteAsync(null);

        sheet.Candidates[1].IsTicked = true;
        Assert.True(sheet.ImportCommand.CanExecute(null));
        await sheet.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["brave-search"], catalog.Imported);
        Assert.Equal("Added: 1", sheet.ResultText);
    }

    [Fact]
    public async Task A_picked_file_is_read_and_a_file_that_is_missing_or_bad_says_so()
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        using var page = Page(catalog, new FakeFiles { Answer = "/work/.mcp.json" });
        page.ImportServersCommand.Execute(null);
        var sheet = (ImportSheetViewModel)page.Sheet!;

        catalog.PreviewAnswer = new ImportPreview { Problem = ImportProblem.NotFound };
        await sheet.ReadFileCommand.ExecuteAsync(null);
        Assert.Equal("That file was not found on this computer.", sheet.StatusText);
        Assert.Contains("read ProjectFile /work/.mcp.json", catalog.Calls);

        catalog.PreviewAnswer = new ImportPreview { Problem = ImportProblem.NoServerList };
        await sheet.ReadClaudeCommand.ExecuteAsync(null);
        Assert.Equal("That file has no list of servers.", sheet.StatusText);

        catalog.PreviewAnswer = new ImportPreview { Problem = ImportProblem.None, Path = "/x/mcp.json" };
        await sheet.ReadVsCodeCommand.ExecuteAsync(null);
        Assert.Equal("Nothing in that file can be imported.", sheet.ResultText);
    }
}

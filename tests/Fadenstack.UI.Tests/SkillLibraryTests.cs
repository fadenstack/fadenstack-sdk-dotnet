using System.IO.Compression;
using System.Text;
using Fadenstack.Models;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Skills;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The skill library's view model: the list with its labels and switches, and new, edit, duplicate, delete, import and export.</summary>
public class SkillLibraryTests
{
    private const string GoodSkillMd = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n## Goal\nReview it.\n";

    private static string WriteZip(SkillRig rig, string file, params (string Name, string Text)[] entries)
    {
        Directory.CreateDirectory(rig.Root);
        var path = Path.Combine(rig.Root, file);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in entries)
            {
                using var content = archive.CreateEntry(name).Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                content.Write(bytes, 0, bytes.Length);
            }
        }
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    [Fact]
    public async Task The_list_has_bundled_own_and_organisation_skills_with_their_labels_and_switches()
    {
        using var rig = new SkillRig(_ => Task.FromResult<IReadOnlyList<SkillOption>>(new[] { new SkillOption { Slug = "org-policy", Name = "Org policy", Description = "From the server." } }));
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();

        await library.RefreshAsync();

        Assert.Equal(new[] { "slide-design", "contract-review", "org-policy" }, library.Rows.Select(r => r.Name));
        var (bundled, own, org) = (library.Rows[0], library.Rows[1], library.Rows[2]);
        Assert.Equal(("Built in", "", false), (bundled.OriginLabel, bundled.StatusLabel, bundled.CanDelete));
        Assert.Equal(("Yours", "Not reviewed", true), (own.OriginLabel, own.StatusLabel, own.CanDelete));
        Assert.Equal(("Your organisation", "", false), (org.OriginLabel, org.StatusLabel, org.CanDelete));
        Assert.True(bundled.CanSwitch && own.CanSwitch && !org.CanSwitch);
        Assert.True(bundled.IsReadOnly && !own.IsReadOnly && org.IsReadOnly);
        Assert.True(bundled.CanOpen && own.CanOpen && !org.CanOpen);
        Assert.Equal("On", own.SwitchLabel);
        Assert.False(library.IsLoading);
        Assert.Equal("", library.Note);
    }

    [Fact]
    public async Task An_empty_library_and_one_that_cannot_be_read_say_so()
    {
        using var rig = new SkillRig();
        rig.Options.Bundled.Clear();
        var library = rig.Library();
        Assert.Equal("", library.Note);

        await library.RefreshAsync();
        Assert.True(library.IsEmpty);
        Assert.Equal("There are no skills yet. Create one, or import a folder or a zip.", library.Note);

        var broken = new SkillLibraryViewModel(new ThrowingWorkshop(), strings: () => SkillRig.English);
        await broken.RefreshAsync();
        Assert.True(broken.Failed);
        Assert.Equal("The skills could not be read.", broken.Note);
    }

    [Fact]
    public async Task The_labels_are_said_in_the_language_of_the_texts_and_change_with_it()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));
        await library.RefreshAsync();
        var own = library.Rows.Single(r => r.Name == "contract-review");
        Assert.Equal("Not reviewed", own.StatusLabel);

        language = "de";
        library.Retext();
        Assert.Equal(("Nicht geprüft", "Eigene"), (own.StatusLabel, own.OriginLabel));
        language = "zh-Hans";
        library.Retext();
        Assert.Equal("未经审核", own.StatusLabel);
    }

    [Fact]
    public async Task The_switch_turns_a_skill_off_in_the_workshop_and_the_list_shows_it()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();
        await library.RefreshAsync();
        var own = library.Rows.Single(r => r.Name == "contract-review");

        await library.ToggleCommand.ExecuteAsync(own);

        Assert.False(rig.Options.Switches!.IsEnabled("contract-review"));
        Assert.False(library.Rows.Single(r => r.Name == "contract-review").IsEnabled);
        Assert.Equal("Off", library.Rows.Single(r => r.Name == "contract-review").SwitchLabel);
        await library.ToggleCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));
        Assert.True(rig.Options.Switches.IsEnabled("contract-review"));
    }

    [Fact]
    public async Task An_organisation_skill_has_no_switch_and_cannot_be_opened()
    {
        using var rig = new SkillRig(_ => Task.FromResult<IReadOnlyList<SkillOption>>(new[] { new SkillOption { Slug = "org-policy", Name = "Org", Description = "x" } }));
        var library = rig.Library();
        await library.RefreshAsync();
        var org = library.Rows.Single(r => r.Name == "org-policy");

        Assert.False(library.ToggleCommand.CanExecute(org));
        Assert.False(library.EditCommand.CanExecute(org));
        Assert.False(library.DuplicateCommand.CanExecute(org));
        Assert.False(library.ExportCommand.CanExecute(org));
        Assert.False(library.AskDeleteCommand.CanExecute(org));
    }

    [Fact]
    public async Task New_opens_an_empty_editor_and_nothing_is_written()
    {
        using var rig = new SkillRig();
        var library = rig.Library();

        library.NewCommand.Execute(null);

        Assert.NotNull(library.Editor);
        Assert.Equal(SkillEditorMode.New, library.Editor!.Mode);
        Assert.False(library.NewCommand.CanExecute(null)); // one editor at a time
        Assert.Equal(0, rig.Workshop.Saves);
        Assert.False(Directory.Exists(rig.Folder));
        library.Editor.CancelCommand.Execute(null);
        Assert.Null(library.Editor);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Edit_opens_an_own_skill_for_editing_and_a_bundled_one_read_only()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();
        await library.RefreshAsync();

        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));
        var own = library.Editor!;
        Assert.Equal((SkillEditorMode.Edit, "contract-review", false), (own.Mode, own.Name, own.IsReadOnly));
        Assert.Equal("Editing contract-review", own.Heading);
        own.CancelCommand.Execute(null);

        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "slide-design"));
        var bundled = library.Editor!;
        Assert.Equal((SkillEditorMode.View, true, "Use one idea per slide."), (bundled.Mode, bundled.IsReadOnly, bundled.Instructions));
        Assert.Equal("slide-design is read only.", bundled.Heading);
    }

    [Fact]
    public async Task Duplicate_opens_a_copy_under_a_free_name_and_saves_nothing()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc() with { Resources = new[] { SkillFile.FromText("references/a.md", "A") } });
        await rig.Workshop.SaveAsync(SkillRig.Doc("contract-review-copy"));
        var saves = rig.Workshop.Saves;
        var library = rig.Library();
        await library.RefreshAsync();

        await library.DuplicateCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));

        var editor = library.Editor!;
        Assert.Equal(("contract-review-copy-2", SkillEditorMode.New), (editor.Name, editor.Mode));
        Assert.Equal("Reviews a contract clause by clause.", editor.Description);
        Assert.Single(editor.Resources);
        Assert.Equal(saves, rig.Workshop.Saves);
        Assert.False(rig.OnDisk("contract-review-copy-2"));

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(rig.OnDisk("contract-review-copy-2"));
        Assert.Null(library.Editor); // saving closed the editor
        await Wait.Until(() => library.Rows.Any(r => r.Name == "contract-review-copy-2"));
        Assert.Equal("contract-review-copy-2 was saved.", library.Message);
    }

    [Fact]
    public async Task Delete_asks_first_and_deletes_only_after_the_person_confirms()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();
        await library.RefreshAsync();
        var own = library.Rows.Single(r => r.Name == "contract-review");

        library.AskDeleteCommand.Execute(own);
        Assert.Equal("Delete contract-review?", library.DeleteTitle);
        Assert.True(rig.OnDisk("contract-review"));
        Assert.Equal(0, rig.Workshop.Deletes);

        library.CancelDeleteCommand.Execute(null);
        Assert.Null(library.PendingDelete);
        Assert.True(rig.OnDisk("contract-review"));

        library.AskDeleteCommand.Execute(own);
        await library.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(rig.OnDisk("contract-review"));
        Assert.Null(library.PendingDelete);
        Assert.DoesNotContain(library.Rows, r => r.Name == "contract-review");
        Assert.Equal("contract-review was deleted.", library.Message);
    }

    [Fact]
    public async Task A_bundled_skill_cannot_be_asked_to_be_deleted()
    {
        using var rig = new SkillRig();
        var library = rig.Library();
        await library.RefreshAsync();

        Assert.False(library.AskDeleteCommand.CanExecute(library.Rows.Single(r => r.Name == "slide-design")));
    }

    [Fact]
    public async Task A_good_import_waits_for_the_person_and_writes_only_when_confirmed()
    {
        using var rig = new SkillRig();
        rig.Files.ImportPath = WriteZip(rig, "good.zip", ("contract-review/SKILL.md", GoodSkillMd));
        var library = rig.Library();

        await library.ImportCommand.ExecuteAsync(null);

        Assert.NotNull(library.PendingImport);
        Assert.True(library.CanConfirmImport);
        Assert.Equal("Import contract-review?", library.ImportTitle);
        Assert.Contains("not reviewed", library.ImportBody);
        Assert.False(rig.OnDisk("contract-review"));
        Assert.Equal(0, rig.Workshop.Commits);

        await library.ConfirmImportCommand.ExecuteAsync(null);

        Assert.True(rig.OnDisk("contract-review"));
        Assert.Null(library.PendingImport);
        Assert.Equal("contract-review was imported.", library.Message);
        Assert.Contains(library.Rows, r => r.Name == "contract-review");
    }

    [Fact]
    public async Task A_cancelled_import_writes_nothing()
    {
        using var rig = new SkillRig();
        rig.Files.ImportPath = WriteZip(rig, "good.zip", ("SKILL.md", GoodSkillMd));
        var library = rig.Library();
        await library.ImportCommand.ExecuteAsync(null);

        library.CancelImportCommand.Execute(null);

        Assert.Null(library.PendingImport);
        Assert.False(rig.OnDisk("contract-review"));
        Assert.Equal(0, rig.Workshop.Commits);
    }

    [Fact]
    public async Task An_import_over_an_existing_skill_says_it_replaces_and_does_so_only_on_Import()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc(description: "The old one."));
        rig.Files.ImportPath = WriteZip(rig, "good.zip", ("SKILL.md", GoodSkillMd));
        var library = rig.Library();

        await library.ImportCommand.ExecuteAsync(null);

        Assert.True(library.ImportReplaces);
        Assert.Equal("A skill named contract-review is already here. Importing replaces it.", library.ImportBody);
        Assert.Equal("The old one.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);

        await library.ConfirmImportCommand.ExecuteAsync(null);

        Assert.Equal("Reviews a contract clause by clause.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);
    }

    [Theory]
    [InlineData("../evil/SKILL.md", "pwned")]
    [InlineData("/abs/x.md", "pwned")]
    [InlineData("scripts/run.ps1", "Remove-Item *")]
    public async Task A_hostile_import_shows_why_and_cannot_be_confirmed(string name, string text)
    {
        using var rig = new SkillRig();
        rig.Files.ImportPath = WriteZip(rig, "hostile.zip", ("SKILL.md", GoodSkillMd), (name, text));
        var library = rig.Library();

        await library.ImportCommand.ExecuteAsync(null);

        Assert.NotNull(library.PendingImport);
        Assert.False(library.CanConfirmImport);
        Assert.False(library.ConfirmImportCommand.CanExecute(null));
        Assert.Contains(library.ImportIssues, i => i.IsError && i.Text.Length > 0);
        Assert.Equal("This cannot be imported. Nothing was written.", library.ImportBody);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.Equal(0, rig.Workshop.Commits);
    }

    [Fact]
    public async Task An_import_with_an_unknown_key_or_a_hidden_character_names_the_problem()
    {
        using var rig = new SkillRig();
        var hidden = char.ConvertFromUtf32(0x200B);
        rig.Files.ImportPath = WriteZip(rig, "keys.zip", ("SKILL.md", "---\nname: a-skill\ndescription: Does" + hidden + " it.\nversion: 2\n---\nBody\n"));
        var library = rig.Library();

        await library.ImportCommand.ExecuteAsync(null);

        var texts = library.ImportIssues.Select(i => i.Text).ToList();
        Assert.Contains(texts, t => t.Contains("version"));
        Assert.Contains(texts, t => t.Contains("U+200B"));
        Assert.False(library.CanConfirmImport);
    }

    [Fact]
    public async Task A_newer_import_makes_an_older_ones_answer_stale()
    {
        using var rig = new SkillRig();
        var first = WriteZip(rig, "first.zip", ("SKILL.md", GoodSkillMd));
        var second = WriteZip(rig, "second.zip", ("SKILL.md", GoodSkillMd.Replace("contract-review", "nda-review")));
        var gate = new TaskCompletionSource<bool>();
        rig.Workshop.HoldAnalysis = path => path == first ? gate.Task : Task.CompletedTask;
        var library = rig.Library();

        var older = library.ImportFromAsync(first);
        await library.ImportFromAsync(second);
        gate.SetResult(true);
        await older;

        Assert.Equal("nda-review", library.PendingImport!.Document!.Name);
    }

    [Fact]
    public async Task Export_gives_the_zip_to_the_file_host_and_says_so()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc() with { Resources = new[] { SkillFile.FromText("references/a.md", "A") } });
        var library = rig.Library();
        await library.RefreshAsync();

        await library.ExportCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));

        Assert.Equal("contract-review.zip", rig.Files.ExportedAs);
        using var archive = new ZipArchive(new MemoryStream(rig.Files.Exported!));
        Assert.Contains(archive.Entries, e => e.FullName == "contract-review/SKILL.md");
        Assert.Contains(archive.Entries, e => e.FullName == "contract-review/references/a.md");
        Assert.Equal("contract-review was exported.", library.Message);
    }

    [Fact]
    public async Task Without_a_file_host_import_and_export_are_off()
    {
        using var rig = new SkillRig();
        var library = new SkillLibraryViewModel(rig.Workshop, null, null, null, () => SkillRig.English);
        await library.RefreshAsync();

        Assert.False(library.ImportCommand.CanExecute(null));
        Assert.False(library.ExportCommand.CanExecute(library.Rows[0]));
    }

    private sealed class ThrowingWorkshop : ISkillWorkshop
    {
        public Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken cancellationToken = default) => throw new IOException("disk");

        public Task<SkillReadResult> ReadAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SkillCheck Check(SkillDocument document, string? folderName = null) => throw new NotSupportedException();

        public Task<SkillSaveResult> SaveAsync(SkillDocument document, string? previousName = null, bool replaceExisting = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImportAnalysis> AnalyzeImportAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImportAnalysis> AnalyzeZipAsync(byte[] zip, string source, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SkillSaveResult> CommitImportAsync(ImportAnalysis analysis, bool replaceExisting, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> ExportAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SkillExtensionParse ParseExtension(string text) => throw new NotSupportedException();

        public SkillTestResult Test(string prompt, SkillDocument? edited, string? editedFrom = null) => throw new NotSupportedException();
    }

    // -- the gaps the skin builders found: texts, message kind, broken rows, row button names, "Draft from this chat" ------------------

    private static UiStrings Fixed(string language) =>
        new(System.Globalization.CultureInfo.GetCultureInfo(language), null, System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task The_library_exposes_its_texts_and_says_so_when_the_language_changes()
    {
        using var rig = new SkillRig();
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));
        var changed = new List<string?>();
        library.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Skills on this device", library.Strings.SkillLibraryTitle);
        language = "de";
        library.Retext();

        Assert.Contains(nameof(SkillLibraryViewModel.Strings), changed);
        Assert.Equal(SkillRig.In("de").SkillLibraryTitle, library.Strings.SkillLibraryTitle);
        await library.RefreshAsync();
        Assert.IsAssignableFrom<ISkillTexts>(library);
    }

    [Fact]
    public async Task A_message_says_whether_it_is_information_or_an_error()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();
        await library.RefreshAsync();
        Assert.Equal("", library.Message);
        Assert.False(library.MessageIsError || library.MessageIsInformation || library.MessageIsWarning);

        // Information: what an action did.
        library.AskDeleteCommand.Execute(library.Rows.First(r => r.Name == "contract-review"));
        await library.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal("contract-review was deleted.", library.Message);
        Assert.Equal(SkillMessageKind.Information, library.MessageKind);
        Assert.True(library.MessageIsInformation);
        Assert.False(library.MessageIsError);

        // An error: the action did not happen (the skill is gone from the disk before Edit reads it).
        await rig.Workshop.SaveAsync(SkillRig.Doc("other-skill"));
        await library.RefreshAsync();
        var row = library.Rows.First(r => r.Name == "other-skill");
        Directory.Delete(Path.Combine(rig.Folder, "other-skill"), true);
        await library.EditCommand.ExecuteAsync(row);
        Assert.NotEqual("", library.Message);
        Assert.Equal(SkillMessageKind.Error, library.MessageKind);
        Assert.True(library.MessageIsError);
        Assert.False(library.MessageIsInformation);
    }

    [Fact]
    public async Task A_broken_skill_says_it_cannot_be_read_and_why()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        Directory.CreateDirectory(Path.Combine(rig.Folder, "bad-skill"));
        File.WriteAllText(Path.Combine(rig.Folder, "bad-skill", "SKILL.md"), "---\nname: bad-skill\ndescription: x\nversion: 3\n---\nBody.\n");
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));

        await library.RefreshAsync();

        var good = library.Rows.First(r => r.Name == "contract-review");
        var bad = library.Rows.First(r => r.Name == "bad-skill");
        Assert.True(bad.IsBroken);
        Assert.StartsWith("This skill cannot be read: ", bad.BrokenText);
        Assert.Contains("version", bad.BrokenText);
        Assert.Equal("", good.BrokenText);
        Assert.Equal("", library.Rows.First(r => r.Name == "slide-design").BrokenText);

        language = "de";
        library.Retext();
        Assert.StartsWith("Dieser Skill kann nicht gelesen werden: ", bad.BrokenText);
    }

    [Fact]
    public async Task A_broken_skill_without_a_known_reason_still_says_it_cannot_be_read()
    {
        var entry = new SkillEntry("odd", "", SkillOrigin.Own, true) { IsBroken = true };
        var row = new SkillRowViewModel(entry, () => SkillRig.English);

        Assert.Equal("This skill cannot be read. Open it to fix it, or delete it.", row.BrokenText);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_buttons_of_a_row_have_names_that_tell_the_rows_apart_in_every_language()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));
        await library.RefreshAsync();
        var row = library.Rows.First(r => r.Name == "contract-review");
        var bundled = library.Rows.First(r => r.Name == "slide-design");

        Assert.Equal(("Edit contract-review", "Delete contract-review", "Export contract-review", "Duplicate contract-review"),
            (row.EditLabel, row.DeleteLabel, row.ExportLabel, row.DuplicateLabel));
        Assert.Equal("View slide-design", bundled.ViewLabel);

        language = "fr";
        library.Retext();
        Assert.Equal("Modifier contract-review", row.EditLabel);
        Assert.Equal("Supprimer contract-review", row.DeleteLabel);
        language = "zh-Hans";
        library.Retext();
        Assert.Contains("contract-review", row.ExportLabel);
        Assert.Equal(5, new[] { row.EditLabel, row.DeleteLabel, row.ExportLabel, row.DuplicateLabel, bundled.ViewLabel }.Distinct().Count());
    }

    [Fact]
    public async Task Draft_from_this_chat_is_an_entry_only_when_the_host_gives_a_conversation()
    {
        using var rig = new SkillRig();
        var without = rig.Library();
        Assert.False(without.HasDraft);
        Assert.Null(without.Draft);
        Assert.False(without.DraftFromChatCommand.CanExecute(null));

        var chat = new[] { new SkillDraftMessage(true, "Please review this contract."), new SkillDraftMessage(false, "Clause 4 caps liability.") };
        var model = new ScriptedChat().Says("---\nname: contract-review\ndescription: Reviews contracts. Use when asked.\n---\n## Goal\nReview.\n");
        var library = new SkillLibraryViewModel(rig.Workshop, rig.Files, model, strings: () => SkillRig.English, draftSource: () => chat);

        Assert.True(library.HasDraft);
        Assert.NotNull(library.Draft);
        Assert.False(library.DraftOpen);
        Assert.True(library.DraftFromChatCommand.CanExecute(null));
        library.DraftFromChatCommand.Execute(null);
        Assert.True(library.DraftOpen);

        // The draft opens in the library's editor as a proposal; nothing is saved.
        await library.Draft!.DraftCommand.ExecuteAsync(null);
        Assert.NotNull(library.Editor);
        Assert.True(library.Editor!.IsProposal);
        Assert.False(library.DraftOpen);
        Assert.Equal(0, rig.Workshop.Saves);
        Assert.False(library.DraftFromChatCommand.CanExecute(null));

        // Open the proposal again while it is open: the same editor, once.
        library.Draft.OpenProposalCommand.Execute(null);
        Assert.Same(library.Draft.Proposal, library.Editor);
        library.Editor.CancelCommand.Execute(null);
        Assert.Null(library.Editor);
    }}

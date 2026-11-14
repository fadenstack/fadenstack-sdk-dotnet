using System.IO.Compression;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Tests;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The skill editor's three Avalonia views (sdk/51 F04): each draws every state of its view model in English and German without a binding
/// error, the buttons run the view model's commands, Enter and Esc and the focus work as the view model says, and nothing is saved except
/// by Save.
/// </summary>
public class SkillViewTests
{
    private const string GoodAnswer = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n\n## Goal\nReview the contract.\n";

    private const string GoodSkillMd = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n## Goal\nReview it.\n";

    private static readonly string[] Languages = { "en", "de" };

    private static TheoryData<string, string> States(params string[] states)
    {
        var data = new TheoryData<string, string>();
        foreach (var language in Languages)
        {
            foreach (var state in states)
            {
                data.Add(state, language);
            }
        }
        return data;
    }

    public static TheoryData<string, string> LibraryStates() =>
        States("list", "empty", "delete", "import", "import-replaces", "import-refused", "message", "editor-new", "editor-view");

    public static TheoryData<string, string> EditorStates() =>
        States("new", "edit", "view", "proposal", "errors", "replace", "tested", "asked", "files");

    public static TheoryData<string, string> DraftStates() =>
        States("idle", "working", "ready", "ready-with-problems", "failed", "unreadable", "no-chat", "no-model");

    private static string WriteZip(SkillRig rig, string file, string skillMd)
    {
        Directory.CreateDirectory(rig.Root);
        var path = Path.Combine(rig.Root, file);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var content = archive.CreateEntry("SKILL.md").Open();
            var bytes = Encoding.UTF8.GetBytes(skillMd);
            content.Write(bytes, 0, bytes.Length);
        }
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static object? Focused(SkillHost host) => host.Window.FocusManager?.GetFocusedElement();

    private static void Press(SkillHost host, PhysicalKey key) => host.Window.KeyPressQwerty(key, RawInputModifiers.None);

    // ---- the library -------------------------------------------------------------------------------------------------

    internal static async Task<SkillHost> LibraryAtAsync(string state, string language)
    {
        var host = await SkillHost.OpenLibraryAsync(language, own: state != "empty", bundled: state != "empty");
        var library = host.Library;
        switch (state)
        {
            case "delete":
                library.AskDeleteCommand.Execute(library.Rows.First(r => r.Name == "contract-review"));
                break;
            case "import":
            case "import-replaces":
            case "import-refused":
                if (state == "import")
                {
                    await host.Rig.Real.DeleteAsync("contract-review");
                }
                host.Rig.Files.ImportPath = WriteZip(host.Rig, state == "import-refused" ? "bad.zip" : "good.zip", state == "import-refused" ? "no front matter at all" : GoodSkillMd);
                await library.ImportCommand.ExecuteAsync(null);
                break;
            case "message":
                await library.ExportCommand.ExecuteAsync(library.Rows.First(r => r.Name == "contract-review"));
                break;
            case "editor-new":
                library.NewCommand.Execute(null);
                break;
            case "editor-view":
                await library.EditCommand.ExecuteAsync(library.Rows.First(r => r.Name == "slide-design"));
                break;
        }
        await host.PumpAsync();
        return host;
    }

    [AvaloniaTheory]
    [MemberData(nameof(LibraryStates))]
    public async Task Every_state_of_the_library_draws_in_both_languages_without_a_binding_error(string state, string language)
    {
        using var host = await LibraryAtAsync(state, language);
        var s = host.Strings;

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        switch (state)
        {
            case "list":
                Assert.Contains("contract-review", texts);
                Assert.Contains("slide-design", texts);
                Assert.Contains(s.SkillOriginBundled, texts);
                Assert.Contains(s.SkillOriginOwn, texts);
                Assert.Contains(s.SkillNotReviewed, texts);
                Assert.Contains(s.SkillLibraryTitle, texts);
                break;
            case "empty":
                Assert.Contains(s.SkillLibraryEmpty, texts);
                break;
            case "delete":
                Assert.Contains(s.SkillDeleteTitle("contract-review"), texts);
                Assert.Contains(s.SkillDeleteBody, texts);
                break;
            case "import":
                Assert.Contains(s.SkillImportTitle("contract-review"), texts);
                Assert.Contains(s.SkillImportBody, texts);
                break;
            case "import-replaces":
                Assert.Contains(s.SkillImportReplaceBody("contract-review"), texts);
                Assert.Contains(s.SkillReplace, texts);
                break;
            case "import-refused":
                Assert.Contains(s.SkillImportRefused, texts);
                Assert.False(host.Library.CanConfirmImport);
                Assert.NotEmpty(host.Library.ImportIssues);
                Assert.Contains(host.Library.ImportIssues[0].Text, texts);
                break;
            case "message":
                Assert.False(string.IsNullOrEmpty(host.Library.Message));
                Assert.Contains(host.Library.Message, texts);
                break;
            case "editor-new":
                Assert.Contains(s.SkillNew, texts);
                Assert.Contains(s.SkillName, texts);
                Assert.DoesNotContain(s.SkillLibraryTitle, texts);
                break;
            case "editor-view":
                Assert.Contains(s.SkillEditorReadOnly("slide-design"), texts);
                break;
        }
        Assert.DoesNotContain(SkillRig.In(language == "en" ? "de" : "en").SkillLibraryTitle, texts);
    }

    [AvaloniaFact]
    public async Task A_row_shows_what_the_person_may_do_with_the_skill()
    {
        using var host = await SkillHost.OpenLibraryAsync();
        var s = host.Strings;

        // The bundled skill: read it, copy it, export it; no delete. Yours: edit and delete.
        Assert.Single(host.ButtonsSaying(s.SkillView));
        Assert.Single(host.ButtonsSaying(s.SkillEdit));
        Assert.Single(host.ButtonsSaying(s.SkillDelete));
        Assert.Equal(2, host.ButtonsSaying(s.SkillDuplicate).Count());
        Assert.Equal(2, host.ButtonsSaying(s.SkillExport).Count());
        Assert.Equal(2, host.Visible().OfType<ToggleSwitch>().Count());
        Assert.Single(host.Visible().OfType<TextBlock>(), t => t.Text == s.SkillNotReviewed);
    }

    [AvaloniaFact]
    public async Task The_switch_runs_the_toggle_command()
    {
        using var host = await SkillHost.OpenLibraryAsync();
        var row = host.Library.Rows.First(r => r.Name == "contract-review");
        var before = row.IsEnabled;

        var toggle = host.Visible().OfType<ToggleSwitch>().First(t => ReferenceEquals(t.DataContext, row));
        toggle.Command!.Execute(toggle.CommandParameter);
        await Wait.Until(() => host.Library.Rows.First(r => r.Name == "contract-review").IsEnabled != before);
        await host.PumpAsync();

        Assert.NotEqual(before, host.Library.Rows.First(r => r.Name == "contract-review").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_Esc_says_no_and_only_Delete_deletes()
    {
        using var host = await SkillHost.OpenLibraryAsync();
        var s = host.Strings;

        var ask = host.ButtonSaying(s.SkillDelete);
        ask.Command!.Execute(ask.CommandParameter);
        await host.PumpAsync();
        Assert.NotNull(host.Library.PendingDelete);
        Assert.Equal(0, host.Rig.Workshop.Deletes);
        Assert.Contains(s.SkillDeleteTitle("contract-review"), host.Texts());
        // The safe answer has the focus.
        Assert.Same(host.ButtonSaying(s.SkillCancel), Focused(host));

        Press(host, PhysicalKey.Escape);
        await host.PumpAsync();
        Assert.Null(host.Library.PendingDelete);
        Assert.Equal(0, host.Rig.Workshop.Deletes);

        host.Library.AskDeleteCommand.Execute(host.Library.Rows.First(r => r.Name == "contract-review"));
        await host.PumpAsync();
        host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Library.ConfirmDeleteCommand)).Command!.Execute(null);
        await Wait.Until(() => host.Rig.Workshop.Deletes == 1);
        await host.PumpAsync();
        Assert.False(host.Rig.OnDisk("contract-review"));
    }

    [AvaloniaFact]
    public async Task Import_shows_what_it_found_and_writes_only_when_Enter_presses_Import()
    {
        using var host = await SkillHost.OpenLibraryAsync(own: false);
        host.Rig.Files.ImportPath = WriteZip(host.Rig, "good.zip", GoodSkillMd);

        host.ButtonSaying(host.Strings.SkillImport).Command!.Execute(null);
        await Wait.Until(() => host.Library.PendingImport is not null);
        await host.PumpAsync();
        Assert.True(host.Library.CanConfirmImport);
        Assert.Equal(0, host.Rig.Workshop.Commits);
        Assert.False(host.Rig.OnDisk("contract-review"));

        host.View.Focus(); // not on a button: Enter presses the question's primary button
        Press(host, PhysicalKey.Enter);
        await Wait.Until(() => host.Rig.Workshop.Commits == 1);
        await host.PumpAsync();
        Assert.True(host.Rig.OnDisk("contract-review"));
        Assert.Null(host.Library.PendingImport);
        Assert.Contains(host.Strings.SkillImported("contract-review"), host.Texts());
    }

    [AvaloniaFact]
    public async Task Esc_cancels_an_import_and_writes_nothing()
    {
        using var host = await SkillHost.OpenLibraryAsync(own: false);
        host.Rig.Files.ImportPath = WriteZip(host.Rig, "good.zip", GoodSkillMd);
        await host.Library.ImportCommand.ExecuteAsync(null);
        await host.PumpAsync();
        Assert.NotNull(host.Library.PendingImport);

        Press(host, PhysicalKey.Escape);
        await host.PumpAsync();

        Assert.Null(host.Library.PendingImport);
        Assert.Equal(0, host.Rig.Workshop.Commits);
    }

    [AvaloniaFact]
    public async Task Export_hands_the_zip_to_the_file_host_and_says_so()
    {
        using var host = await SkillHost.OpenLibraryAsync();
        var row = host.Library.Rows.First(r => r.Name == "contract-review");
        var button = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.CommandParameter, row) && b.Content as string == host.Strings.SkillExport);

        button.Command!.Execute(button.CommandParameter);
        await Wait.Until(() => host.Rig.Files.Exported is not null);
        await Wait.Until(() => host.Library.Message.Length > 0);
        await host.PumpAsync();

        Assert.Equal("contract-review.zip", host.Rig.Files.ExportedAs);
        Assert.Contains(host.Strings.SkillExported("contract-review"), host.Texts());
    }

    [AvaloniaFact]
    public async Task New_opens_the_editor_in_place_of_the_list_and_Esc_brings_the_list_back()
    {
        using var host = await SkillHost.OpenLibraryAsync();
        var s = host.Strings;

        host.ButtonSaying(s.SkillNew).Command!.Execute(null);
        await host.PumpAsync();
        Assert.NotNull(host.Library.Editor);
        Assert.DoesNotContain(s.SkillLibraryTitle, host.Texts());
        // The editor has the focus on its name.
        Assert.Same(host.Find<TextBox>("NameBox"), Focused(host));

        Press(host, PhysicalKey.Escape);
        await host.PumpAsync();
        Assert.Null(host.Library.Editor);
        Assert.Contains(s.SkillLibraryTitle, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task The_first_button_has_the_focus_when_the_library_opens()
    {
        using var host = await SkillHost.OpenLibraryAsync();

        Assert.Same(host.ButtonSaying(host.Strings.SkillNew), Focused(host));
    }

    // ---- the editor --------------------------------------------------------------------------------------------------

    internal static async Task<SkillHost> EditorAtAsync(string state, string language)
    {
        var mode = state is "edit" or "tested" or "asked" or "files" ? "edit" : state == "view" ? "view" : "new";
        var model = state == "asked" ? new ScriptedChat().Loads("contract-review") : null;
        var host = await SkillHost.OpenEditorAsync(language, mode, model);
        var editor = host.Editor;
        switch (state)
        {
            case "errors":
                editor.Name = "Bad Name";
                break;
            case "replace":
                await host.Rig.Real.SaveAsync(SkillRig.Doc("taken", "Another skill."));
                editor.Name = "taken";
                editor.Description = "Does a thing.";
                editor.Instructions = "## Goal\nDo it.";
                await editor.SaveCommand.ExecuteAsync(null);
                break;
            case "tested":
                editor.TestPrompt = "Please review this contract";
                editor.RunTest();
                break;
            case "asked":
                editor.TestPrompt = "Please review this contract";
                await editor.AskModelCommand.ExecuteAsync(null);
                break;
            case "files":
                editor.AddResource(new SkillFile("references/notes.md", Encoding.UTF8.GetBytes("# Notes")));
                break;
        }
        await host.PumpAsync();
        return host;
    }

    [AvaloniaTheory]
    [MemberData(nameof(EditorStates))]
    public async Task Every_state_of_the_editor_draws_in_both_languages_without_a_binding_error(string state, string language)
    {
        if (state == "proposal")
        {
            await ProposalDrawsAsync(language);
            return;
        }
        using var host = await EditorAtAsync(state, language);
        var s = host.Strings;

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        Assert.Contains(host.Editor.Heading, texts);
        Assert.Contains(s.SkillName, texts);
        Assert.Contains(s.SkillInstructions, texts);
        Assert.Contains(s.SkillTestHeading, texts);
        switch (state)
        {
            case "new":
                Assert.Contains(s.SkillNew, texts);
                Assert.Contains(s.SkillNoResources, texts);
                break;
            case "edit":
                Assert.Contains(s.SkillEditorEditing("contract-review"), texts);
                Assert.Contains(s.SkillChecksOk, texts);
                break;
            case "view":
                Assert.Contains(s.SkillEditorReadOnly("slide-design"), texts);
                Assert.Empty(host.ButtonsSaying(s.SkillSave));
                Assert.True(host.Find<TextBox>("NameBox").IsReadOnly);
                break;
            case "errors":
                Assert.True(host.Editor.HasErrors);
                Assert.Contains(host.Editor.Issues.First(i => i.Subject == "name").Text, texts);
                break;
            case "replace":
                Assert.True(host.Editor.NeedsReplaceConfirmation);
                Assert.Contains(s.SkillExistsTitle("taken"), texts);
                Assert.Contains(s.SkillExistsBody, texts);
                break;
            case "tested":
                Assert.Contains(s.SkillTestListed, texts);
                Assert.Contains(host.Editor.TestListed[0], texts);
                break;
            case "asked":
                Assert.Contains(s.SkillModelWouldLoad("contract-review"), texts);
                Assert.Contains(s.SkillModelNote, texts);
                break;
            case "files":
                Assert.Contains("references/notes.md", texts);
                Assert.Contains(s.SkillRenameFile, texts);
                Assert.Contains(s.SkillRemoveFile, texts);
                break;
        }
        Assert.DoesNotContain(SkillRig.In(language == "en" ? "de" : "en").SkillInstructions, texts);
    }

    private static async Task ProposalDrawsAsync(string language)
    {
        using var host = await SkillHost.OpenDraftAsync(language, new ScriptedChat().Says(GoodAnswer));
        await host.Draft.DraftCommand.ExecuteAsync(null);
        await host.PumpAsync();
        var view = new SkillEditorView { DataContext = host.Draft.Proposal };
        host.Window.Content = view;
        await host.PumpAsync();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        Assert.Empty(host.BindingErrors);
        Assert.Contains(host.Strings.SkillEditorProposal, texts);
        Assert.Contains(host.Strings.SkillNew, texts);
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task Name_and_description_checks_show_as_you_type_and_Save_follows_them()
    {
        using var host = await SkillHost.OpenEditorAsync();
        var save = host.ButtonSaying(host.Strings.SkillSave);
        Assert.False(save.IsEffectivelyEnabled);

        host.Find<TextBox>("NameBox").Text = "Bad Name";
        await host.PumpAsync();
        Assert.Equal("Bad Name", host.Editor.Name);
        Assert.Contains(host.Editor.Issues.First(i => i.Subject == "name").Text, host.Texts());

        host.Find<TextBox>("NameBox").Text = "good-name";
        host.Find<TextBox>("DescriptionBox").Text = "Does a thing. Use it when asked.";
        host.Find<TextBox>("InstructionsBox").Text = "## Goal\nDo it.";
        await host.PumpAsync();
        Assert.False(host.Editor.HasErrors);
        Assert.True(save.IsEffectivelyEnabled);
        Assert.Contains(host.Strings.SkillChecksOk, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task The_preview_shows_the_instructions_as_the_chat_draws_markdown()
    {
        using var host = await SkillHost.OpenEditorAsync();
        host.Editor.Instructions = "## Goal\nDo **it**.";
        var card = host.Find<Border>("PreviewCard");
        Assert.False(card.IsEffectivelyVisible);

        host.Find<CheckBox>("PreviewBox").IsChecked = true;
        await host.PumpAsync();

        Assert.True(host.Editor.ShowPreview);
        Assert.True(card.IsEffectivelyVisible);
        var markdown = card.GetVisualDescendants().OfType<MarkdownView>().Single();
        Assert.Equal(host.Editor.PreviewBlocks.Count, markdown.Children.Count);
        Assert.True(markdown.Children.Count >= 2);
    }

    [AvaloniaFact]
    public async Task The_files_are_added_renamed_and_removed_through_the_view_models_commands()
    {
        using var host = await SkillHost.OpenEditorAsync();
        host.Rig.Files.Resource = new SkillFile("references/a.md", Encoding.UTF8.GetBytes("a"));

        host.ButtonSaying(host.Strings.SkillAddFile).Command!.Execute(null);
        await Wait.Until(() => host.Editor.Resources.Count == 1);
        await host.PumpAsync();
        Assert.Contains("references/a.md", host.Texts());

        var rename = host.Visible().OfType<TextBox>().First(t => t.Text == "references/a.md");
        rename.Text = "references/b.md";
        await host.PumpAsync();
        host.ButtonSaying(host.Strings.SkillRenameFile).Command!.Execute(null);
        await host.PumpAsync();
        Assert.Equal("references/b.md", host.Editor.Resources[0].Path);

        host.ButtonSaying(host.Strings.SkillRemoveFile).Command!.Execute(null);
        await host.PumpAsync();
        Assert.Empty(host.Editor.Resources);
        Assert.Contains(host.Strings.SkillNoResources, host.Texts());
    }

    [AvaloniaFact]
    public async Task Enter_in_the_try_box_runs_the_test_and_the_lists_follow()
    {
        using var host = await SkillHost.OpenEditorAsync(mode: "edit");
        var box = host.Find<TextBox>("TestBox");
        box.Focus();
        box.Text = "Please review this contract";
        await host.PumpAsync();
        Assert.False(host.Editor.HasTestResult);

        Press(host, PhysicalKey.Enter);
        await host.PumpAsync();

        Assert.True(host.Editor.HasTestResult);
        Assert.Contains(host.Strings.SkillTestListed, host.Texts());
        Assert.Contains("contract-review", host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task Asking_the_model_is_offered_only_when_there_is_a_model_and_shows_its_answer()
    {
        using (var none = await SkillHost.OpenEditorAsync(mode: "edit"))
        {
            Assert.Empty(none.ButtonsSaying(none.Strings.SkillAskModel));
        }
        var model = new ScriptedChat().Loads("contract-review");
        using var host = await SkillHost.OpenEditorAsync(mode: "edit", model: model);
        host.Editor.TestPrompt = "Please review this contract";
        await host.PumpAsync();

        host.ButtonSaying(host.Strings.SkillAskModel).Command!.Execute(null);
        await Wait.Until(() => host.Editor.ModelAnswer == SkillModelAnswer.LoadsThis);
        await host.PumpAsync();

        Assert.Contains(host.Strings.SkillModelWouldLoad("contract-review"), host.Texts());
        Assert.Single(model.Calls);
    }

    [AvaloniaFact]
    public async Task Enter_in_the_name_saves_when_the_checks_pass_and_Esc_cancels_without_saving()
    {
        using var host = await SkillHost.OpenEditorAsync();
        SkillEditorClosedEventArgs? closed = null;
        host.Editor.Closed += (_, e) => closed = e;
        var name = host.Find<TextBox>("NameBox");
        Assert.Same(name, Focused(host));
        name.Text = "new-skill";
        host.Find<TextBox>("DescriptionBox").Text = "Does a thing. Use it when asked.";
        host.Find<TextBox>("InstructionsBox").Text = "## Goal\nDo it.";
        await host.PumpAsync();
        name.Focus();

        Press(host, PhysicalKey.Enter);
        await Wait.Until(() => closed is not null);
        await host.PumpAsync();

        Assert.True(closed!.Saved);
        Assert.Equal(1, host.Rig.Workshop.Saves);
        Assert.True(host.Rig.OnDisk("new-skill"));

        using var second = await SkillHost.OpenEditorAsync();
        SkillEditorClosedEventArgs? cancelled = null;
        second.Editor.Closed += (_, e) => cancelled = e;
        Press(second, PhysicalKey.Escape);
        await second.PumpAsync();
        Assert.False(cancelled!.Saved);
        Assert.Equal(0, second.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task Enter_does_not_save_while_a_check_fails_and_Esc_answers_the_replace_question_first()
    {
        using var host = await SkillHost.OpenEditorAsync();
        host.Find<TextBox>("NameBox").Focus();
        Press(host, PhysicalKey.Enter);
        await host.PumpAsync();
        Assert.Equal(0, host.Rig.Workshop.Saves);

        await host.Rig.Real.SaveAsync(SkillRig.Doc("taken", "Another skill."));
        host.Editor.Name = "taken";
        host.Editor.Description = "Does a thing.";
        host.Editor.Instructions = "## Goal\nDo it.";
        SkillEditorClosedEventArgs? closed = null;
        host.Editor.Closed += (_, e) => closed = e;
        await host.Editor.SaveCommand.ExecuteAsync(null);
        await host.PumpAsync();
        Assert.True(host.Editor.NeedsReplaceConfirmation);
        Assert.Same(host.ButtonSaying(host.Strings.SkillCancel), Focused(host));

        Press(host, PhysicalKey.Escape);
        await host.PumpAsync();

        Assert.False(host.Editor.NeedsReplaceConfirmation);
        Assert.Null(closed); // Esc answered the question; the editor is still open
    }

    // ---- the draft ---------------------------------------------------------------------------------------------------

    internal static async Task<SkillHost> DraftAtAsync(string state, string language)
    {
        IChatClient? model = state switch
        {
            "working" => new ScriptedChat().Then((_, _) => new TaskCompletionSource<ChatResponse>().Task),
            "ready" => new ScriptedChat().Says(GoodAnswer),
            "ready-with-problems" => new ScriptedChat().Says("---\nname: contract-review\ndescription: Reviews.\nversion: 2\n---\n\n## Goal\nDo it.\n"),
            "failed" => new ScriptedChat().Throws(),
            "unreadable" => new ScriptedChat().Says("   "),
            "idle" or "no-chat" => new ScriptedChat().Says(GoodAnswer),
            _ => null,
        };
        var host = await SkillHost.OpenDraftAsync(language, model, chat: state != "no-chat");
        if (state != "idle")
        {
            _ = host.Draft.DraftAsync();
            await host.PumpAsync();
            await Wait.Until(() => state == "working" ? host.Draft.State == SkillDraftState.Working : host.Draft.State != SkillDraftState.Working);
            await host.PumpAsync();
        }
        return host;
    }

    [AvaloniaTheory]
    [MemberData(nameof(DraftStates))]
    public async Task Every_state_of_the_draft_draws_in_both_languages_without_a_binding_error(string state, string language)
    {
        using var host = await DraftAtAsync(state, language);
        var s = host.Strings;

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        Assert.Contains(s.SkillDraftTitle, texts);
        Assert.Contains(s.SkillDraftGuidance, texts);
        Assert.Contains(s.SkillDraftStart, texts);
        var expected = state switch
        {
            "working" => SkillDraftState.Working,
            "ready" => SkillDraftState.Ready,
            "ready-with-problems" => SkillDraftState.ReadyWithProblems,
            "failed" => SkillDraftState.Failed,
            "unreadable" => SkillDraftState.Unreadable,
            "no-chat" => SkillDraftState.NoChat,
            "no-model" => SkillDraftState.NoModel,
            _ => SkillDraftState.Idle,
        };
        Assert.Equal(expected, host.Draft.State);
        if (expected == SkillDraftState.Idle)
        {
            Assert.DoesNotContain(s.SkillDraftOpen, texts);
        }
        else
        {
            Assert.Contains(host.Draft.StatusText, texts);
        }
        if (expected is SkillDraftState.Ready or SkillDraftState.ReadyWithProblems)
        {
            // The proposal is there, and nothing is saved.
            Assert.Contains(s.SkillEditorProposal, texts);
            Assert.Contains(s.SkillDraftOpen, texts);
            Assert.Equal(0, host.Rig.Workshop.Saves);
        }
        if (expected == SkillDraftState.Ready)
        {
            Assert.Contains(s.SkillDraftReady, texts); // "Nothing is saved until you press Save."
        }
        Assert.Equal(expected == SkillDraftState.Working, host.Visible().OfType<ProgressBar>().Any());
    }

    [AvaloniaFact]
    public async Task The_draft_has_the_focus_on_its_field_and_Enter_in_it_asks_for_a_draft()
    {
        using var host = await SkillHost.OpenDraftAsync(model: new ScriptedChat().Says(GoodAnswer));
        var box = host.Find<TextBox>("GuidanceBox");
        Assert.Same(box, Focused(host));
        box.Text = "For contracts";
        await host.PumpAsync();
        Assert.Equal("For contracts", host.Draft.Guidance);

        Press(host, PhysicalKey.Enter);
        await Wait.Until(() => host.Draft.State == SkillDraftState.Ready);
        await host.PumpAsync();

        Assert.Equal(SkillDraftState.Ready, host.Draft.State);
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    [AvaloniaFact]
    public async Task The_open_button_is_the_view_models_command_and_asks_for_the_proposal_in_the_editor()
    {
        using var host = await SkillHost.OpenDraftAsync(model: new ScriptedChat().Says(GoodAnswer));
        await host.Draft.DraftCommand.ExecuteAsync(null);
        await host.PumpAsync();
        SkillEditorViewModel? asked = null;
        host.Draft.ProposalReady += (_, editor) => asked = editor;

        var open = host.Find<Button>("OpenButton");
        Assert.Same(host.Draft.OpenProposalCommand, open.Command);
        open.Command!.Execute(null);
        await host.PumpAsync();

        Assert.Same(host.Draft.Proposal, asked);
        Assert.Equal(0, host.Rig.Workshop.Saves);
    }

    // ---- the gaps the skin builders found ----------------------------------------------------------------------------

    private static TextBlock MessageLine(SkillHost host, string banner) => host.Find<Border>(banner).GetVisualDescendants().OfType<TextBlock>().First();

    [AvaloniaFact]
    public async Task The_views_take_their_texts_from_the_view_model_and_follow_a_language_change()
    {
        var rig = new SkillRig();
        using var _ = rig;
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));
        var view = new SkillLibraryView { DataContext = library };

        Assert.Equal("en", ChatText.GetStrings(view)!.Language);
        language = "de";
        library.Retext();
        Assert.Equal("de", ChatText.GetStrings(view)!.Language);

        // A view without a view model says the current language's texts.
        view.DataContext = null;
        Assert.Null(ChatText.GetStrings(view));
        await Task.CompletedTask;
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task The_library_shows_information_as_a_status_and_an_error_as_an_alert(string language)
    {
        using var host = await LibraryAtAsync("list", language);
        var library = host.Library;
        var s = host.Strings;

        library.AskDeleteCommand.Execute(library.Rows.First(r => r.Name == "contract-review"));
        await library.ConfirmDeleteCommand.ExecuteAsync(null);
        await host.PumpAsync();

        Assert.Equal(SkillMessageKind.Information, library.MessageKind);
        Assert.True(host.Find<Border>("MessageInfo").IsEffectivelyVisible);
        Assert.False(host.Find<Border>("MessageError").IsEffectivelyVisible);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageInfo")));
        Assert.Contains(s.SkillDeleted("contract-review"), host.Texts());

        await host.Rig.Real.SaveAsync(SkillRig.Doc("other-skill"));
        await library.RefreshAsync();
        await Wait.Until(() => library.Rows.Any(r => r.Name == "other-skill"));
        var row = library.Rows.First(r => r.Name == "other-skill");
        Directory.Delete(Path.Combine(host.Rig.Folder, "other-skill"), true);
        await library.EditCommand.ExecuteAsync(row);
        await host.PumpAsync();

        Assert.Equal(SkillMessageKind.Error, library.MessageKind);
        Assert.True(host.Find<Border>("MessageError").IsEffectivelyVisible);
        Assert.False(host.Find<Border>("MessageInfo").IsEffectivelyVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageError")));
        Assert.Contains(library.Message, host.Texts());
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task A_broken_skill_says_it_cannot_be_read_and_why(string language)
    {
        using var host = await SkillHost.OpenLibraryAsync(language);
        Directory.CreateDirectory(Path.Combine(host.Rig.Folder, "bad-skill"));
        File.WriteAllText(Path.Combine(host.Rig.Folder, "bad-skill", "SKILL.md"), "---\nname: bad-skill\ndescription: x\nversion: 3\n---\nBody.\n");
        await host.Library.RefreshAsync();
        await Wait.Until(() => host.Library.Rows.Any(r => r.Name == "bad-skill"));
        await host.PumpAsync();

        var bad = host.Library.Rows.First(r => r.Name == "bad-skill");
        Assert.True(bad.IsBroken);
        Assert.Contains(bad.BrokenText, host.Texts());
        Assert.StartsWith(host.Strings.SkillBroken("").Split(':')[0], bad.BrokenText);
        Assert.Single(host.Texts(), t => t == bad.BrokenText);
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task The_buttons_of_a_row_are_named_for_the_row(string language)
    {
        using var host = await SkillHost.OpenLibraryAsync(language);
        var s = host.Strings;
        var own = host.Library.Rows.First(r => r.Name == "contract-review");
        var bundled = host.Library.Rows.First(r => r.Name == "slide-design");

        Button Of(SkillRowViewModel row, string text) => host.Visible().OfType<Button>().First(b => ReferenceEquals(b.CommandParameter, row) && b.Content as string == text);

        Assert.Equal(s.SkillEditNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillEdit)));
        Assert.Equal(s.SkillDuplicateNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillDuplicate)));
        Assert.Equal(s.SkillExportNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillExport)));
        Assert.Equal(s.SkillDeleteNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillDelete)));
        Assert.Equal(s.SkillViewNamed("slide-design"), AutomationProperties.GetName(Of(bundled, s.SkillView)));
    }

    [AvaloniaFact]
    public async Task Draft_from_this_chat_is_an_entry_of_the_library_only_when_the_host_gave_a_conversation()
    {
        using (var without = await SkillHost.OpenLibraryAsync())
        {
            Assert.Empty(without.ButtonsSaying(without.Strings.SkillDraftTitle));
            Assert.DoesNotContain(without.Strings.SkillDraftNothingSaved, without.Texts());
        }

        using var host = await SkillHost.OpenLibraryAsync(model: new ScriptedChat().Says(GoodAnswer), draft: true);
        var s = host.Strings;
        Assert.DoesNotContain(s.SkillDraftNothingSaved, host.Texts());

        host.ButtonSaying(s.SkillDraftTitle).Command!.Execute(null);
        await host.PumpAsync();

        Assert.True(host.Library.DraftOpen);
        Assert.Contains(s.SkillDraftNothingSaved, host.Texts());
        Assert.Contains(s.SkillDraftStart, host.Texts());

        await host.Library.Draft!.DraftCommand.ExecuteAsync(null);
        await host.PumpAsync();

        Assert.NotNull(host.Library.Editor);
        Assert.True(host.Library.Editor!.IsProposal);
        Assert.Contains(s.SkillEditorProposal, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task The_editor_counts_the_instructions_tokens_and_warns_when_over(string language)
    {
        using var host = await SkillHost.OpenEditorAsync(language, "edit");

        host.Editor.Instructions = new string('w', 1200 * 4);
        await host.PumpAsync();
        Assert.Equal(host.Strings.SkillTokens(1200, 5000), host.Editor.InstructionsTokensText);
        Assert.True(host.Find<TextBlock>("TokensText").IsEffectivelyVisible);
        Assert.False(host.Find<Border>("TokensWarning").IsEffectivelyVisible);
        Assert.Contains(host.Editor.InstructionsTokensText, host.Texts());

        host.Editor.Instructions = new string('w', 5001 * 4);
        await host.PumpAsync();
        Assert.False(host.Find<TextBlock>("TokensText").IsEffectivelyVisible);
        Assert.True(host.Find<Border>("TokensWarning").IsEffectivelyVisible);
        Assert.Contains(host.Strings.SkillTokens(5001, 5000), host.Texts());
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaFact]
    public async Task The_try_panel_has_a_heading_for_the_matched_list()
    {
        using var host = await SkillHost.OpenEditorAsync(mode: "edit");
        Assert.DoesNotContain(host.Strings.SkillTestMatchesHeading, host.Texts());

        host.Editor.KeywordsText = "contract";
        host.Editor.TestPrompt = "Please review this contract";
        host.Editor.RunTest();
        await host.PumpAsync();

        Assert.NotEmpty(host.Editor.TestMatches);
        Assert.Contains(host.Strings.SkillTestMatchesHeading, host.Texts());
    }

    [AvaloniaFact]
    public async Task The_editor_shows_an_error_as_an_alert_and_information_as_a_status()
    {
        using var host = await SkillHost.OpenEditorAsync();
        Directory.CreateDirectory(host.Rig.Root);
        File.WriteAllText(host.Rig.Folder, "not a folder");
        host.Editor.Name = "contract-review";
        host.Editor.Description = "Reviews a contract.";
        host.Editor.Instructions = "## Goal\nReview.\n";

        await host.Editor.SaveCommand.ExecuteAsync(null);
        await host.PumpAsync();

        Assert.Equal(SkillMessageKind.Error, host.Editor.MessageKind);
        Assert.True(host.Find<Border>("MessageError").IsEffectivelyVisible);
        Assert.False(host.Find<Border>("MessageInfo").IsEffectivelyVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageError")));
        Assert.Contains(host.Strings.SkillSaveFailed, host.Texts());
        Assert.Empty(host.BindingErrors);
    }

    [AvaloniaTheory]
    [MemberData(nameof(DraftStates))]
    public async Task Nothing_is_saved_until_Save_is_said_in_every_state_of_the_draft_view(string state, string language)
    {
        using var host = await DraftAtAsync(state, language);

        Assert.True(host.Find<TextBlock>("StandingNote").IsEffectivelyVisible);
        Assert.Contains(host.Strings.SkillDraftNothingSaved, host.Texts());
        if (host.Draft.HasProposal)
        {
            var open = host.Find<Button>("OpenButton");
            Assert.Same(host.Draft.OpenProposalCommand, open.Command);
            Assert.Equal(host.Strings.SkillDraftOpen, AutomationProperties.GetName(open));
        }
    }}

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wpf.Views;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The skill editor's three WPF views (sdk/51 F04): each draws every state of its view model in English and German without a binding
/// error, every input and button on screen has a name for screen readers, the buttons run the view model's commands, Enter and Esc and
/// the focus work as the view model says, and nothing is saved except by Save.
/// </summary>
public class SkillViewTests
{
    private const string GoodAnswer = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n\n## Goal\nReview the contract.\n";

    private const string GoodSkillMd = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n## Goal\nReview it.\n";

    private static TheoryData<string, string> States(params string[] states)
    {
        var data = new TheoryData<string, string>();
        foreach (var language in new[] { "en", "de" })
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

    private static List<string> Unnamed(SkillHost host, string where)
    {
        var failures = new List<string>();
        foreach (var element in host.Controls())
        {
            var what = $"{where}: {element.GetType().Name} {element.Name}";
            var name = AutomationProperties.GetName(element);
            if (string.IsNullOrWhiteSpace(name))
            {
                failures.Add(what + " has no automation name");
            }
            else if (element is Button { Content: string shown } && name.IndexOf(shown, StringComparison.OrdinalIgnoreCase) < 0)
            {
                // The name may say more than the button does ("Edit contract-review") but must contain what the button says (WCAG 2.5.3).
                failures.Add(what + $" is named '{name}' but says '{shown}'");
            }
        }
        return failures;
    }

    // ---- the library -------------------------------------------------------------------------------------------------

    private static SkillHost LibraryAt(string state, string language)
    {
        var host = SkillHost.OpenLibrary(language, own: state != "empty", bundled: state != "empty");
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
                    host.Await(host.Rig.Real.DeleteAsync("contract-review"));
                }
                host.Rig.Files.ImportPath = WriteZip(host.Rig, state == "import-refused" ? "bad.zip" : "good.zip", state == "import-refused" ? "no front matter at all" : GoodSkillMd);
                host.Await(library.ImportCommand.ExecuteAsync(null));
                break;
            case "message":
                host.Await(library.ExportCommand.ExecuteAsync(library.Rows.First(r => r.Name == "contract-review")));
                break;
            case "editor-new":
                library.NewCommand.Execute(null);
                break;
            case "editor-view":
                host.Await(library.EditCommand.ExecuteAsync(library.Rows.First(r => r.Name == "slide-design")));
                break;
        }
        host.Pump();
        return host;
    }

    [Theory]
    [MemberData(nameof(LibraryStates))]
    public void Every_state_of_the_library_draws_in_both_languages_without_a_binding_error(string state, string language) => Sta.Run(() =>
    {
        using var host = LibraryAt(state, language);
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
        var failures = Unnamed(host, $"library/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    [Fact]
    public void A_row_shows_what_the_person_may_do_with_the_skill() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();
        var s = host.Strings;

        // The bundled skill: read it, copy it, export it; no delete. Yours: edit and delete.
        Assert.Single(host.ButtonsSaying(s.SkillView));
        Assert.Single(host.ButtonsSaying(s.SkillEdit));
        Assert.Single(host.ButtonsSaying(s.SkillDelete));
        Assert.Equal(2, host.ButtonsSaying(s.SkillDuplicate).Count());
        Assert.Equal(2, host.ButtonsSaying(s.SkillExport).Count());
        Assert.Equal(2, host.Visible().OfType<CheckBox>().Count());
        Assert.Single(host.Visible().OfType<TextBlock>(), t => t.Text == s.SkillNotReviewed);
        var cards = host.Visible().OfType<Border>().Where(b => b.DataContext is SkillRowViewModel && ReferenceEquals(b.Style, host.View.FindResource("Skill.Card"))).ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, card => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(card))));
    });

    [Fact]
    public void The_switch_runs_the_toggle_command() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();
        var before = host.Library.Rows.First(r => r.Name == "contract-review").IsEnabled;

        var toggle = host.Visible().OfType<CheckBox>().First(t => AutomationProperties.GetName(t) == "contract-review");
        toggle.Command!.Execute(toggle.CommandParameter);
        host.WaitUntil(() => host.Library.Rows.First(r => r.Name == "contract-review").IsEnabled != before);

        Assert.NotEqual(before, host.Library.Rows.First(r => r.Name == "contract-review").IsEnabled);
    });

    [Fact]
    public void Delete_asks_first_Esc_says_no_and_only_Delete_deletes() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();
        var s = host.Strings;

        var ask = host.ButtonSaying(s.SkillDelete);
        ask.Command!.Execute(ask.CommandParameter);
        host.Pump();
        Assert.NotNull(host.Library.PendingDelete);
        Assert.Equal(0, host.Rig.Workshop.Deletes);
        Assert.Contains(s.SkillDeleteTitle("contract-review"), host.Texts());
        Assert.Same(host.ButtonSaying(s.SkillCancel), host.Focused()); // the safe answer has the focus

        host.Press(host.View, Key.Escape);
        Assert.Null(host.Library.PendingDelete);
        Assert.Equal(0, host.Rig.Workshop.Deletes);

        host.Library.AskDeleteCommand.Execute(host.Library.Rows.First(r => r.Name == "contract-review"));
        host.Pump();
        host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Library.ConfirmDeleteCommand)).Command!.Execute(null);
        host.WaitUntil(() => host.Rig.Workshop.Deletes == 1);
        Assert.False(host.Rig.OnDisk("contract-review"));
    });

    [Fact]
    public void Import_shows_what_it_found_and_writes_only_when_Enter_presses_Import() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary(own: false);
        host.Rig.Files.ImportPath = WriteZip(host.Rig, "good.zip", GoodSkillMd);

        host.ButtonSaying(host.Strings.SkillImport).Command!.Execute(null);
        host.WaitUntil(() => host.Library.PendingImport is not null);
        Assert.True(host.Library.CanConfirmImport);
        Assert.Equal(0, host.Rig.Workshop.Commits);
        Assert.False(host.Rig.OnDisk("contract-review"));

        host.Press(host.View, Key.Enter);
        host.WaitUntil(() => host.Rig.Workshop.Commits == 1);
        host.WaitUntil(() => host.Rig.OnDisk("contract-review"));
        Assert.True(host.Rig.OnDisk("contract-review"));
        host.WaitUntil(() => host.Library.PendingImport is null);
        Assert.Null(host.Library.PendingImport);
        host.WaitUntil(() => host.Library.Message.Length > 0);
        Assert.Contains(host.Strings.SkillImported("contract-review"), host.Texts());
    });

    [Fact]
    public void Esc_cancels_an_import_and_writes_nothing() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary(own: false);
        host.Rig.Files.ImportPath = WriteZip(host.Rig, "good.zip", GoodSkillMd);
        host.Await(host.Library.ImportCommand.ExecuteAsync(null));
        Assert.NotNull(host.Library.PendingImport);

        host.Press(host.View, Key.Escape);

        Assert.Null(host.Library.PendingImport);
        Assert.Equal(0, host.Rig.Workshop.Commits);
    });

    [Fact]
    public void Export_hands_the_zip_to_the_file_host_and_says_so() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();
        var row = host.Library.Rows.First(r => r.Name == "contract-review");
        var button = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.CommandParameter, row) && b.Content as string == host.Strings.SkillExport);

        button.Command!.Execute(button.CommandParameter);
        host.WaitUntil(() => host.Rig.Files.Exported is not null);
        host.WaitUntil(() => host.Library.Message.Length > 0);

        Assert.Equal("contract-review.zip", host.Rig.Files.ExportedAs);
        Assert.Contains(host.Strings.SkillExported("contract-review"), host.Texts());
    });

    [Fact]
    public void New_opens_the_editor_in_place_of_the_list_and_Esc_brings_the_list_back() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();
        var s = host.Strings;

        host.ButtonSaying(s.SkillNew).Command!.Execute(null);
        host.Pump();
        Assert.NotNull(host.Library.Editor);
        Assert.DoesNotContain(s.SkillLibraryTitle, host.Texts());
        Assert.Same(host.Find<TextBox>("NameBox"), host.Focused()); // the editor has the focus on its name

        host.Press(host.Find<TextBox>("NameBox"), Key.Escape);
        Assert.Null(host.Library.Editor);
        Assert.Contains(s.SkillLibraryTitle, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    });

    [Fact]
    public void The_first_button_has_the_focus_when_the_library_opens() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary();

        Assert.Same(host.ButtonSaying(host.Strings.SkillNew), host.Focused());
    });

    // ---- the editor --------------------------------------------------------------------------------------------------

    private static SkillHost EditorAt(string state, string language)
    {
        var mode = state is "edit" or "tested" or "asked" or "files" ? "edit" : state == "view" ? "view" : "new";
        var model = state == "asked" ? new ScriptedChat().Loads("contract-review") : null;
        var host = SkillHost.OpenEditor(language, mode, model);
        var editor = host.Editor;
        switch (state)
        {
            case "errors":
                editor.Name = "Bad Name";
                break;
            case "replace":
                host.Await(host.Rig.Real.SaveAsync(SkillRig.Doc("taken", "Another skill.")));
                editor.Name = "taken";
                editor.Description = "Does a thing.";
                editor.Instructions = "## Goal\nDo it.";
                host.Await(editor.SaveCommand.ExecuteAsync(null));
                break;
            case "tested":
                editor.TestPrompt = "Please review this contract";
                editor.RunTest();
                break;
            case "asked":
                editor.TestPrompt = "Please review this contract";
                host.Await(editor.AskModelCommand.ExecuteAsync(null));
                break;
            case "files":
                editor.AddResource(new SkillFile("references/notes.md", Encoding.UTF8.GetBytes("# Notes")));
                break;
        }
        host.Pump();
        return host;
    }

    [Theory]
    [MemberData(nameof(EditorStates))]
    public void Every_state_of_the_editor_draws_in_both_languages_without_a_binding_error(string state, string language) => Sta.Run(() =>
    {
        if (state == "proposal")
        {
            ProposalDraws(language);
            return;
        }
        using var host = EditorAt(state, language);
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
        var failures = Unnamed(host, $"editor/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    private static void ProposalDraws(string language)
    {
        using var host = SkillHost.OpenDraft(language, new ScriptedChat().Says(GoodAnswer));
        host.Await(host.Draft.DraftCommand.ExecuteAsync(null));
        var view = new SkillEditorView { DataContext = host.Draft.Proposal };
        host.Window.Content = view;
        host.Pump();

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        Assert.Contains(host.Strings.SkillEditorProposal, texts);
        Assert.Contains(host.Strings.SkillNew, texts);
        Assert.Equal(0, host.Rig.Workshop.Saves);
        Assert.True(Unnamed(host, "proposal").Count == 0);
    }

    [Fact]
    public void Name_and_description_checks_show_as_you_type_and_Save_follows_them() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        var save = host.ButtonSaying(host.Strings.SkillSave);
        Assert.False(save.IsEnabled);

        host.Find<TextBox>("NameBox").Text = "Bad Name"; // bound as it is typed
        host.Pump();
        Assert.Equal("Bad Name", host.Editor.Name);
        Assert.Contains(host.Editor.Issues.First(i => i.Subject == "name").Text, host.Texts());

        host.Find<TextBox>("NameBox").Text = "good-name";
        host.Find<TextBox>("DescriptionBox").Text = "Does a thing. Use it when asked.";
        host.Find<TextBox>("InstructionsBox").Text = "## Goal\nDo it.";
        host.Pump();
        Assert.False(host.Editor.HasErrors);
        Assert.True(save.IsEnabled);
        Assert.Contains(host.Strings.SkillChecksOk, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    });

    [Fact]
    public void The_preview_shows_the_instructions_as_the_chat_draws_markdown() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        host.Editor.Instructions = "## Goal\nDo **it**.";
        var card = host.Find<Border>("PreviewCard");
        Assert.False(card.IsVisible);

        host.Find<CheckBox>("PreviewBox").IsChecked = true;
        host.Pump();

        Assert.True(host.Editor.ShowPreview);
        Assert.True(card.IsVisible);
        Assert.Equal(host.Editor.PreviewBlocks.Count, host.Find<ItemsControl>("PreviewBlocks").Items.Count);
        Assert.True(host.Editor.PreviewBlocks.Count >= 2);
        Assert.Contains(host.Visible().OfType<TextBlock>(), t => t.Text == "Goal" || t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Goal"));
    });

    [Fact]
    public void The_files_are_added_renamed_and_removed_through_the_view_models_commands() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        host.Rig.Files.Resource = new SkillFile("references/a.md", Encoding.UTF8.GetBytes("a"));

        host.ButtonSaying(host.Strings.SkillAddFile).Command!.Execute(null);
        host.WaitUntil(() => host.Editor.Resources.Count == 1);
        Assert.Contains("references/a.md", host.Texts());

        var rename = host.Visible().OfType<TextBox>().First(t => t.Text == "references/a.md");
        rename.Text = "references/b.md";
        host.Pump();
        host.ButtonSaying(host.Strings.SkillRenameFile).Command!.Execute(null);
        host.Pump();
        Assert.Equal("references/b.md", host.Editor.Resources[0].Path);

        host.ButtonSaying(host.Strings.SkillRemoveFile).Command!.Execute(null);
        host.Pump();
        Assert.Empty(host.Editor.Resources);
        Assert.Contains(host.Strings.SkillNoResources, host.Texts());
    });

    [Fact]
    public void Enter_in_the_try_box_runs_the_test_and_the_lists_follow() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor(mode: "edit");
        var box = host.Find<TextBox>("TestBox");
        box.Text = "Please review this contract";
        host.Pump();
        Assert.False(host.Editor.HasTestResult);

        host.Press(box, Key.Enter);

        Assert.True(host.Editor.HasTestResult);
        Assert.Contains(host.Strings.SkillTestListed, host.Texts());
        Assert.Contains("contract-review", host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
    });

    [Fact]
    public void Asking_the_model_is_offered_only_when_there_is_a_model_and_shows_its_answer() => Sta.Run(() =>
    {
        using (var none = SkillHost.OpenEditor(mode: "edit"))
        {
            Assert.Empty(none.ButtonsSaying(none.Strings.SkillAskModel));
        }
        var model = new ScriptedChat().Loads("contract-review");
        using var host = SkillHost.OpenEditor(mode: "edit", model: model);
        host.Editor.TestPrompt = "Please review this contract";
        host.Pump();

        host.ButtonSaying(host.Strings.SkillAskModel).Command!.Execute(null);
        host.WaitUntil(() => host.Editor.ModelAnswer == SkillModelAnswer.LoadsThis);

        Assert.Contains(host.Strings.SkillModelWouldLoad("contract-review"), host.Texts());
        Assert.Single(model.Calls);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(host.Find<TextBlock>("ModelAnswerText")));
    });

    [Fact]
    public void Enter_in_the_name_saves_when_the_checks_pass_and_Esc_cancels_without_saving() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        SkillEditorClosedEventArgs? closed = null;
        host.Editor.Closed += (_, e) => closed = e;
        var name = host.Find<TextBox>("NameBox");
        Assert.Same(name, host.Focused());
        name.Text = "new-skill";
        host.Find<TextBox>("DescriptionBox").Text = "Does a thing. Use it when asked.";
        host.Find<TextBox>("InstructionsBox").Text = "## Goal\nDo it.";
        host.Pump();

        host.Press(name, Key.Enter);
        host.WaitUntil(() => closed is not null);

        Assert.True(closed!.Saved);
        Assert.Equal(1, host.Rig.Workshop.Saves);
        Assert.True(host.Rig.OnDisk("new-skill"));

        using var second = SkillHost.OpenEditor();
        SkillEditorClosedEventArgs? cancelled = null;
        second.Editor.Closed += (_, e) => cancelled = e;
        second.Press(second.Find<TextBox>("NameBox"), Key.Escape);
        Assert.False(cancelled!.Saved);
        Assert.Equal(0, second.Rig.Workshop.Saves);
    });

    [Fact]
    public void Enter_does_not_save_while_a_check_fails_and_Esc_answers_the_replace_question_first() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        host.Press(host.Find<TextBox>("NameBox"), Key.Enter);
        Assert.Equal(0, host.Rig.Workshop.Saves);

        host.Await(host.Rig.Real.SaveAsync(SkillRig.Doc("taken", "Another skill.")));
        host.Editor.Name = "taken";
        host.Editor.Description = "Does a thing.";
        host.Editor.Instructions = "## Goal\nDo it.";
        SkillEditorClosedEventArgs? closed = null;
        host.Editor.Closed += (_, e) => closed = e;
        host.Await(host.Editor.SaveCommand.ExecuteAsync(null));
        Assert.True(host.Editor.NeedsReplaceConfirmation);
        Assert.Same(host.ButtonSaying(host.Strings.SkillCancel), host.Focused());

        host.Press(host.View, Key.Escape);

        Assert.False(host.Editor.NeedsReplaceConfirmation);
        Assert.Null(closed); // Esc answered the question; the editor is still open
    });

    // ---- the draft ---------------------------------------------------------------------------------------------------

    private static SkillHost DraftAt(string state, string language)
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
        var host = SkillHost.OpenDraft(language, model, chat: state != "no-chat");
        if (state != "idle")
        {
            _ = host.Draft.DraftAsync();
            host.WaitUntil(() => state == "working" ? host.Draft.State == SkillDraftState.Working : host.Draft.State != SkillDraftState.Working);
        }
        return host;
    }

    [Theory]
    [MemberData(nameof(DraftStates))]
    public void Every_state_of_the_draft_draws_in_both_languages_without_a_binding_error(string state, string language) => Sta.Run(() =>
    {
        using var host = DraftAt(state, language);
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
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(host.Find<TextBlock>("ReadyText")));
        }
        if (expected == SkillDraftState.Failed)
        {
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(host.Find<TextBlock>("ErrorText")));
        }
        Assert.Equal(expected == SkillDraftState.Working, host.Visible().OfType<ProgressBar>().Any());
        Assert.All(host.Visible().OfType<ProgressBar>(), bar => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar))));
        var failures = Unnamed(host, $"draft/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    [Fact]
    public void The_draft_has_the_focus_on_its_field_and_Enter_in_it_asks_for_a_draft() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenDraft(model: new ScriptedChat().Says(GoodAnswer));
        var box = host.Find<TextBox>("GuidanceBox");
        Assert.Same(box, host.Focused());
        box.Text = "For contracts";
        host.Pump();
        Assert.Equal("For contracts", host.Draft.Guidance);

        host.Press(box, Key.Enter);
        host.WaitUntil(() => host.Draft.State == SkillDraftState.Ready);

        Assert.Equal(SkillDraftState.Ready, host.Draft.State);
        Assert.Equal(0, host.Rig.Workshop.Saves);
    });

    [Fact]
    public void The_open_button_is_the_view_models_command_and_asks_for_the_proposal_in_the_editor() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenDraft(model: new ScriptedChat().Says(GoodAnswer));
        host.Await(host.Draft.DraftCommand.ExecuteAsync(null));
        SkillEditorViewModel? asked = null;
        host.Draft.ProposalReady += (_, editor) => asked = editor;

        var open = host.Find<Button>("OpenButton");
        Assert.Same(host.Draft.OpenProposalCommand, open.Command);
        open.Command!.Execute(null);
        host.Pump();

        Assert.Same(host.Draft.Proposal, asked);
        Assert.Equal(0, host.Rig.Workshop.Saves);
    });

    // ---- the gaps the skin builders found ----------------------------------------------------------------------------

    private static TextBlock MessageLine(SkillHost host, string banner) => (TextBlock)host.Find<Border>(banner).Child;

    [Fact]
    public void The_views_take_their_texts_from_the_view_model_and_follow_a_language_change() => Sta.Run(() =>
    {
        using var rig = new SkillRig();
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
    });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void The_library_shows_information_as_a_status_and_an_error_as_an_alert(string language) => Sta.Run(() =>
    {
        using var host = LibraryAt("list", language);
        var library = host.Library;
        var s = host.Strings;

        library.AskDeleteCommand.Execute(library.Rows.First(r => r.Name == "contract-review"));
        host.Await(library.ConfirmDeleteCommand.ExecuteAsync(null));
        host.WaitUntil(() => library.Message.Length > 0);
        host.Pump();

        Assert.Equal(SkillMessageKind.Information, library.MessageKind);
        Assert.True(host.Find<Border>("MessageInfo").IsVisible);
        Assert.False(host.Find<Border>("MessageError").IsVisible);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageInfo")));
        Assert.Contains(s.SkillDeleted("contract-review"), host.Texts());

        host.Await(host.Rig.Real.SaveAsync(SkillRig.Doc("other-skill")));
        host.Await(library.RefreshAsync());
        host.WaitUntil(() => library.Rows.Any(r => r.Name == "other-skill"));
        var row = library.Rows.First(r => r.Name == "other-skill");
        Directory.Delete(Path.Combine(host.Rig.Folder, "other-skill"), true);
        host.Await(library.EditCommand.ExecuteAsync(row));
        host.WaitUntil(() => library.MessageKind == SkillMessageKind.Error);
        host.Pump();

        Assert.Equal(SkillMessageKind.Error, library.MessageKind);
        Assert.True(host.Find<Border>("MessageError").IsVisible);
        Assert.False(host.Find<Border>("MessageInfo").IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageError")));
        Assert.Contains(library.Message, host.Texts());
        Assert.Empty(host.BindingErrors);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void A_broken_skill_says_it_cannot_be_read_and_why(string language) => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary(language);
        Directory.CreateDirectory(Path.Combine(host.Rig.Folder, "bad-skill"));
        File.WriteAllText(Path.Combine(host.Rig.Folder, "bad-skill", "SKILL.md"), "---\nname: bad-skill\ndescription: x\nversion: 3\n---\nBody.\n");
        host.Await(host.Library.RefreshAsync());
        host.WaitUntil(() => host.Library.Rows.Any(r => r.Name == "bad-skill"));
        host.Pump();

        var bad = host.Library.Rows.First(r => r.Name == "bad-skill");
        Assert.True(bad.IsBroken);
        Assert.Contains(bad.BrokenText, host.Texts());
        Assert.StartsWith(host.Strings.SkillBroken("").Split(':')[0], bad.BrokenText);
        Assert.Single(host.Texts(), t => t == bad.BrokenText);
        Assert.Empty(host.BindingErrors);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void The_buttons_of_a_row_are_named_for_the_row(string language) => Sta.Run(() =>
    {
        using var host = SkillHost.OpenLibrary(language);
        var s = host.Strings;
        var own = host.Library.Rows.First(r => r.Name == "contract-review");
        var bundled = host.Library.Rows.First(r => r.Name == "slide-design");

        Button Of(SkillRowViewModel row, string text) => host.Visible().OfType<Button>().First(b => ReferenceEquals(b.CommandParameter, row) && b.Content as string == text);

        Assert.Equal(s.SkillEditNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillEdit)));
        Assert.Equal(s.SkillDuplicateNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillDuplicate)));
        Assert.Equal(s.SkillExportNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillExport)));
        Assert.Equal(s.SkillDeleteNamed("contract-review"), AutomationProperties.GetName(Of(own, s.SkillDelete)));
        Assert.Equal(s.SkillViewNamed("slide-design"), AutomationProperties.GetName(Of(bundled, s.SkillView)));
    });

    [Fact]
    public void Draft_from_this_chat_is_an_entry_of_the_library_only_when_the_host_gave_a_conversation() => Sta.Run(() =>
    {
        using (var without = SkillHost.OpenLibrary())
        {
            Assert.Empty(without.ButtonsSaying(without.Strings.SkillDraftTitle));
            Assert.DoesNotContain(without.Strings.SkillDraftNothingSaved, without.Texts());
        }

        using var host = SkillHost.OpenLibrary(model: new ScriptedChat().Says(GoodAnswer), draft: true);
        var s = host.Strings;
        Assert.DoesNotContain(s.SkillDraftNothingSaved, host.Texts());

        host.ButtonSaying(s.SkillDraftTitle).Command!.Execute(null);
        host.Pump();

        Assert.True(host.Library.DraftOpen);
        Assert.Contains(s.SkillDraftNothingSaved, host.Texts());
        Assert.Contains(s.SkillDraftStart, host.Texts());

        host.Await(host.Library.Draft!.DraftCommand.ExecuteAsync(null));
        host.WaitUntil(() => host.Library.Editor is not null);
        host.Pump();

        Assert.NotNull(host.Library.Editor);
        Assert.True(host.Library.Editor!.IsProposal);
        Assert.Contains(s.SkillEditorProposal, host.Texts());
        Assert.Equal(0, host.Rig.Workshop.Saves);
        Assert.Empty(host.BindingErrors);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void The_editor_counts_the_instructions_tokens_and_warns_when_over(string language) => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor(language, "edit");

        host.Editor.Instructions = new string('w', 1200 * 4);
        host.Pump();
        Assert.Equal(host.Strings.SkillTokens(1200, 5000), host.Editor.InstructionsTokensText);
        Assert.True(host.Find<TextBlock>("TokensText").IsVisible);
        Assert.False(host.Find<Border>("TokensWarning").IsVisible);
        Assert.Contains(host.Editor.InstructionsTokensText, host.Texts());

        host.Editor.Instructions = new string('w', 5001 * 4);
        host.Pump();
        Assert.False(host.Find<TextBlock>("TokensText").IsVisible);
        Assert.True(host.Find<Border>("TokensWarning").IsVisible);
        Assert.Contains(host.Strings.SkillTokens(5001, 5000), host.Texts());
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void The_try_panel_has_a_heading_for_the_matched_list() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor(mode: "edit");
        Assert.DoesNotContain(host.Strings.SkillTestMatchesHeading, host.Texts());

        host.Editor.KeywordsText = "contract";
        host.Editor.TestPrompt = "Please review this contract";
        host.Editor.RunTest();
        host.Pump();

        Assert.NotEmpty(host.Editor.TestMatches);
        Assert.Contains(host.Strings.SkillTestMatchesHeading, host.Texts());
    });

    [Fact]
    public void The_editor_shows_an_error_as_an_alert_and_information_as_a_status() => Sta.Run(() =>
    {
        using var host = SkillHost.OpenEditor();
        Directory.CreateDirectory(host.Rig.Root);
        File.WriteAllText(host.Rig.Folder, "not a folder");
        host.Editor.Name = "contract-review";
        host.Editor.Description = "Reviews a contract.";
        host.Editor.Instructions = "## Goal\nReview.\n";

        host.Await(host.Editor.SaveCommand.ExecuteAsync(null));
        host.WaitUntil(() => host.Editor.Message.Length > 0);
        host.Pump();

        Assert.Equal(SkillMessageKind.Error, host.Editor.MessageKind);
        Assert.True(host.Find<Border>("MessageError").IsVisible);
        Assert.False(host.Find<Border>("MessageInfo").IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(MessageLine(host, "MessageError")));
        Assert.Contains(host.Strings.SkillSaveFailed, host.Texts());
        Assert.Empty(host.BindingErrors);
    });

    [Theory]
    [MemberData(nameof(DraftStates))]
    public void Nothing_is_saved_until_Save_is_said_in_every_state_of_the_draft_view(string state, string language) => Sta.Run(() =>
    {
        using var host = DraftAt(state, language);

        Assert.True(host.Find<TextBlock>("StandingNote").IsVisible);
        Assert.Contains(host.Strings.SkillDraftNothingSaved, host.Texts());
        if (host.Draft.HasProposal)
        {
            var open = host.Find<Button>("OpenButton");
            Assert.Same(host.Draft.OpenProposalCommand, open.Command);
            Assert.Equal(host.Strings.SkillDraftOpen, AutomationProperties.GetName(open));
        }
    });}

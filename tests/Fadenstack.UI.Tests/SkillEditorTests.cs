using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Skills;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The skill editor's view model: the fields, the checks as you type, Save, Cancel, the files, the preview and the Test step.</summary>
public class SkillEditorTests
{
    private static SkillEditorViewModel NewEditor(SkillRig rig, IChatClient? model = null)
    {
        var library = rig.Library(model);
        library.NewCommand.Execute(null);
        return library.Editor!;
    }

    private static IEnumerable<string> Texts(SkillEditorViewModel editor) => editor.Issues.Select(i => i.Text);

    [Fact]
    public void A_new_skill_starts_with_its_checks_failing_and_Save_off_and_they_clear_as_you_type()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);

        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Contains("The name is empty.", Texts(editor));
        Assert.Contains("The description is empty. It tells the model when to use the skill.", Texts(editor));
        Assert.False(editor.IsDirty);

        editor.Name = "Contract Review";
        Assert.Contains("The name may use only lower-case letters, digits and single hyphens.", Texts(editor));
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";

        Assert.True(editor.CanSave);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.True(editor.IsDirty);
        Assert.DoesNotContain(editor.Issues, i => i.IsError);
    }

    [Fact]
    public void The_limits_show_their_numbers_and_a_long_body_is_only_a_warning()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Name = new string('a', 65);
        editor.Description = new string('d', 1025);
        editor.Compatibility = new string('c', 501);

        Assert.Contains("The name is 65 characters long; the limit is 64.", Texts(editor));
        Assert.Contains("The description is 1025 characters long; the limit is 1024.", Texts(editor));
        Assert.Contains("The compatibility note is 501 characters long; the limit is 500.", Texts(editor));

        editor.Name = "long-body";
        editor.Description = "x";
        editor.Compatibility = "";
        editor.Instructions = new string('w', 5001 * 4);

        Assert.True(editor.CanSave);
        Assert.Contains(editor.Issues, i => !i.IsError && i.Text.Contains("5001"));
        Assert.Equal(5001, editor.InstructionsTokens);
    }

    [Fact]
    public void Invisible_characters_and_scripts_stop_the_save_and_say_where()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Name = "a-skill";
        editor.Description = "Does" + char.ConvertFromUtf32(0x202E) + " it.";

        Assert.False(editor.CanSave);
        Assert.Contains("description contains an invisible character (U+202E). Remove it.", Texts(editor));

        editor.Description = "Does it.";
        editor.AddResource(SkillFile.FromText("scripts/run.py", "print(1)"));
        Assert.False(editor.CanSave);
        Assert.Contains("scripts/run.py is a script or a program. Scripts never run here; remove it.", Texts(editor));

        editor.Resources.Single().RemoveCommand.Execute(null);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public async Task Save_writes_the_skill_closes_the_editor_and_tells_the_library()
    {
        using var rig = new SkillRig();
        var library = rig.Library();
        library.NewCommand.Execute(null);
        var editor = library.Editor!;
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        editor.Instructions = "## Goal\nReview.";
        SkillEditorClosedEventArgs? closed = null;
        editor.Closed += (_, e) => closed = e;

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(rig.OnDisk("contract-review"));
        Assert.Equal(("contract-review", true), (closed!.Name, closed.Saved));
        Assert.Null(library.Editor);
        Assert.False(editor.IsDirty);
        Assert.Equal(SkillEditorMode.Edit, editor.Mode);
        Assert.Equal(1, rig.Workshop.Saves);
    }

    [Fact]
    public async Task Cancel_closes_without_saving_anything()
    {
        using var rig = new SkillRig();
        var library = rig.Library();
        library.NewCommand.Execute(null);
        var editor = library.Editor!;
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        SkillEditorClosedEventArgs? closed = null;
        editor.Closed += (_, e) => closed = e;

        editor.CancelCommand.Execute(null);

        Assert.False(closed!.Saved);
        Assert.Null(library.Editor);
        Assert.Equal(0, rig.Workshop.Saves);
        Assert.False(Directory.Exists(rig.Folder));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_name_that_is_taken_asks_before_it_replaces_and_a_new_name_withdraws_the_question()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc(description: "The old one."));
        var editor = NewEditor(rig);
        editor.Name = "contract-review";
        editor.Description = "The new one.";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.NeedsReplaceConfirmation);
        Assert.Equal("Replace contract-review?", editor.ReplaceTitle);
        Assert.Equal("The old one.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);

        editor.Name = "nda-review";
        Assert.False(editor.NeedsReplaceConfirmation);
        editor.Name = "contract-review";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.NeedsReplaceConfirmation);

        await editor.ConfirmReplaceCommand.ExecuteAsync(null);

        Assert.Equal("The new one.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);
    }

    [Fact]
    public async Task Editing_a_skill_and_renaming_it_moves_it()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        var library = rig.Library();
        await library.RefreshAsync();
        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));
        var editor = library.Editor!;

        editor.Name = "nda-review";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(rig.OnDisk("nda-review"));
        Assert.False(rig.OnDisk("contract-review"));
    }

    [Fact]
    public async Task A_bundled_skill_cannot_be_changed_or_saved()
    {
        using var rig = new SkillRig();
        var library = rig.Library();
        await library.RefreshAsync();
        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "slide-design"));
        var editor = library.Editor!;

        editor.Description = "Changed.";
        editor.Name = "other";
        editor.AddResource(SkillFile.FromText("a.md", "x"));

        Assert.Equal(("Designs a slide.", "slide-design"), (editor.Description, editor.Name));
        Assert.Empty(editor.Resources);
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.IsDirty);
        Assert.Equal(0, rig.Workshop.Saves);
    }

    [Fact]
    public void The_preview_is_the_chats_markdown_model_and_only_when_it_is_on()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Instructions = "# Goal\n\n- one\n- two\n\n`code`";

        Assert.Empty(editor.PreviewBlocks);

        editor.TogglePreviewCommand.Execute(null);

        Assert.True(editor.ShowPreview);
        Assert.NotEmpty(editor.PreviewBlocks);
        Assert.Equal(Fadenstack.UI.Markdown.MarkdownParser.Parse(editor.Instructions).Count, editor.PreviewBlocks.Count);
        var changes = new List<string?>();
        editor.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        editor.Instructions = "# Other";
        Assert.Contains(nameof(editor.PreviewBlocks), changes);
    }

    [Fact]
    public async Task The_faden_yaml_fields_are_edited_as_fields_and_written_back_as_a_faden_yaml()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        editor.KeywordsText = "contract, clause\nnda";
        editor.FileTypesText = ".docx; pdf";
        editor.PreferredToolsText = "read_page";
        editor.TagsText = "legal";
        editor.ExtensionTitle = "Contract review";
        editor.Priority = 70;
        editor.Activation = "always";
        Assert.Equal(new[] { "auto", "always", "manual" }, editor.ActivationOptions.Select(o => o.Value));
        Assert.Equal("Always", editor.ActivationOptions.Single(o => o.Value == "always").Label);

        await editor.SaveCommand.ExecuteAsync(null);

        var yaml = File.ReadAllText(Path.Combine(rig.Folder, "contract-review", "faden.yaml"));
        Assert.Contains("keywords:", yaml);
        Assert.Contains("- nda", yaml);
        var back = (await rig.Workshop.ReadAsync("contract-review")).Document!.Extension;
        Assert.Equal(new[] { "contract", "clause", "nda" }, back.Keywords);
        Assert.Equal(new[] { ".docx", "pdf" }, back.FileTypes);
        Assert.Equal(("Contract review", 70, "always"), (back.Title, back.Priority, back.Activation));
        Assert.DoesNotContain("keywords", File.ReadAllText(Path.Combine(rig.Folder, "contract-review", "SKILL.md")));
    }

    [Fact]
    public void A_priority_the_module_would_not_accept_is_an_error_here_already()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Name = "a-skill";
        editor.Description = "x";

        editor.Priority = 500;

        Assert.False(editor.CanSave);
        Assert.Contains(Texts(editor), t => t.StartsWith("faden.yaml cannot be read:"));
    }

    [Fact]
    public async Task Files_are_added_renamed_and_removed_and_the_checks_follow()
    {
        using var rig = new SkillRig();
        rig.Files.Resource = SkillFile.FromText("references\\tone.md", "Warm.");
        var editor = NewEditor(rig);
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";

        await editor.AddResourceCommand.ExecuteAsync(null);

        var row = editor.Resources.Single();
        Assert.Equal(("references/tone.md", true, true), (row.Path, row.IsText, editor.HasResources));

        row.NewPath = "../tone.md";
        row.RenameCommand.Execute(null);
        Assert.False(editor.CanSave);
        Assert.Contains(Texts(editor), t => t.Contains("is not allowed"));

        editor.Resources.Single().NewPath = "references/voice.md";
        editor.Resources.Single().RenameCommand.Execute(null);
        Assert.True(editor.CanSave);
        Assert.Equal("references/voice.md", editor.Resources.Single().Path);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "contract-review", "references", "voice.md")));
    }

    [Fact]
    public async Task A_skill_with_fields_beyond_the_six_cannot_be_saved_until_they_are_removed()
    {
        using var rig = new SkillRig();
        Directory.CreateDirectory(Path.Combine(rig.Folder, "extra-key"));
        File.WriteAllText(Path.Combine(rig.Folder, "extra-key", "SKILL.md"), "---\nname: extra-key\ndescription: Has a stray field.\nversion: 2\n---\nBody\n");
        var library = rig.Library();
        await library.RefreshAsync();
        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "extra-key"));
        var editor = library.Editor!;

        Assert.True(editor.HasUnknownKeys);
        Assert.False(editor.CanSave);
        Assert.Contains(Texts(editor), t => t.Contains("version"));

        editor.DropUnknownKeysCommand.Execute(null);

        Assert.False(editor.HasUnknownKeys);
        Assert.True(editor.CanSave);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("version", File.ReadAllText(Path.Combine(rig.Folder, "extra-key", "SKILL.md")));
    }

    [Fact]
    public async Task A_file_too_large_to_read_stops_the_save_so_that_it_is_not_dropped_silently()
    {
        using var rig = new SkillRig();
        await rig.Workshop.SaveAsync(SkillRig.Doc());
        File.WriteAllBytes(Path.Combine(rig.Folder, "contract-review", "big.bin"), new byte[SkillLimits.FileMaxBytes + 10]);
        var library = rig.Library();
        await library.RefreshAsync();

        await library.EditCommand.ExecuteAsync(library.Rows.Single(r => r.Name == "contract-review"));

        Assert.False(library.Editor!.CanSave);
        Assert.Contains(Texts(library.Editor), t => t.StartsWith("big.bin is larger than"));
    }

    [Fact]
    public void The_test_step_shows_what_the_pre_selection_lists_matches_and_pre_loads()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        editor.KeywordsText = "clause";

        editor.TestPrompt = "Please check this clause and make a slide.";
        editor.RunTestCommand.Execute(null);

        Assert.True(editor.HasTestResult);
        Assert.Contains("contract-review", editor.TestListed);
        Assert.Contains("contract-review — matches its keywords", editor.TestMatches);
        Assert.Contains("slide-design — matches its keywords", editor.TestMatches);
        Assert.Equal(new[] { "contract-review" }, editor.TestPreloaded);
        Assert.True(editor.TestIncludesThisSkill);
        Assert.Equal("", editor.TestNote);

        editor.TestPrompt = "Something else entirely.";
        editor.RunTestCommand.Execute(null);

        Assert.False(editor.TestIncludesThisSkill);
        Assert.Equal("No skill is offered for this message.", editor.TestNote);
        Assert.Equal(0, rig.Workshop.Saves);
    }

    [Fact]
    public async Task Ask_the_model_once_reports_whether_it_would_call_load_skill_and_runs_no_tool()
    {
        using var rig = new SkillRig();
        var model = new ScriptedChat().Loads("contract-review").Loads("slide-design").Says("I would just answer.").Throws();
        var editor = NewEditor(rig, model);
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        editor.TestPrompt = "Review this contract.";
        Assert.True(editor.CanAskModel);

        await editor.AskModelCommand.ExecuteAsync(null);
        Assert.Equal((SkillModelAnswer.LoadsThis, "The model would load contract-review."), (editor.ModelAnswer, editor.ModelAnswerText));
        await editor.AskModelCommand.ExecuteAsync(null);
        Assert.Equal((SkillModelAnswer.LoadsOther, "The model would load another skill instead: slide-design."), (editor.ModelAnswer, editor.ModelAnswerText));
        await editor.AskModelCommand.ExecuteAsync(null);
        Assert.Equal((SkillModelAnswer.LoadsNone, "The model would not load a skill for this message."), (editor.ModelAnswer, editor.ModelAnswerText));
        await editor.AskModelCommand.ExecuteAsync(null);
        Assert.Equal((SkillModelAnswer.Failed, "The model could not be asked."), (editor.ModelAnswer, editor.ModelAnswerText));

        var (messages, options) = model.Calls[0];
        Assert.Equal(4, model.Calls.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains("contract-review", messages[0].Text);
        Assert.Contains("<available_skills>", messages[0].Text);
        Assert.Equal("Review this contract.", messages[1].Text);
        // The one tool is only a declaration: it cannot run.
        var tool = Assert.Single(options!.Tools!);
        Assert.Equal("load_skill", tool.Name);
        Assert.False(tool is AIFunction);
        Assert.Equal(0, rig.Workshop.Saves);
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Fact]
    public async Task Without_a_model_the_question_is_off_and_with_no_skill_listed_no_call_is_made()
    {
        using var rig = new SkillRig();
        var editor = NewEditor(rig);
        editor.TestPrompt = "x";
        Assert.False(editor.CanAskModel);
        Assert.False(editor.AskModelCommand.CanExecute(null));

        var model = new ScriptedChat();
        var withModel = NewEditor(rig, model);
        withModel.CancelCommand.Execute(null);
        var other = NewEditor(rig, model);
        other.Name = "a-skill";
        other.Description = "x";
        other.KeywordsText = "never-matches";
        other.TestPrompt = "Something else.";

        await other.AskModelCommand.ExecuteAsync(null);

        Assert.Empty(model.Calls); // nothing is listed, so there is nothing to ask
        Assert.Equal(SkillModelAnswer.LoadsNone, other.ModelAnswer);
    }

    [Fact]
    public async Task A_newer_question_makes_an_older_answer_stale()
    {
        using var rig = new SkillRig();
        var gate = new TaskCompletionSource<ChatResponse>();
        var model = new ScriptedChat().Then((_, _) => gate.Task).Loads("contract-review");
        var editor = NewEditor(rig, model);
        editor.Name = "contract-review";
        editor.Description = "Reviews a contract.";
        editor.TestPrompt = "Review this contract.";

        var older = editor.AskModelCommand.ExecuteAsync(null);
        await Wait.Until(() => model.Calls.Count == 1);
        Assert.Equal(SkillModelAnswer.Asking, editor.ModelAnswer);
        await editor.AskModelCommand.ExecuteAsync(null);
        gate.SetResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I would just answer.")));
        await older;

        Assert.Equal(SkillModelAnswer.LoadsThis, editor.ModelAnswer);
    }

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        using var rig = new SkillRig();
        var language = "en";
        var library = rig.Library(strings: () => SkillRig.In(language));
        library.NewCommand.Execute(null);
        var editor = library.Editor!;
        Assert.Equal("New skill", editor.Heading);
        Assert.Contains("The name is empty.", Texts(editor));

        language = "de";
        editor.Retext();

        Assert.Equal("Neuer Skill", editor.Heading);
        Assert.Contains("Der Name ist leer.", Texts(editor));
        Assert.Equal("Immer", editor.ActivationOptions.Single(o => o.Value == "always").Label);
        language = "fr";
        editor.Retext();
        Assert.Contains("Le nom est vide.", Texts(editor));
        await Task.CompletedTask;
    }
}

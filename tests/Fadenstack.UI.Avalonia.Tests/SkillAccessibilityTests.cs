using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Tests;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The skill views' screen-reader contract: in every state and both languages, every button, switch, check box, text box and combo box
/// that is on screen has an explicit name from the view model's texts; the check lines, the answers and the draft's state are live
/// regions; nothing that changes by itself every second is.
/// </summary>
public class SkillAccessibilityTests
{
    private static string? NameOf(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name : null;

    private static IEnumerable<Control> Named(SkillHost host) => host.Visible().Where(c =>
        c is Button or ToggleButton or TextBox or CheckBox or ComboBox || c is ScrollViewer { Focusable: true });

    private static List<string> Unnamed(SkillHost host, string where)
    {
        var failures = new List<string>();
        foreach (var control in Named(host))
        {
            var what = $"{where}: {control.GetType().Name} '{string.Join(' ', control.Classes)}' {control.Name}";
            if (string.IsNullOrWhiteSpace(NameOf(control)))
            {
                failures.Add(what + " has no automation name");
            }
            else if (control is Button { Content: string shown } && NameOf(control) != shown)
            {
                failures.Add(what + $" is named '{NameOf(control)}' but says '{shown}'");
            }
        }
        return failures;
    }

    [AvaloniaTheory]
    [MemberData(nameof(SkillViewTests.LibraryStates), MemberType = typeof(SkillViewTests))]
    public async Task Every_input_and_button_of_the_library_has_a_name_in_every_state(string state, string language)
    {
        using var host = await SkillViewTests.LibraryAtAsync(state, language);

        var failures = Unnamed(host, $"library/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaTheory]
    [MemberData(nameof(SkillViewTests.EditorStates), MemberType = typeof(SkillViewTests))]
    public async Task Every_input_and_button_of_the_editor_has_a_name_in_every_state(string state, string language)
    {
        using var host = await SkillViewTests.EditorAtAsync(state, language);

        var failures = Unnamed(host, $"editor/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaTheory]
    [MemberData(nameof(SkillViewTests.DraftStates), MemberType = typeof(SkillViewTests))]
    public async Task Every_input_and_button_of_the_draft_has_a_name_in_every_state(string state, string language)
    {
        using var host = await SkillViewTests.DraftAtAsync(state, language);

        var failures = Unnamed(host, $"draft/{state}/{language}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public async Task The_views_between_them_check_a_good_number_of_controls()
    {
        var counted = 0;
        foreach (var state in new[] { "list", "delete", "import" })
        {
            using var host = await SkillViewTests.LibraryAtAsync(state, "en");
            counted += Named(host).Count();
        }
        foreach (var state in new[] { "new", "files", "replace" })
        {
            using var host = await SkillViewTests.EditorAtAsync(state, "en");
            counted += Named(host).Count();
        }
        using (var host = await SkillViewTests.DraftAtAsync("ready", "en"))
        {
            counted += Named(host).Count();
        }
        Assert.True(counted > 60, $"only {counted} controls were checked");
    }

    [AvaloniaFact]
    public async Task The_check_lines_the_answers_and_the_drafts_state_are_live_regions()
    {
        using (var editor = await SkillViewTests.EditorAtAsync("errors", "en"))
        {
            var lines = editor.Visible().OfType<TextBlock>().Where(t => editor.Editor.Issues.Any(i => i.Text == t.Text)).ToList();
            Assert.NotEmpty(lines);
            Assert.Contains(lines, t => AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
        }
        using (var asked = await SkillViewTests.EditorAtAsync("asked", "en"))
        {
            var answer = asked.Find<TextBlock>("ModelAnswerText");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(answer));
        }
        using (var draft = await SkillViewTests.DraftAtAsync("failed", "en"))
        {
            var error = draft.Find<TextBlock>("ErrorText");
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
        }
        using (var ready = await SkillViewTests.DraftAtAsync("ready", "en"))
        {
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(ready.Find<TextBlock>("ReadyText")));
        }
        using (var working = await SkillViewTests.DraftAtAsync("working", "en"))
        {
            Assert.All(working.Visible().OfType<ProgressBar>(), bar => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar))));
        }
    }

    [AvaloniaFact]
    public async Task The_rows_and_the_files_are_named_groups_and_the_switch_has_the_skills_name()
    {
        using var library = await SkillViewTests.LibraryAtAsync("list", "en");
        var switches = library.Visible().OfType<ToggleSwitch>().ToList();
        Assert.Equal(new[] { "contract-review", "slide-design" }, switches.Select(NameOf).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(library.Visible().OfType<Border>().Where(b => b.Classes.Contains("card") && b.DataContext is SkillRowViewModel),
            card => Assert.False(string.IsNullOrWhiteSpace(NameOf(card))));

        using var editor = await SkillViewTests.EditorAtAsync("files", "en");
        var file = editor.Visible().OfType<Border>().Single(b => b.Classes.Contains("card") && b.DataContext is SkillResourceViewModel);
        Assert.Equal("references/notes.md", NameOf(file));
    }
}

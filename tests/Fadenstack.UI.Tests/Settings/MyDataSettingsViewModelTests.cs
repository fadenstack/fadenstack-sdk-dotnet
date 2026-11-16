using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.UI.Settings;
using Xunit;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>"Your data on this device": the buttons, the two confirmations, the result, the failures, the export and the texts.</summary>
public class MyDataSettingsViewModelTests
{
    private static readonly UiStrings English = UiStrings.For(new CultureInfo("en"));

    private static MyDataSettingsViewModel Model(FakeMyData fake, Func<UiStrings>? strings = null) => new(fake, fake, strings: strings ?? (() => English));

    [Fact]
    public void It_starts_with_the_buttons()
    {
        var model = Model(new FakeMyData());

        Assert.Equal(MyDataState.Idle, model.State);
        Assert.True(model.IsIdle);
        Assert.All(new[] { model.ExportCommand, model.ExportAllCommand, model.RemoveCommand, model.RemoveAllCommand }, c => Assert.True(c.CanExecute(null)));
        Assert.False(model.ConfirmCommand.CanExecute(null));
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Equal("", model.StatusText);
        Assert.Equal("", model.ErrorText);
        Assert.Equal("Your data on this device", model.Strings.MyDataTitle);
    }

    [Fact]
    public async Task Remove_with_other_apps_says_the_shared_data_stays_and_names_them()
    {
        var fake = FakeMyData.Of("confirm-shared");
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.ConfirmThisApp, model.State);
        Assert.True(model.SharedDataStays);
        Assert.Equal(new[] { "Desktop agent", "Excel add-in" }, model.OtherApps);
        Assert.Equal(
            "This removes this app's chats, approvals and preferences on this device. Your shared data (skills, the MCP list, keys and provenance records) stays, because other Fadenstack apps use it: Desktop agent, Excel add-in.",
            model.ConfirmText);
        Assert.Equal("Remove my data", model.ConfirmButtonText);
        Assert.Equal("This cannot be undone.", model.UndoText);
        Assert.Empty(model.AppLines);
        Assert.True(model.ConfirmCommand.CanExecute(null));
        Assert.True(model.CancelCommand.CanExecute(null));
        Assert.False(model.RemoveCommand.CanExecute(null));
        Assert.Equal(1, fake.AppsReads);
        Assert.Empty(fake.Deleted);
    }

    [Fact]
    public async Task An_app_known_only_by_its_data_is_named_as_it_was_given()
    {
        var fake = new FakeMyData { Apps = new[] { FakeMyData.ThisApp, FakeMyData.Legacy } };
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "old-assistant" }, model.OtherApps);
        Assert.Contains("because other Fadenstack apps use it: old-assistant.", model.ConfirmText);
    }

    [Fact]
    public async Task Remove_without_other_apps_says_everything_goes()
    {
        var model = Model(FakeMyData.Of("confirm-everything"));

        await model.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.ConfirmThisApp, model.State);
        Assert.False(model.SharedDataStays);
        Assert.Empty(model.OtherApps);
        Assert.Equal(
            "No other Fadenstack app uses your data on this device, so everything of yours here is removed: chats, approvals, preferences, skills, the MCP list, keys and provenance records.",
            model.ConfirmText);
    }

    [Fact]
    public async Task Remove_all_lists_every_app_it_affects_and_marks_this_one()
    {
        var model = Model(FakeMyData.Of("confirm-all"));

        await model.RemoveAllCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.ConfirmAll, model.State);
        Assert.True(model.IsConfirmAll && model.IsConfirming && !model.IsConfirmThisApp);
        Assert.Equal(new[] { "Word add-in (this app)", "Desktop agent", "old-assistant" }, model.AppLines);
        Assert.True(model.HasAppLines);
        Assert.Equal("This removes all your Fadenstack data on this device, shared data included. These apps are affected:", model.ConfirmText);
        Assert.Equal("Remove everything", model.ConfirmButtonText);
    }

    [Fact]
    public async Task Confirming_removes_this_apps_data_and_says_what_stayed()
    {
        var fake = FakeMyData.Of("done-kept");
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DataScope.ThisApp }, fake.Deleted);
        Assert.Equal(MyDataState.Done, model.State);
        Assert.Equal("Removed. Shared data kept for: Desktop agent, Excel add-in.", model.StatusText);
        Assert.True(model.CancelCommand.CanExecute(null)); // closes the result
        Assert.False(model.HasStatusLine);
        Assert.Equal("", model.ConfirmText);
    }

    [Fact]
    public async Task Confirming_when_no_other_app_uses_the_data_says_everything_was_removed()
    {
        var fake = FakeMyData.Of("done-everything");
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DataScope.ThisApp }, fake.Deleted);
        Assert.Equal("Removed everything.", model.StatusText);
    }

    [Fact]
    public async Task Confirming_remove_all_removes_everything()
    {
        var fake = FakeMyData.Of("done-all");
        var model = Model(fake);

        await model.RemoveAllCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DataScope.AllApps }, fake.Deleted);
        Assert.Equal(MyDataState.Done, model.State);
        Assert.Equal("Removed everything.", model.StatusText);
    }

    [Fact]
    public async Task The_result_also_says_which_agents_stayed_and_that_a_store_of_the_app_is_its_own()
    {
        var fake = FakeMyData.Of("confirm-shared");
        fake.Deletion = scope => new DataDeletion(1, true, false)
        {
            Scope = scope, OtherApps = new[] { "Desktop agent" }, AgentsKept = new[] { "docs-assistant", "shared-agent" },
        };
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            "Removed. Shared data kept for: Desktop agent. Data of these agents stays, because another app uses them too: docs-assistant, shared-agent. "
            + "Chats or approvals that this app keeps in a store of its own were not removed here.",
            model.StatusText);
    }

    [Fact]
    public async Task Cancel_leaves_a_confirmation_and_deletes_nothing()
    {
        var fake = FakeMyData.Of("confirm-shared");
        var model = Model(fake);
        await model.RemoveCommand.ExecuteAsync(null);

        model.CancelCommand.Execute(null);

        Assert.Equal(MyDataState.Idle, model.State);
        Assert.Empty(fake.Deleted);
        Assert.Equal("", model.ConfirmText);
        Assert.True(model.RemoveCommand.CanExecute(null));
        Assert.False(model.ConfirmCommand.CanExecute(null));
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_closes_the_result_and_the_error()
    {
        var done = Model(FakeMyData.Of("done-everything"));
        await done.RemoveCommand.ExecuteAsync(null);
        await done.ConfirmCommand.ExecuteAsync(null);
        done.CancelCommand.Execute(null);
        Assert.Equal(MyDataState.Idle, done.State);
        Assert.Equal("", done.StatusText);

        var failed = Model(FakeMyData.Of("error"));
        await failed.RemoveCommand.ExecuteAsync(null);
        Assert.True(failed.IsError);
        failed.CancelCommand.Execute(null);
        Assert.Equal(MyDataState.Idle, failed.State);
        Assert.Equal("", failed.ErrorText);
    }

    [Fact]
    public async Task While_busy_nothing_can_be_pressed()
    {
        var fake = FakeMyData.Of("busy");
        var model = Model(fake);

        var running = model.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Busy, model.State);
        Assert.True(model.IsBusy);
        Assert.Equal("Please wait…", model.StatusText);
        Assert.All(
            new System.Windows.Input.ICommand[] { model.ExportCommand, model.ExportAllCommand, model.RemoveCommand, model.RemoveAllCommand, model.ConfirmCommand, model.CancelCommand },
            c => Assert.False(c.CanExecute(null)));

        fake.Gate!.SetResult(true);
        await running;
        Assert.Equal(MyDataState.ConfirmThisApp, model.State);
    }

    [Fact]
    public async Task Apps_that_cannot_be_read_are_an_error_without_the_reason_and_nothing_is_removed()
    {
        var fake = FakeMyData.Of("error");
        var model = Model(fake);

        await model.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Error, model.State);
        Assert.Equal("The apps that use your data could not be listed. Nothing was removed. Try again.", model.ErrorText);
        Assert.DoesNotContain("secret", model.ErrorText + model.StatusText);
        Assert.Empty(fake.Deleted);
        Assert.False(model.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_removal_that_fails_is_an_error_without_the_reason()
    {
        var fake = FakeMyData.Of("confirm-shared");
        fake.ThrowOnDelete = new IOException("C:\\Users\\someone\\secret.json is locked");
        var model = Model(fake);
        await model.RemoveCommand.ExecuteAsync(null);

        await model.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Error, model.State);
        Assert.Equal("Your data could not be removed completely. Try again.", model.ErrorText);
        Assert.DoesNotContain("secret", model.ErrorText);
        Assert.Equal("", model.StatusText);
    }

    [Fact]
    public async Task An_export_cancelled_in_the_dialog_changes_nothing()
    {
        var fake = new FakeMyData { Target = null };
        var model = Model(fake);

        await model.ExportCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Idle, model.State);
        Assert.Equal("", model.StatusText);
        Assert.Empty(fake.Exported);
        Assert.Equal(new[] { "fadenstack-my-data.zip" }, fake.Suggested);
        Assert.True(model.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_export_writes_this_apps_scope_and_closes_the_file()
    {
        var fake = new FakeMyData();
        var model = Model(fake);

        await model.ExportCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DataScope.ThisApp }, fake.Exported);
        Assert.Equal("zip:ThisApp", System.Text.Encoding.UTF8.GetString(fake.Target!.ToArray()));
        Assert.True(fake.Target.Closed);
        Assert.Equal(MyDataState.Idle, model.State);
        Assert.Equal("Your data was saved to the file you chose.", model.StatusText);
        Assert.True(model.HasStatusLine);
        model.RemoveCommand.Execute(null);
        Assert.NotEqual("Your data was saved to the file you chose.", model.StatusText);
    }

    [Fact]
    public async Task Export_all_writes_everything_under_its_own_name()
    {
        var fake = new FakeMyData();
        var model = Model(fake);

        await model.ExportAllCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DataScope.AllApps }, fake.Exported);
        Assert.Equal(new[] { "fadenstack-all-my-data.zip" }, fake.Suggested);
        Assert.Equal("zip:AllApps", System.Text.Encoding.UTF8.GetString(fake.Target!.ToArray()));
    }

    [Fact]
    public async Task An_export_that_fails_is_an_error_and_still_closes_the_file()
    {
        var fake = new FakeMyData { ThrowOnExport = new InvalidOperationException("disk full at secret place") };
        var model = Model(fake);

        await model.ExportCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Error, model.State);
        Assert.Equal("The export did not work. Try again.", model.ErrorText);
        Assert.DoesNotContain("secret", model.ErrorText);
        Assert.True(fake.Target!.Closed);
    }

    [Fact]
    public async Task A_save_target_that_fails_is_an_export_error()
    {
        var fake = new FakeMyData { ThrowOnOpen = new UnauthorizedAccessException() };
        var model = Model(fake);

        await model.ExportAllCommand.ExecuteAsync(null);

        Assert.Equal(MyDataState.Error, model.State);
        Assert.Equal("The export did not work. Try again.", model.ErrorText);
        Assert.Empty(fake.Exported);
        model.CancelCommand.Execute(null);
        Assert.True(model.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Retext_says_every_text_again_in_the_new_language()
    {
        var fake = FakeMyData.Of("done-kept");
        var strings = English;
        var model = Model(fake, () => strings);
        await model.RemoveCommand.ExecuteAsync(null);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var confirm = model.ConfirmText;

        strings = UiStrings.For(new CultureInfo("de"));
        model.Retext();

        Assert.Contains("Ihre gemeinsamen Daten", model.ConfirmText);
        Assert.NotEqual(confirm, model.ConfirmText);
        Assert.Equal("Meine Daten entfernen", model.ConfirmButtonText);
        Assert.Contains(nameof(MyDataSettingsViewModel.Strings), changed);
        Assert.Contains(nameof(MyDataSettingsViewModel.ConfirmText), changed);
        Assert.Contains(nameof(MyDataSettingsViewModel.ConfirmButtonText), changed);

        await model.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Entfernt. Gemeinsame Daten bleiben erhalten für: Desktop agent, Excel add-in.", model.StatusText);
        strings = UiStrings.For(new CultureInfo("fr"));
        model.Retext();
        Assert.Equal("Supprimé. Données partagées conservées pour\u00a0: Desktop agent, Excel add-in.", model.StatusText);
    }

    [Fact]
    public void A_state_change_is_told_to_the_view()
    {
        var model = Model(FakeMyData.Of("confirm-shared"));
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.RemoveCommand.Execute(null);

        Assert.Contains(string.Empty, changed); // "" says every property changed, State and the texts with it
        Assert.Equal(MyDataState.ConfirmThisApp, model.State);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void Every_text_exists_in_every_language(string language)
    {
        var s = UiStrings.For(new CultureInfo(language));
        var texts = new[]
        {
            s.MyDataTitle, s.MyDataIntro, s.MyDataExport, s.MyDataExportName, s.MyDataExportAll, s.MyDataExportAllName, s.MyDataRemove, s.MyDataRemoveName,
            s.MyDataRemoveAll, s.MyDataConfirmThisAppShared("A, B"), s.MyDataConfirmThisAppEverything, s.MyDataConfirmAll, s.MyDataConfirmUndo, s.MyDataThisApp("A"),
            s.MyDataConfirmRemove, s.MyDataConfirmRemoveAll, s.MyDataCancel, s.MyDataDismiss, s.MyDataBusy, s.MyDataRemovedEverything, s.MyDataRemovedKept("A, B"),
            s.MyDataAgentsKept("a"), s.MyDataHostStores, s.MyDataExported, s.MyDataErrorRead, s.MyDataErrorRemove, s.MyDataErrorExport, s.MyDataListSeparator,
        };
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t) || t.StartsWith("MyData", StringComparison.Ordinal), $"{language}: '{t}'"));
        Assert.Contains("A, B", s.MyDataConfirmThisAppShared("A, B"));
        Assert.Contains("A", s.MyDataThisApp("A"));
        // a button's accessible name says what the button says (WCAG 2.5.3)
        Assert.Contains(s.MyDataExport, s.MyDataExportName);
        Assert.Contains(s.MyDataExportAll, s.MyDataExportAllName);
        Assert.Contains(s.MyDataRemove, s.MyDataRemoveName);
    }

    [Fact]
    public void French_keeps_its_non_breaking_space_before_a_colon()
    {
        var fr = UiStrings.For(new CultureInfo("fr"));
        Assert.Contains("\u00a0: ", fr.MyDataRemovedKept("A"));
        Assert.Contains("\u00a0:", fr.MyDataConfirmAll);
        Assert.DoesNotContain(" :", fr.MyDataConfirmThisAppShared("A").Replace("\u00a0:", ""));
    }
}

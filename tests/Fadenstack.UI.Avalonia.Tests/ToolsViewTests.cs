using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The Tools view over the real view model and the fake catalog (sdk/51 F05): every state draws without a binding error, the plain-http
/// warning's Confirm has the focus when it opens and Esc says no to it, and a secret value typed into a row's masked box reaches the row
/// and is gone from the box after Save and after Cancel. The view model's own behaviour is tested in Fadenstack.UI.Tests.
/// </summary>
public class ToolsViewTests
{
    public static IEnumerable<object[]> States() => ToolsScenes.States.Select(s => new object[] { s });

    [AvaloniaTheory]
    [MemberData(nameof(States))]
    public async Task Every_state_draws_without_a_binding_error(string state)
    {
        using var host = await ToolsHost.OpenAsync(state);
        Assert.Empty(host.BindingErrors);
        Assert.Contains(host.Page.Strings.ToolsTitle, host.Texts());
    }

    [AvaloniaFact]
    public async Task The_list_shows_each_status_with_its_text_and_the_fix()
    {
        using var host = await ToolsHost.OpenAsync("list");
        var texts = host.Texts();
        Assert.Contains(texts, t => t.StartsWith(host.Page.Strings.ToolsStateRunning, StringComparison.Ordinal));
        Assert.Contains(texts, t => t == host.Page.Strings.ToolsStateMissingValue("GITHUB_TOKEN"));
        Assert.Contains(host.Visible().OfType<Button>(), b => b.Content as string == host.Page.Strings.ToolsFixAddValue);
        Assert.Equal(4, host.Find<ItemsControl>("ServerCards").ItemCount);
    }

    [AvaloniaFact]
    public async Task Each_plain_http_refusal_says_why_on_its_card()
    {
        using var host = await ToolsHost.OpenAsync("refusals");
        var s = host.Page.Strings;
        var texts = host.Texts();
        Assert.Contains(s.McpPlainHttpNoOptIn, texts);
        Assert.Contains(s.McpPlainHttpNotLocal, texts);
        Assert.Contains(s.McpPlainHttpSecret, texts);
        Assert.Contains(s.McpPlainHttpNotAllowed, texts);
        Assert.Contains(s.ToolsStateNotAllowed, texts);
    }

    [AvaloniaFact]
    public async Task A_destructive_tool_shows_Destructive_without_a_picker_and_a_governed_server_has_no_pickers()
    {
        using (var host = await ToolsHost.OpenAsync("drawer-tools"))
        {
            var s = host.Page.Strings;
            // Five tools; four can be raised (the destructive one cannot).
            Assert.Equal(4, host.Visible().OfType<ComboBox>().Count());
            Assert.Contains(s.ToolsClassDestructive, host.Texts());
            Assert.Contains(s.ToolsDestructiveHelp, host.Texts());
            Assert.True(host.Find<ToggleSwitch>("TrustedSwitch").IsEffectivelyEnabled);
        }
        using (var host = await ToolsHost.OpenAsync("drawer-governed"))
        {
            Assert.Empty(host.Visible().OfType<ComboBox>());
            Assert.False(host.Find<ToggleSwitch>("TrustedSwitch").IsEffectivelyEnabled);
            Assert.Contains(host.Page.Strings.ToolsTrustedByAdmin, host.Texts());
        }
    }

    // ---- Advanced: the plain-http switch and its warning --------------------------------------------------------------

    [AvaloniaFact]
    public async Task Advanced_shows_only_where_the_view_model_shows_it()
    {
        using (var host = await ToolsHost.OpenAsync("drawer"))
        {
            Assert.False(host.Has("AdvancedSection"));
        }
        using (var host = await ToolsHost.OpenAsync("drawer-advanced"))
        {
            Assert.True(host.Has("AdvancedSection"));
            Assert.False(host.Has("PlainHttpWarning"));
        }
    }

    [AvaloniaFact]
    public async Task The_warning_opens_with_the_focus_on_Confirm_and_Confirm_turns_the_switch_on()
    {
        using var host = await ToolsHost.OpenAsync("drawer-advanced");
        host.Find<ToggleSwitch>("PlainHttpSwitch").IsChecked = true;
        await host.PumpAsync(12);
        Assert.True(host.Page.Details!.IsPlainHttpWarningOpen);
        var confirm = host.Find<Button>("PlainHttpConfirmButton");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.Same(confirm, host.Focused());
        Assert.Contains(host.Page.Strings.McpPlainHttpWarning, host.Texts());
        Assert.DoesNotContain("plain-http nas True", host.Catalog.Calls);

        confirm.Command!.Execute(null);
        await host.PumpAsync(12);
        Assert.Contains($"plain-http nas True {UiStrings.McpPlainHttpWarningVersion}", host.Catalog.Calls);
        Assert.False(host.Has("PlainHttpWarning"));
    }

    [AvaloniaFact]
    public async Task Esc_says_no_to_the_warning_first_then_closes_the_drawer()
    {
        using var host = await ToolsHost.OpenAsync("drawer-warning");
        await host.PumpAsync(12);
        Assert.True(host.Page.Details!.IsPlainHttpWarningOpen);

        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await host.PumpAsync();
        Assert.True(host.Page.IsDetailsOpen);
        Assert.False(host.Page.Details!.IsPlainHttpWarningOpen);
        Assert.DoesNotContain(host.Catalog.Calls, c => c.StartsWith("plain-http", StringComparison.Ordinal));
        Assert.False(host.Page.Details.PlainHttpOn);

        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await host.PumpAsync();
        Assert.False(host.Page.IsDetailsOpen);
    }

    [AvaloniaFact]
    public async Task Cancel_in_the_warning_leaves_the_switch_off()
    {
        using var host = await ToolsHost.OpenAsync("drawer-warning");
        host.Find<Button>("PlainHttpCancelButton").Command!.Execute(null);
        await host.PumpAsync();
        Assert.False(host.Has("PlainHttpWarning"));
        Assert.False(host.Find<ToggleSwitch>("PlainHttpSwitch").IsChecked);
    }

    // ---- secret values --------------------------------------------------------------------------------------------------

    private static TextBox SecretBox(ToolsHost host, int row) =>
        host.View.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name == "SecretValueBox").ElementAt(row);

    [AvaloniaFact]
    public async Task A_typed_secret_reaches_the_row_masked_and_is_gone_from_the_box_after_Save()
    {
        using var host = await ToolsHost.OpenAsync("add-address");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var box = SecretBox(host, 1);
        Assert.Equal('●', box.PasswordChar);
        box.Text = "s3cret-value";
        await host.PumpAsync();
        Assert.Equal("s3cret-value", sheet.HeaderRows[1].Value);
        sheet.HeaderRows[1].Name = "X-Api-Key";

        await sheet.SaveCommand.ExecuteAsync(null);
        await host.PumpAsync();
        Assert.Null(host.Page.Sheet);
        Assert.Contains("s3cret-value", host.Catalog.SecretValuesSeen);
        Assert.True(string.IsNullOrEmpty(box.Text), "the box still holds the value");
        Assert.Equal("", sheet.HeaderRows[1].Value);
    }

    [AvaloniaFact]
    public async Task A_typed_secret_is_gone_from_the_box_after_Cancel()
    {
        using var host = await ToolsHost.OpenAsync("add-address");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var box = SecretBox(host, 1);
        box.Text = "s3cret-value";
        await host.PumpAsync();
        Assert.Equal("s3cret-value", sheet.HeaderRows[1].Value);

        sheet.CancelCommand.Execute(null);
        await host.PumpAsync();
        Assert.Null(host.Page.Sheet);
        Assert.True(string.IsNullOrEmpty(box.Text), "the box still holds the value");
        Assert.Equal("", sheet.HeaderRows[1].Value);
        Assert.DoesNotContain("s3cret-value", host.Catalog.SecretValuesSeen);
    }

    [AvaloniaFact]
    public async Task A_saved_value_shows_dots_and_Replace_and_Replace_gives_an_empty_masked_box()
    {
        using var host = await ToolsHost.OpenAsync("edit-kept");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var s = host.Page.Strings;
        Assert.Contains(s.ToolsValueSaved, host.Texts());
        var replace = host.Visible().OfType<Button>().Single(b => b.Name == "ReplaceValueButton");
        Assert.Equal(s.ToolsReplaceValue, replace.Content);
        // The kept row has no box; the other row has one, masked.
        Assert.Single(host.Visible().OfType<TextBox>(), t => t.Name == "SecretValueBox");

        replace.Command!.Execute(null);
        await host.PumpAsync();
        Assert.False(sheet.EnvironmentRows[0].IsKept);
        var boxes = host.Visible().OfType<TextBox>().Where(t => t.Name == "SecretValueBox").ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.Equal('●', b.PasswordChar));
    }

    // ---- the sheets -------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_scan_shows_its_progress_while_it_runs_and_its_findings_when_it_is_done()
    {
        using (var host = await ToolsHost.OpenAsync("scan-running"))
        {
            Assert.True(host.Has("ScanProgress"));
            Assert.True(host.Page.Sheet is ScanSheetViewModel { IsScanning: true });
            host.Scene.ScanGate!.TrySetResult(true);
        }
        using (var host = await ToolsHost.OpenAsync("scan-done"))
        {
            Assert.False(host.Has("ScanProgress"));
            Assert.True(host.Has("ScanDoneRow"));
            Assert.Equal(4, ((ScanSheetViewModel)host.Page.Sheet!).ChosenCount);
            Assert.Contains(host.Page.Strings.ToolsScanAddMany(4), host.Texts());
        }
    }

    [AvaloniaFact]
    public async Task The_app_tab_is_first_and_draws_the_apps_tools_read_only()
    {
        using var host = await ToolsHost.OpenAsync("app");
        var s = host.Page.Strings;
        var texts = host.Texts();
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.Equal(["AppTab", "LocalTab", "ServerTab"], host.Find<Border>("ToolsTabs").GetVisualDescendants().OfType<RadioButton>().Select(r => r.Name));
        Assert.True(host.Find<RadioButton>("AppTab").IsChecked);
        Assert.Equal(host.Page.AppTabText, global::Avalonia.Automation.AutomationProperties.GetName(host.Find<RadioButton>("AppTab")));
        Assert.True(host.Find<RadioButton>("AppTab").Focusable);
        Assert.Contains("read_page", texts);
        Assert.Contains("click_button", texts);
        Assert.Contains("Reads the page that is open in the browser. Returns its text.", texts);
        Assert.Contains(s.ApprovalClassLabel(Fadenstack.Tools.ToolClass.Destructive), texts);
        Assert.Equal(4, host.Find<ItemsControl>("AppTools").ItemCount);
        Assert.False(host.Has("AppEmpty"));
        Assert.False(host.Has("PolicyChip"));
        Assert.Empty(host.Visible().OfType<ToggleSwitch>());
    }

    [AvaloniaFact]
    public async Task The_server_tab_is_there_only_when_governed()
    {
        using (var host = await ToolsHost.OpenAsync("app"))
        {
            Assert.True(host.Has("ServerTab"));
        }
        using (var host = await ToolsHost.OpenAsync("app-standalone"))
        {
            Assert.False(host.Has("ServerTab"));
            Assert.True(host.Has("AppTab"));
            Assert.True(host.Has("LocalTab"));
            Assert.Equal(0, host.Catalog.CloudCalls);
            Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        }
        using (var host = await ToolsHost.OpenAsync("standalone"))
        {
            Assert.False(host.Has("ServerTab"));
            Assert.True(host.Page.ShowsLocal);
            Assert.True(host.Has("PolicyChip"));
        }
    }

    [AvaloniaFact]
    public async Task The_app_tab_without_tools_has_its_one_line()
    {
        using var host = await ToolsHost.OpenAsync("app");
        host.Catalog.App.Clear();
        host.Page.Refresh();
        await host.PumpAsync();
        Assert.True(host.Has("AppEmpty"));
        Assert.Contains(host.Page.Strings.ToolsAppNone, host.Texts());
    }

    [AvaloniaFact]
    public async Task The_cloud_tab_lists_the_governed_tools_read_only()
    {
        using var host = await ToolsHost.OpenAsync("cloud");
        var s = host.Page.Strings;
        Assert.Contains("search_repositories", host.Texts());
        Assert.Contains(s.ToolsCloudOffByAdmin, host.Texts());
        Assert.Equal(2, host.Find<ItemsControl>("CloudGroups").ItemCount);
        Assert.Empty(host.Visible().OfType<ToggleSwitch>());
    }
}

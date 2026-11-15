using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The Tools view over the real view model and the fake catalog (sdk/51 F05): every state draws without a binding error, the plain-http
/// warning's Confirm has the focus when it opens and Esc says no to it, and a secret value typed into a row's password box reaches the row
/// and is gone from the box after Save and after Cancel. The view model's own behaviour is tested in Fadenstack.UI.Tests.
/// </summary>
public class ToolsViewTests
{
    public static IEnumerable<object[]> States() => ToolsScenes.States.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(States))]
    public void Every_state_draws_without_a_binding_error(string state) => Sta.Run(() =>
    {
        using var host = new ToolsHost(state);
        Assert.True(host.BindingErrors.Count == 0, string.Join("\n", host.BindingErrors));
        Assert.Contains(host.Page.Strings.ToolsTitle, host.Texts());
    });

    [Fact]
    public void The_list_shows_each_status_with_its_text_and_the_fix() => Sta.Run(() =>
    {
        using var host = new ToolsHost("list");
        var texts = host.Texts();
        var s = host.Page.Strings;
        Assert.Contains(texts, t => t.StartsWith(s.ToolsStateRunning, StringComparison.Ordinal));
        Assert.Contains(texts, t => t == s.ToolsStateMissingValue("GITHUB_TOKEN"));
        Assert.Contains(host.Visible().OfType<Button>(), b => b.Content as string == s.ToolsFixAddValue);
        Assert.Equal(4, host.Find<ItemsControl>("ServerCards").Items.Count);
    });

    [Fact]
    public void Each_plain_http_refusal_says_why_on_its_card() => Sta.Run(() =>
    {
        using var host = new ToolsHost("refusals");
        var s = host.Page.Strings;
        var texts = host.Texts();
        Assert.Contains(s.McpPlainHttpNoOptIn, texts);
        Assert.Contains(s.McpPlainHttpNotLocal, texts);
        Assert.Contains(s.McpPlainHttpSecret, texts);
        Assert.Contains(s.McpPlainHttpNotAllowed, texts);
        Assert.Contains(s.ToolsStateNotAllowed, texts);
    });

    [Fact]
    public void A_destructive_tool_shows_Destructive_without_a_picker_and_a_governed_server_has_no_pickers() => Sta.Run(() =>
    {
        using (var host = new ToolsHost("drawer-tools"))
        {
            var s = host.Page.Strings;
            // Five tools; four can be raised (the destructive one cannot).
            Assert.Equal(4, host.Visible().OfType<ComboBox>().Count());
            Assert.Contains(s.ToolsClassDestructive, host.Texts());
            Assert.Contains(s.ToolsDestructiveHelp, host.Texts());
            Assert.True(host.Find<ToggleButton>("TrustedSwitch").IsEnabled);
        }
        using (var host = new ToolsHost("drawer-governed"))
        {
            Assert.Empty(host.Visible().OfType<ComboBox>());
            Assert.False(host.Find<ToggleButton>("TrustedSwitch").IsEnabled);
            Assert.Contains(host.Page.Strings.ToolsTrustedByAdmin, host.Texts());
        }
    });

    // ---- Advanced: the plain-http switch and its warning --------------------------------------------------------------

    [Fact]
    public void Advanced_shows_only_where_the_view_model_shows_it() => Sta.Run(() =>
    {
        using (var host = new ToolsHost("drawer"))
        {
            Assert.False(host.Has("AdvancedSection"));
        }
        using (var host = new ToolsHost("drawer-advanced"))
        {
            Assert.True(host.Has("AdvancedSection"));
            Assert.False(host.Has("PlainHttpWarning"));
        }
    });

    [Fact]
    public void The_warning_opens_with_the_focus_on_Confirm_and_Confirm_turns_the_switch_on() => Sta.Run(() =>
    {
        using var host = new ToolsHost("drawer-advanced");
        var toggle = host.Find<ToggleButton>("PlainHttpSwitch");
        // As a click: the switch flips, the view model opens the warning and keeps the switch off until Confirm.
        ((System.Windows.Automation.Provider.IToggleProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(toggle)).Toggle();
        host.Pump();
        Assert.True(host.Page.Details!.IsPlainHttpWarningOpen);
        Assert.False(toggle.IsChecked);
        var confirm = host.Find<Button>("PlainHttpConfirmButton");
        Assert.True(confirm.IsVisible);
        Assert.Same(confirm, host.Focused());
        Assert.Contains(host.Page.Strings.McpPlainHttpWarning, host.Texts());
        Assert.DoesNotContain("plain-http nas True", host.Catalog.Calls);

        confirm.Command!.Execute(null);
        host.Pump();
        Assert.Contains($"plain-http nas True {UiStrings.McpPlainHttpWarningVersion}", host.Catalog.Calls);
        Assert.False(host.Has("PlainHttpWarning"));
        Assert.True(toggle.IsChecked);
    });

    [Fact]
    public void Esc_says_no_to_the_warning_first_then_closes_the_drawer() => Sta.Run(() =>
    {
        using var host = new ToolsHost("drawer-warning");
        host.Pump();
        Assert.True(host.Page.Details!.IsPlainHttpWarningOpen);
        var confirm = host.Find<Button>("PlainHttpConfirmButton");

        host.Press(confirm, Key.Escape);
        Assert.True(host.Page.IsDetailsOpen);
        Assert.False(host.Page.Details!.IsPlainHttpWarningOpen);
        Assert.DoesNotContain(host.Catalog.Calls, c => c.StartsWith("plain-http", StringComparison.Ordinal));
        Assert.False(host.Page.Details.PlainHttpOn);

        host.Press(host.Find<ToggleButton>("PlainHttpSwitch"), Key.Escape);
        Assert.False(host.Page.IsDetailsOpen);
    });

    [Fact]
    public void Cancel_in_the_warning_leaves_the_switch_off() => Sta.Run(() =>
    {
        using var host = new ToolsHost("drawer-warning");
        host.Find<Button>("PlainHttpCancelButton").Command!.Execute(null);
        host.Pump();
        Assert.False(host.Has("PlainHttpWarning"));
        Assert.False(host.Find<ToggleButton>("PlainHttpSwitch").IsChecked);
    });

    // ---- secret values --------------------------------------------------------------------------------------------------

    private static PasswordBox PasswordBoxOf(ToolsHost host, int row) =>
        host.Tree().OfType<PasswordBox>().Where(t => t.Name == "SecretValueBox").ElementAt(row);

    [Fact]
    public void A_typed_secret_reaches_the_row_from_a_password_box_and_is_gone_from_the_box_after_Save() => Sta.Run(() =>
    {
        using var host = new ToolsHost("add-address");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var box = PasswordBoxOf(host, 1);
        box.Password = "s3cret-value";
        host.Pump();
        Assert.Equal("s3cret-value", sheet.HeaderRows[1].Value);
        sheet.HeaderRows[1].Name = "X-Api-Key";

        host.Await(sheet.SaveCommand.ExecuteAsync(null));
        Assert.Null(host.Page.Sheet);
        Assert.Contains("s3cret-value", host.Catalog.SecretValuesSeen);
        Assert.Equal("", box.Password);
        Assert.Equal("", sheet.HeaderRows[1].Value);
    });

    [Fact]
    public void A_typed_secret_is_gone_from_the_box_after_Cancel() => Sta.Run(() =>
    {
        using var host = new ToolsHost("add-address");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var box = PasswordBoxOf(host, 1);
        box.Password = "s3cret-value";
        host.Pump();
        Assert.Equal("s3cret-value", sheet.HeaderRows[1].Value);

        sheet.CancelCommand.Execute(null);
        host.Pump();
        Assert.Null(host.Page.Sheet);
        Assert.Equal("", box.Password);
        Assert.Equal("", sheet.HeaderRows[1].Value);
        Assert.DoesNotContain("s3cret-value", host.Catalog.SecretValuesSeen);
    });

    [Fact]
    public void The_box_carries_the_password_both_ways_and_is_a_PasswordBox_never_a_TextBox() => Sta.Run(() =>
    {
        using var host = new ToolsHost("add-address");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var box = PasswordBoxOf(host, 0);
        Assert.Equal("Bearer 0123456789", box.Password);
        sheet.HeaderRows[0].Value = "changed";
        host.Pump();
        Assert.Equal("changed", box.Password);
        Assert.DoesNotContain(host.Tree().OfType<TextBox>(), t => t.Name == "SecretValueBox");
        Assert.Equal("", PasswordBoxOf(host, 1).Password);
        Assert.Equal("changed", SecretBoxValue(box));
    });

    private static string SecretBoxValue(PasswordBox box) => Fadenstack.UI.Wpf.Behaviors.SecretBox.GetValue(box);

    [Fact]
    public void A_saved_value_shows_dots_and_Replace_and_Replace_gives_an_empty_password_box() => Sta.Run(() =>
    {
        using var host = new ToolsHost("edit-kept");
        var sheet = (ServerSheetViewModel)host.Page.Sheet!;
        var s = host.Page.Strings;
        Assert.Contains(s.ToolsValueSaved, host.Texts());
        var replace = host.Visible().OfType<Button>().Single(b => b.Name == "ReplaceValueButton");
        Assert.Equal(s.ToolsReplaceValue, replace.Content);
        // The kept row has no box; the other row has one.
        Assert.Single(host.Visible().OfType<PasswordBox>(), t => t.Name == "SecretValueBox");

        replace.Command!.Execute(null);
        host.Pump();
        Assert.False(sheet.EnvironmentRows[0].IsKept);
        Assert.Equal(2, host.Visible().OfType<PasswordBox>().Count(t => t.Name == "SecretValueBox"));
    });

    // ---- the sheets -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_scan_shows_its_progress_while_it_runs_and_its_findings_when_it_is_done() => Sta.Run(() =>
    {
        using (var host = new ToolsHost("scan-running"))
        {
            Assert.True(host.Has("ScanProgress"));
            Assert.True(host.Page.Sheet is ScanSheetViewModel { IsScanning: true });
            host.Scene.ScanGate!.TrySetResult(true);
        }
        using (var host = new ToolsHost("scan-done"))
        {
            Assert.False(host.Has("ScanProgress"));
            Assert.True(host.Has("ScanDoneRow"));
            Assert.Equal(4, ((ScanSheetViewModel)host.Page.Sheet!).ChosenCount);
            Assert.Contains(host.Page.Strings.ToolsScanAddMany(4), host.Texts());
        }
    });

    [Fact]
    public void The_cloud_tab_lists_the_governed_tools_read_only() => Sta.Run(() =>
    {
        using var host = new ToolsHost("cloud");
        var s = host.Page.Strings;
        Assert.Contains("search_repositories", host.Texts());
        Assert.Contains(s.ToolsCloudOffByAdmin, host.Texts());
        Assert.Equal(2, host.Find<ItemsControl>("CloudGroups").Items.Count);
        Assert.DoesNotContain(host.Visible().OfType<ToggleButton>(), t => t is not RadioButton && t is not CheckBox);
    });

    [Fact]
    public void The_cards_take_as_many_columns_as_the_width_allows_and_the_drawer_never_wider_than_the_view() => Sta.Run(() =>
    {
        using var host = new ToolsHost("drawer");
        Assert.Equal(1, host.View.CardColumns);
        Assert.Equal(host.View.ActualWidth, host.View.DrawerWidth, 0.5);
        host.Window.Width = 1000;
        host.Pump();
        Assert.Equal(3, host.View.CardColumns);
        Assert.Equal(400, host.View.DrawerWidth);
    });
}

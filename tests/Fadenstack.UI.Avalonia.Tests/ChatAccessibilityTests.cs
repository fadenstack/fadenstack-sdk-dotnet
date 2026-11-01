using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// Design 22, section 7: every button, toggle and menu item of the chat has a name for screen readers, and every icon
/// button a tooltip and an explicit name from the texts. The test walks the chat's visual tree (and its open popups and
/// menus) in its states and fails on one without.
/// </summary>
public class ChatAccessibilityTests
{
    /// <summary>What a screen reader reads: the explicit name, else what the control's automation peer derives (its text).</summary>
    private static string? NameOf(Control control)
    {
        if (AutomationProperties.GetName(control) is { Length: > 0 } name)
        {
            return name;
        }
        if (ControlAutomationPeer.CreatePeerForElement(control).GetName() is { Length: > 0 } derived)
        {
            return derived;
        }
        return control switch
        {
            MenuItem { Header: string { Length: > 0 } header } => header,
            ContentControl { Content: string { Length: > 0 } text } => text,
            _ => null,
        };
    }

    private static IEnumerable<Control> Interactive(Visual root)
    {
        var all = new List<Visual> { root };
        all.AddRange(root.GetVisualDescendants());
        foreach (var popup in root.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child is not null).ToList())
        {
            all.Add(popup.Child!);
            all.AddRange(popup.Child!.GetVisualDescendants());
        }
        return all.OfType<Control>()
            .Where(c => c is Button or ToggleButton or MenuItem && c.IsEffectivelyVisible)
            .Where(c => AutomationProperties.GetAccessibilityView(c) != AccessibilityView.Raw)
            .Distinct();
    }

    private static void AssertNamed(ChatHost host, string state, List<string> failures, int[] counted)
    {
        foreach (var control in Interactive(host.Window))
        {
            counted[0]++;
            var what = $"{state}: {control.GetType().Name} '{string.Join(' ', control.Classes)}'";
            if (string.IsNullOrWhiteSpace(NameOf(control)))
            {
                failures.Add(what + " has no automation name");
            }
            if (control.Classes.Contains("icon"))
            {
                if (ToolTip.GetTip(control) is not string { Length: > 0 })
                {
                    failures.Add(what + " is an icon button without a tooltip");
                }
                if (AutomationProperties.GetName(control) is not { Length: > 0 })
                {
                    failures.Add(what + " is an icon button without an explicit automation name");
                }
            }
        }
    }

    private static async Task Answered(ChatHost host, string prompt, Func<bool> done)
    {
        host.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);
        _ = host.Chat.SendAsync(prompt);
        await host.UntilAsync(done);
        await host.PumpAsync();
    }

    [AvaloniaFact]
    public async Task Every_button_toggle_and_menu_item_of_the_chat_has_a_name_in_every_state()
    {
        var failures = new List<string>();
        var counted = new int[1];

        using (var empty = await ChatHost.OpenAsync(0, openSession: false))
        {
            AssertNamed(empty, "empty", failures, counted);
        }

        using var host = await ChatHost.OpenAsync(3);
        AssertNamed(host, "conversation", failures, counted);

        // A tool line, folded out
        await Answered(host, "Read page 3", () => !host.Chat.IsBusy && host.Chat.Turns[^1].Parts.OfType<ToolCallPart>().Any());
        foreach (var part in host.Chat.Turns[^1].Parts.OfType<ToolCallPart>())
        {
            part.ToggleCommand.Execute(null);
        }
        await host.PumpAsync();
        AssertNamed(host, "tool call", failures, counted);

        // An approval card, and its split button's menu
        await Answered(host, "Add a comment to page 2", () => host.Chat.Turns[^1].Parts.OfType<ApprovalPart>().Any());
        AssertNamed(host, "approval", failures, counted);
        var split = host.View.GetVisualDescendants().OfType<SplitButton>().FirstOrDefault(s => s.IsEffectivelyVisible);
        Assert.NotNull(split);
        split!.Flyout!.ShowAt(split);
        await host.PumpAsync();
        foreach (var item in ((MenuFlyout)split.Flyout).Items.OfType<MenuItem>().Where(i => i.IsVisible))
        {
            counted[0]++;
            if (string.IsNullOrWhiteSpace(NameOf(item)))
            {
                failures.Add("approval menu: MenuItem has no automation name");
            }
        }
        split.Flyout.Hide();
        host.Chat.Turns[^1].Parts.OfType<ApprovalPart>().First().DenyCommand.Execute(null);
        await host.UntilAsync(() => !host.Chat.IsBusy);
        await host.PumpAsync();

        // Chips in the composer
        host.Chat.Attachments.Add(new AttachmentViewModel("Selection", "The current selection", _ => Task.FromResult<MessageAttachment?>(null), isImplicit: true));
        host.Chat.Attachments.Add(new AttachmentViewModel("Report.docx", "document", _ => Task.FromResult<MessageAttachment?>(null)));
        host.Chat.PickedSkills.Add(new SkillOption { Id = "1", Slug = "summarise", Name = "Summarise", Description = "Short summaries" });
        await host.PumpAsync();
        AssertNamed(host, "chips", failures, counted);

        // A failed turn and a problem banner
        typeof(TurnViewModel).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Chat.Turns[^1], new object[] { "The server answered 503.", DateTimeOffset.Now });
        host.Chat.PictureNotRead("photo.heic");
        await host.PumpAsync();
        AssertNamed(host, "errors", failures, counted);

        // The menus: history, tool mode, more (language, About), About
        host.Chat.IsHistoryOpen = true;
        await host.PumpAsync();
        AssertNamed(host, "history", failures, counted);
        host.Chat.IsHistoryOpen = false;
        host.Chat.IsModeMenuOpen = true;
        await host.PumpAsync();
        AssertNamed(host, "mode menu", failures, counted);
        host.Chat.IsModeMenuOpen = false;
        host.Chat.IsReasoningMenuOpen = true;
        await host.PumpAsync();
        AssertNamed(host, "thinking menu", failures, counted);
        host.Chat.IsReasoningMenuOpen = false;
        host.Chat.IsSettingsOpen = true;
        await host.PumpAsync();
        AssertNamed(host, "settings", failures, counted);
        host.Chat.IsSettingsOpen = false;
        host.Chat.OpenAboutCommand.Execute(null);
        await host.PumpAsync();
        AssertNamed(host, "about", failures, counted);

        Assert.True(counted[0] > 60, $"only {counted[0]} controls were checked");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
    }
}

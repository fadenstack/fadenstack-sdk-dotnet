using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The approval card's title has its own row: the class badge is below it, so a narrow panel never breaks the name mid-word.</summary>
public class ApprovalTitleLayoutTests
{
    private static (Window Window, ContentControl Card) Show(ApprovalRequest request, double width)
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var part = (ApprovalPart)typeof(ApprovalPart).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ApprovalRequest), typeof(ApprovalState), typeof(int), typeof(UiStrings) }, null)!
            .Invoke(new object[] { request, ApprovalState.Pending, 200, strings });
        var view = new ChatView();
        var template = view.DataTemplates.OfType<DataTemplate>().First(t => t.DataType == typeof(ApprovalPart));
        var card = new ContentControl { Content = part, ContentTemplate = template };
        // The chat view's own styles (the badge, the icons, the code look) are scoped to it: the card takes them along.
        var styles = view.Styles.ToList();
        view.Styles.Clear();
        card.Styles.AddRange(styles);
        ChatText.SetStrings(card, strings);
        var window = new Window { Width = width, Height = 500, Content = card };
        window.Show();
        return (window, card);
    }

    private static ApprovalRequest Mcp(string name) => new()
    {
        CallId = "call-1", ToolName = name, ToolClass = ToolClass.Unknown, Source = "mcp", Server = "Files", ReadOnlyHint = true,
    };

    [AvaloniaTheory]
    [InlineData("allowed_folders", 340)]
    [InlineData("long_report", 300)]
    [InlineData("allowed_folders", 480)]
    public void The_title_is_alone_on_its_row_above_the_class_badge(string name, double width)
    {
        var (window, card) = Show(Mcp(name), width);

        var title = card.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("subtitle"));
        var badge = card.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("badge"));
        var titleBottom = title.TranslatePoint(new Point(0, title.Bounds.Height), card)!.Value.Y;
        var badgeTop = badge.TranslatePoint(new Point(0, 0), card)!.Value.Y;
        Assert.True(badgeTop >= titleBottom, $"badge at {badgeTop}, title ends at {titleBottom}");
        // No line of the title ends inside the name: the name is never cut up (it fits, or wraps between words).
        var text = string.Concat(title.Inlines!.OfType<global::Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal("Run " + name + "?", text);
        var lines = title.TextLayout.TextLines;
        var start = 0;
        foreach (var line in lines)
        {
            var end = start + line.Length;
            var inside = text.IndexOf(name, StringComparison.Ordinal) is var at && at < end && end < at + name.Length && end > at;
            Assert.False(inside, $"the name is cut at {end} in '{text}'");
            start = end;
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_source_line_is_shown_for_an_MCP_tool_and_hidden_for_another()
    {
        var (window, card) = Show(Mcp("allowed_folders"), 480);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "From Files, an MCP server on this computer");
        window.Close();

        var (other, otherCard) = Show(new ApprovalRequest { CallId = "c", ToolName = "do_it", ToolClass = ToolClass.Unknown }, 480);
        Assert.DoesNotContain(otherCard.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith("From "));
        other.Close();
    }

    /// <summary>A picture of the card at the live panel's width, when FADEN_AUDIT_OUT names a folder.</summary>
    [AvaloniaFact]
    public void Render_the_card_when_asked()
    {
        if (Environment.GetEnvironmentVariable("FADEN_AUDIT_OUT") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        var (window, _) = Show(Mcp("allowed_folders"), 340);
        window.CaptureRenderedFrame()!.Save(Path.Combine(folder, "approval-mcp-340.png"), new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }
}

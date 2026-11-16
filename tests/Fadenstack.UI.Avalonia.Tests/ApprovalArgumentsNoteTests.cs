using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The held call's cut arguments in the Avalonia card: the text, and the note under it.</summary>
public class ApprovalArgumentsNoteTests
{
    private static (Window Window, ContentControl Card) Show(ApprovalRequest request)
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var part = (ApprovalPart)typeof(ApprovalPart).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ApprovalRequest), typeof(ApprovalState), typeof(int), typeof(UiStrings) }, null)!
            .Invoke(new object[] { request, ApprovalState.Pending, 200, strings });
        var view = new ChatView();
        var template = view.DataTemplates.OfType<DataTemplate>().First(t => t.DataType == typeof(ApprovalPart));
        var card = new ContentControl { Content = part, ContentTemplate = template };
        ChatText.SetStrings(card, strings);
        var window = new Window { Width = 480, Height = 400, Content = card };
        window.Show();
        return (window, card);
    }

    private static ApprovalRequest Held(bool shortened) => new()
    {
        ToolName = "mcp.github.comment", ToolClass = ToolClass.Write, Source = "gateway", ArgumentsShortened = shortened,
        Arguments = new Dictionary<string, object?> { ["arguments"] = "{\"ti" },
    };

    [AvaloniaFact]
    public void The_note_stands_under_the_cut_text()
    {
        var (window, card) = Show(Held(shortened: true));

        var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("{\"ti", texts);
        Assert.Contains("The arguments are shortened.", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Complete_arguments_have_no_note()
    {
        var (window, card) = Show(Held(shortened: false));

        Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible), t => t.Text == "The arguments are shortened.");
        window.Close();
    }
}

using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>The held call's cut arguments in the WPF card: the text, and the note under it.</summary>
public class ApprovalArgumentsNoteTests
{
    private static FrameworkElement Card(bool shortened)
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Colors.Light.xaml") });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Chat.xaml") });
        var request = new ApprovalRequest
        {
            ToolName = "mcp.github.comment", ToolClass = ToolClass.Write, Source = "gateway", ArgumentsShortened = shortened,
            Arguments = new Dictionary<string, object?> { ["arguments"] = "{\"ti" },
        };
        var part = (ApprovalPart)typeof(ApprovalPart).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(ApprovalRequest), typeof(ApprovalState), typeof(int), typeof(UiStrings) }, null)!
            .Invoke(new object[] { request, ApprovalState.Pending, 200, UiStrings.For(CultureInfo.GetCultureInfo("en")) });
        var card = (FrameworkElement)((DataTemplate)resources[new DataTemplateKey(typeof(ApprovalPart))]).LoadContent();
        card.Resources.MergedDictionaries.Add(resources);
        card.DataContext = part;
        card.Measure(new Size(480, 1000));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        return card;
    }

    private static IEnumerable<DependencyObject> Visual(DependencyObject root) =>
        new[] { root }.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)).Select(i => VisualTreeHelper.GetChild(root, i)).SelectMany(Visual));

    private static bool Shown(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void The_note_stands_under_the_cut_text() => Sta.Run(() =>
    {
        var texts = Visual(Card(shortened: true)).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ToList();

        Assert.Contains("{\"ti", texts);
        Assert.Contains("The arguments are shortened.", texts);
    });

    [Fact]
    public void Complete_arguments_have_no_note() => Sta.Run(() =>
    {
        Assert.DoesNotContain(Visual(Card(shortened: false)).OfType<TextBlock>().Where(Shown), t => t.Text == "The arguments are shortened.");
    });
}

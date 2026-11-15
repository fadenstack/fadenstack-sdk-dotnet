using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>sdk/51 F08 §3 in the Office-looking skin: "Say why" opens a box above the buttons; Deny sends what was typed.</summary>
public class ApprovalReasonCardTests
{
    private static (ApprovalPart Part, Task<ApprovalDecision> Answer) Part()
    {
        var broker = new ApprovalBroker();
        PendingApproval? pending = null;
        broker.Requested += p => pending = p;
        var answer = broker.RequestAsync(new ApprovalRequest { CallId = "call-1", ToolName = "add_comment", ToolClass = ToolClass.Write }, CancellationToken.None);
        var part = (ApprovalPart)typeof(ApprovalPart).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(PendingApproval), typeof(int), typeof(UiStrings) }, null)!
            .Invoke(new object[] { pending!, 200, UiStrings.For(CultureInfo.GetCultureInfo("en")) });
        return (part, answer);
    }

    private static ResourceDictionary Load()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var all = new ResourceDictionary();
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Colors.Light.xaml") });
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Chat.xaml") });
        return all;
    }

    private static IEnumerable<DependencyObject> Visual(DependencyObject root) =>
        new[] { root }.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)).Select(i => VisualTreeHelper.GetChild(root, i)).SelectMany(Visual));

    private static IEnumerable<DependencyObject> Logical(DependencyObject root) =>
        new[] { root }.Concat(LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Logical));

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

    private static void Layout(FrameworkElement card)
    {
        card.Measure(new Size(480, 1000));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
    }

    private sealed class BindingTrace : TraceListener
    {
        private string _line = "";

        public List<string> Errors { get; } = new();

        public override void Write(string? message) => _line += message;

        public override void WriteLine(string? message)
        {
            Errors.Add(_line + message);
            _line = "";
        }
    }

    [Fact]
    public void Say_why_opens_the_box_and_deny_sends_what_was_typed_without_binding_errors() => Sta.Run(() =>
    {
        var trace = new BindingTrace();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(trace);
        source.Switch.Level = SourceLevels.Error;
        try
        {
            var (part, answer) = Part();
            var resources = Load();
            var card = (FrameworkElement)((DataTemplate)resources[new DataTemplateKey(typeof(ApprovalPart))]).LoadContent();
            card.Resources.MergedDictionaries.Add(resources);
            card.DataContext = part;
            Layout(card);

            var box = Visual(card).OfType<TextBox>().Single();
            Assert.False(Shown(box));
            var link = Logical(card).OfType<Hyperlink>().Single(h => h.Command == part.ToggleReasonCommand);
            Assert.Equal("Say why", ((Run)link.Inlines.First()).Text);

            link.Command!.Execute(null);
            Layout(card);
            Assert.True(Shown(box));
            Assert.Equal(500, box.MaxLength);
            Assert.Equal(3, box.MaxLines);
            Assert.Equal("Your reason for declining", System.Windows.Automation.AutomationProperties.GetName(box));
            Assert.Contains(box.InputBindings.OfType<KeyBinding>(), k => k.Key == Key.Escape && k.Command == part.ToggleReasonCommand);
            Assert.Contains(Visual(card).OfType<TextBlock>().Where(Shown), t => t.Text == "Why not? The assistant sees your reason.");
            var frame = Visual(card).OfType<Border>().Single(b => b.Child is Grid g && g.Children.Contains(box));
            Assert.Equal(new CornerRadius(12), frame.CornerRadius);

            box.Text = "Wrong page.";
            Assert.Equal("Wrong page.", part.DenyReason);
            var deny = Visual(card).OfType<Button>().Single(b => b.Command == part.DenyCommand);
            deny.Command!.Execute(null);
            Layout(card);

            Assert.False(answer.Result.Approved);
            Assert.Equal("Wrong page.", answer.Result.Reason);
            Assert.False(Shown(box)); // closed once decided
            Assert.Contains(Visual(card).OfType<TextBlock>().Where(Shown), t => t.Text == "Your reason: Wrong page.");
            Assert.Empty(trace.Errors);
        }
        finally
        {
            source.Listeners.Remove(trace);
            source.Switch.Level = SourceLevels.Off;
        }
    });
}

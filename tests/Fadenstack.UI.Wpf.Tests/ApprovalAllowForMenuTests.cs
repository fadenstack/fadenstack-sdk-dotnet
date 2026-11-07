using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The approval card's split button in the Office-looking skin: its drop-down lists "Allow for this chat", "Allow always"
/// and "Allow for…", a sub-menu of the card's times, each answering with its own (no second "Allow for…" button).
/// </summary>
public class ApprovalAllowForMenuTests
{
    private static readonly TimeSpan[] Times = { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) };

    private static ApprovalPart Part(bool time) => (ApprovalPart)typeof(ApprovalPart).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ApprovalRequest), typeof(ApprovalState), typeof(int), typeof(UiStrings) }, null)!
        .Invoke(new object[]
        {
            new ApprovalRequest
            {
                CallId = "call-1", ToolName = "add_comment", ToolClass = ToolClass.Write, CanAllowForSession = true, CanAllowAlways = true,
                GrantDurations = time ? Times : Array.Empty<TimeSpan>(),
            },
            ApprovalState.Pending, 200, UiStrings.For(CultureInfo.GetCultureInfo("en")),
        });

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

    private static IEnumerable<DependencyObject> Logical(DependencyObject root) =>
        new[] { root }.Concat(LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Logical));

    /// <summary>The card's drop-down menu, laid out as the root (a ContextMenu cannot have a parent), as it is when shown.</summary>
    private static (FrameworkElement Card, ContextMenu Menu) Card(ResourceDictionary resources, ApprovalPart part)
    {
        var card = (FrameworkElement)((DataTemplate)resources[new DataTemplateKey(typeof(ApprovalPart))]).LoadContent();
        card.Resources.MergedDictionaries.Add(resources);
        card.DataContext = part;
        var menu = Logical(card).OfType<ToggleButton>().Single(t => t.ContextMenu is not null).ContextMenu!;
        menu.Resources.MergedDictionaries.Add(resources);
        menu.DataContext = part; // as the menu takes its placement target's when it opens
        menu.Measure(new Size(400, 400));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        return (card, menu);
    }

    [Fact]
    public void Allow_for_is_a_sub_menu_of_the_cards_times_each_answering_with_its_own() => Sta.Run(() =>
    {
        var part = Part(time: true);
        var (card, menu) = Card(Load(), part);

        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new object[] { "Allow for this chat", "Allow always", "Allow for…" }, items.Select(i => i.Header).ToArray());
        Assert.All(items, i => Assert.Equal(Visibility.Visible, i.Visibility));
        var allowFor = items[2];
        Assert.Equal(Visibility.Visible, allowFor.Visibility);
        Assert.Equal(MenuItemRole.SubmenuHeader, allowFor.Role);

        // The times: laid out as when the sub-menu opens, each item bound to the card's command and its own time.
        allowFor.ApplyTemplate();
        var times = (FrameworkElement)((Popup)allowFor.Template.FindName("PART_Popup", allowFor)).Child;
        times.Measure(new Size(400, 400));
        times.Arrange(new Rect(times.DesiredSize));
        times.UpdateLayout();
        var subs = Enumerable.Range(0, Times.Length).Select(i => Assert.IsType<MenuItem>(allowFor.ItemContainerGenerator.ContainerFromIndex(i))).ToList();
        Assert.Equal(new object[] { "15 min", "30 min", "1 h" }, subs.Select(s => s.Header).ToArray());
        Assert.All(subs, s => Assert.Same(part.AllowForTimeCommand, s.Command));
        Assert.Equal(part.Durations, subs.Select(s => Assert.IsType<DurationOption>(s.CommandParameter)));
        Assert.All(subs, s => Assert.True(s.Command.CanExecute(s.CommandParameter)));

        // One way to allow for a time: no separate "Allow for…" button on the card.
        Assert.DoesNotContain(Logical(card).OfType<ButtonBase>(), b => b.Content as string == "Allow for…");
    });

    [Fact]
    public void Without_times_allow_for_is_not_offered() => Sta.Run(() =>
    {
        var part = Part(time: false);
        var (_, menu) = Card(Load(), part);

        var allowFor = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Allow for…"));
        Assert.Equal(Visibility.Collapsed, allowFor.Visibility);
        Assert.False(part.CanAllowForTime);
    });
}

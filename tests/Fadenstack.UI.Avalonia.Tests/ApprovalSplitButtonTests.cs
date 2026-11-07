using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>The approval card's allow choices are one split button: "Allow" once, the others in its drop-down.</summary>
public class ApprovalSplitButtonTests
{
    private static (Window Window, ContentControl Card, ApprovalPart Part, UiStrings Strings) Show(ApprovalRequest request)
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
        return (window, card, part, strings);
    }

    private static readonly TimeSpan[] Times = { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) };

    private static ApprovalRequest Request(bool chat, bool always, bool time = false) => new()
    {
        CallId = "call-1", ToolName = "add_comment", ToolClass = ToolClass.Write,
        CanAllowForSession = chat, CanAllowAlways = always, GrantDurations = time ? Times : Array.Empty<TimeSpan>(),
    };

    [AvaloniaFact]
    public void The_choices_are_one_split_button_whose_main_part_allows_once_and_whose_drop_down_lists_only_the_offered_ones()
    {
        var (window, card, part, strings) = Show(Request(chat: true, always: true, time: true));

        var split = card.GetVisualDescendants().OfType<SplitButton>().Single();
        Assert.True(split.IsEffectivelyVisible);
        Assert.Equal(strings.Allow, split.Content);
        Assert.Same(part.AllowCommand, split.Command);
        var flyout = Assert.IsType<MenuFlyout>(split.Flyout);
        flyout.ShowAt(split); // the items take their bindings when the menu is shown
        Dispatcher.UIThread.RunJobs();
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { strings.AllowForChat, strings.AllowAlways, strings.AllowFor }, items.Select(i => i.Header));
        Assert.All(items, i => Assert.True(i.IsVisible));
        Assert.Equal(new object?[] { part.AllowForSessionCommand, part.AllowAlwaysCommand, null }, items.Select(i => (object?)i.Command).ToArray());
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<Button>().Where(b => b.GetVisualAncestors().OfType<SplitButton>().Count() == 0),
            b => b.Content as string == strings.Allow && b.IsEffectivelyVisible); // no second "Allow"
        window.Close();
    }

    [AvaloniaFact]
    public void Allow_for_is_a_sub_menu_of_the_cards_times_each_answering_with_its_own()
    {
        var (window, card, part, strings) = Show(Request(chat: true, always: true, time: true));

        var split = card.GetVisualDescendants().OfType<SplitButton>().Single();
        split.Flyout!.ShowAt(split);
        Dispatcher.UIThread.RunJobs();
        var allowFor = ((MenuFlyout)split.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, strings.AllowFor));
        allowFor.Open();
        Dispatcher.UIThread.RunJobs();
        var times = Enumerable.Range(0, Times.Length).Select(i => Assert.IsType<MenuItem>(allowFor.ContainerFromIndex(i))).ToList();
        Assert.Equal(new[] { "15 min", "30 min", "1 h" }, times.Select(t => t.Header));
        Assert.All(times, t => Assert.Same(part.AllowForTimeCommand, t.Command));
        Assert.Equal(part.Durations, times.Select(t => Assert.IsType<DurationOption>(t.CommandParameter)));
        Assert.All(times, t => Assert.True(t.Command!.CanExecute(t.CommandParameter)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_choice_the_request_does_not_offer_is_not_in_the_drop_down()
    {
        var (window, card, part, _) = Show(Request(chat: true, always: false));

        var split = card.GetVisualDescendants().OfType<SplitButton>().Single();
        split.Flyout!.ShowAt(split);
        Dispatcher.UIThread.RunJobs();
        var items = ((MenuFlyout)split.Flyout!).Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { true, false, false }, items.Select(i => i.IsVisible));
        Assert.False(part.AllowAlwaysCommand.CanExecute(null));
        Assert.False(part.CanAllowForTime);
        window.Close();
    }

    [AvaloniaFact]
    public void A_time_alone_makes_the_split_button()
    {
        var (window, card, _, strings) = Show(Request(chat: false, always: false, time: true));

        var split = card.GetVisualDescendants().OfType<SplitButton>().Single();
        Assert.True(split.IsEffectivelyVisible);
        split.Flyout!.ShowAt(split);
        Dispatcher.UIThread.RunJobs();
        var visible = ((MenuFlyout)split.Flyout!).Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
        Assert.Equal(strings.AllowFor, Assert.Single(visible).Header);
        window.Close();
    }

    [AvaloniaFact]
    public void With_nothing_to_choose_from_the_card_has_a_plain_allow_button_and_no_split_button()
    {
        var (window, card, part, strings) = Show(Request(chat: false, always: false));

        Assert.DoesNotContain(card.GetVisualDescendants().OfType<SplitButton>(), s => s.IsEffectivelyVisible);
        var allow = card.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == strings.Allow && b.IsEffectivelyVisible);
        Assert.Same(part.AllowCommand, allow.Command);
        Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => b.Content as string == strings.Deny && b.IsEffectivelyVisible);
        window.Close();
    }
}

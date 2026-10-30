using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;
using Fadenstack.Samples.Wpf;

namespace Fadenstack.UI.Avalonia.Tests;

public class ChatViewTests
{
    [AvaloniaFact]
    public async Task A_thousand_turns_open_with_only_the_latest_page_and_a_handful_of_realized_containers()
    {
        using var host = await ChatHost.OpenAsync(1000);

        Assert.Equal(30, host.Chat.Turns.Count);
        Assert.True(host.Chat.HasEarlierTurns);
        Assert.InRange(host.Realized, 1, 30);
        Assert.True(host.AtEnd, "a session opens at its newest turn");
        Assert.True(StickToBottom.GetIsFollowing(host.Scroller));
    }

    [AvaloniaFact]
    public async Task Scrolling_up_releases_the_end_and_the_button_brings_it_back()
    {
        using var host = await ChatHost.OpenAsync(300);
        var viewer = host.Scroller;

        // The reader's wheel: the scroll that follows it is theirs.
        host.Window.MouseWheel(new Point(100, 300), new Vector(0, 3));
        viewer.Offset = new Vector(0, viewer.Offset.Y - 3 * viewer.Viewport.Height);
        await host.PumpAsync();
        Assert.False(StickToBottom.GetIsFollowing(viewer));

        // New content while the reader is up there does not move them.
        var before = viewer.Offset.Y;
        await host.Chat.SendAsync("Read page 3");
        await host.PumpAsync();
        Assert.Equal(before, viewer.Offset.Y, 1);
        Assert.False(host.AtEnd);

        StickToBottom.ScrollToNewestCommand.Execute(viewer);
        await host.PumpAsync();
        Assert.True(host.AtEnd);
        Assert.True(StickToBottom.GetIsFollowing(viewer));
    }

    [AvaloniaFact]
    public async Task A_streaming_answer_keeps_the_end_in_view_while_the_reader_is_at_the_bottom()
    {
        using var host = await ChatHost.OpenAsync(300);
        host.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);

        var sending = host.Chat.SendAsync(HarnessGateway.StreamLong + " answer");
        while (!sending.IsCompleted)
        {
            await host.PumpAsync(1);
        }
        await sending;
        await host.PumpAsync();

        Assert.True(host.AtEnd, $"offset {host.Scroller.Offset.Y}, viewport {host.Scroller.Viewport.Height}, extent {host.Scroller.Extent.Height}");
        Assert.True(StickToBottom.GetIsFollowing(host.Scroller));
    }

    [AvaloniaFact]
    public async Task A_streaming_answer_taller_than_the_window_realizes_no_other_turn_again()
    {
        using var host = await ChatHost.OpenAsync(300);
        host.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);
        var prepared = 0;
        host.TurnList.ContainerPrepared += (_, _) => prepared++;

        var sending = host.Chat.SendAsync(HarnessGateway.StreamLong + " answer");
        while (!sending.IsCompleted)
        {
            await host.PumpAsync(1);
        }
        await sending;
        await host.PumpAsync();

        // The new turn, and at most a neighbour or two the end brings into the cache; not one container per streamed word.
        Assert.InRange(prepared, 1, 6);
        Assert.True(host.AtEnd);
    }

    [AvaloniaFact]
    public async Task Heights_measured_while_scrolling_do_not_move_what_the_reader_sees()
    {
        using var host = await ChatHost.OpenAsync(300);
        var viewer = host.Scroller;
        StickToBottom.Release(viewer);
        viewer.Offset = new Vector(0, viewer.Offset.Y - 2 * viewer.Viewport.Height);
        await host.PumpAsync();
        var seen = host.TurnList.ItemsPanelRoot!.Children
            .Select(c => (Turn: (TurnViewModel)host.TurnList.ItemFromContainer(c)!, Top: c.TranslatePoint(default, viewer)!.Value.Y))
            .First(t => t.Top + 1 > 0);

        // Scroll further up: the turns that come into the cache are measured (their real height differs from the
        // estimate), and the turn the reader is looking at stays where it is.
        for (var i = 0; i < 6; i++)
        {
            viewer.Offset = new Vector(0, viewer.Offset.Y - 40);
            await host.PumpAsync(2);
            var now = host.TopOf(seen.Turn);
            Assert.NotNull(now);
            Assert.Equal(seen.Top + 40 * (i + 1), now!.Value, 2);
        }
        Assert.InRange(host.Realized, 1, 30);
    }

    [AvaloniaFact]
    public async Task Older_turns_load_before_the_top_is_reached_and_the_visible_turn_stays_put()
    {
        using var host = await ChatHost.OpenAsync(300);
        var viewer = host.Scroller;
        StickToBottom.Release(viewer);
        // Up to within a screen of the top, never to 0.
        viewer.Offset = new Vector(0, viewer.Viewport.Height * 3);
        await host.PumpAsync();
        var shown = host.Chat.Turns.Count;
        viewer.Offset = new Vector(0, viewer.Viewport.Height * 0.9);
        host.Window.UpdateLayout();

        // The turn at the top of the viewport, and where it is, before the page arrives.
        var first = host.TurnList.ItemsPanelRoot!.Children
            .Select(c => (Turn: (TurnViewModel)host.TurnList.ItemFromContainer(c)!, Top: c.TranslatePoint(default, viewer)!.Value.Y))
            .Where(t => t.Top + 1 > 0)
            .OrderBy(t => t.Top)
            .First();

        await host.UntilAsync(() => host.Chat.Turns.Count > shown);
        await host.PumpAsync();

        Assert.Equal(shown + 30, host.Chat.Turns.Count);
        Assert.NotNull(host.TopOf(first.Turn));
        Assert.Equal(first.Top, host.TopOf(first.Turn)!.Value, 1);
        Assert.True(viewer.Offset.Y > 0, "the load must not wait for the top");
    }

    [AvaloniaFact]
    public async Task Turns_far_from_the_viewport_drop_their_blocks_and_get_them_back_when_they_return()
    {
        using var host = await ChatHost.OpenAsync(400);
        var viewer = host.Scroller;
        StickToBottom.Release(viewer);
        // Load everything by going up.
        for (var i = 0; i < 200 && host.Chat.HasEarlierTurns; i++)
        {
            viewer.Offset = new Vector(0, viewer.Viewport.Height * 0.5);
            await host.PumpAsync(3);
        }
        Assert.Equal(400, host.Chat.Turns.Count);

        StickToBottom.ScrollToNewest(viewer);
        await host.PumpAsync();
        FarContent.Sweep(viewer);

        var oldest = host.Chat.Turns[0];
        var newest = host.Chat.Turns[^1];
        Assert.All(oldest.Parts.OfType<MarkdownPart>(), p => Assert.False(p.IsLoaded));
        Assert.All(newest.Parts.OfType<MarkdownPart>(), p => Assert.True(p.IsLoaded));
        Assert.All(oldest.Parts.OfType<MarkdownPart>(), p => Assert.NotEmpty(p.Text));

        StickToBottom.Release(viewer);
        viewer.Offset = new Vector(0, 0);
        await host.PumpAsync();
        Assert.All(oldest.Parts.OfType<MarkdownPart>(), p => Assert.True(p.IsLoaded));
    }

    [AvaloniaFact]
    public async Task Every_kind_of_markdown_block_and_part_draws()
    {
        const string answer = "# Heading\n\nA paragraph with **bold**, *italic*, `code` and a [link](https://example.com).\n\n- one\n- two\n  - nested\n\n"
            + "1. first\n2. second\n\n> A quote.\n\n| A | B |\n|:--|--:|\n| 1 | 2 |\n\n---\n\n```csharp\nvar x = 1;\n```\n";
        using var host = await ChatHost.OpenAsync(async store =>
        {
            await SyntheticSession.StartAsync(store, DemoGateway.Agent, HarnessChat.SessionId);
            await SyntheticSession.AppendTurnAsync(store, DemoGateway.Agent, HarnessChat.SessionId, "t0", "Show everything", answer);
        });

        var tree = host.View.GetVisualDescendants().ToList();
        var texts = tree.OfType<TextBlock>().ToList();

        Assert.Contains(texts, t => t.Text == "var x = 1;");
        Assert.Contains(tree.OfType<SelectableTextBlock>(), t => t.Inlines?.Count > 0 && t.Inlines.OfType<Run>().Any(r => r.Classes.Contains("link")));
        Assert.Contains(tree.OfType<SelectableTextBlock>(), t => t.Inlines!.OfType<Run>().Any(r => r.Classes.Contains("code")));
        Assert.Contains(tree.OfType<Border>(), b => b.Classes.Contains("table-head"));
        Assert.Contains(tree.OfType<Border>(), b => b.Classes.Contains("quote"));
        Assert.Contains(tree.OfType<Border>(), b => b.Classes.Contains("rule"));
        Assert.Contains(texts, t => t.Text == "nested" || t.Inlines?.Any(i => (i as Run)?.Text == "nested") == true);
    }

    [AvaloniaFact]
    public async Task An_approval_card_is_answered_in_place_and_its_texts_are_the_chats()
    {
        using var host = await ChatHost.OpenAsync(2);
        host.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);

        var sending = host.Chat.SendAsync("Delete page 2");
        ApprovalPart? card = null;
        await host.UntilAsync(() => (card = host.Chat.Turns[^1].Parts.OfType<ApprovalPart>().FirstOrDefault()) is not null);
        await host.PumpAsync();

        var allow = host.View.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == host.Chat.Strings.Allow);
        var deny = host.View.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == host.Chat.Strings.Deny);
        Assert.True(allow.IsEffectivelyVisible);
        Assert.Same(card!.DenyCommand, deny.Command);

        deny.Command!.Execute(null);
        await sending;
        await host.PumpAsync();

        Assert.Equal(ApprovalState.Denied, card.State);
        Assert.DoesNotContain(host.View.GetVisualDescendants().OfType<Button>(), b => b.Content as string == host.Chat.Strings.Allow && b.IsEffectivelyVisible);
        Assert.Contains(host.View.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == card.StateText);
    }

    [AvaloniaFact]
    public async Task Light_and_dark_are_theme_variants_of_the_same_names()
    {
        using var host = await ChatHost.OpenAsync(3);

        Assert.True(host.View.TryFindResource("Fadenstack.Background", ThemeVariant.Light, out var light));
        Assert.True(host.View.TryFindResource("Fadenstack.Background", ThemeVariant.Dark, out var dark));
        Assert.Equal(Color.Parse("#F5F7FB"), ((ISolidColorBrush)light!).Color);
        Assert.Equal(Color.Parse("#0A0E1A"), ((ISolidColorBrush)dark!).Color);

        host.View.Appearance = ChatTheme.Dark;
        await host.PumpAsync();
        var scope = host.View.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Equal(ThemeVariant.Dark, scope.ActualThemeVariant);
    }
}

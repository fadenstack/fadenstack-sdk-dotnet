using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// <c>ChatView.AgentPicture</c> (desktop-agent/25 section 2): the host's picture of the agent over the empty chat, in the
/// place and at the size of the skin's own icon, and in the visual tree only while the empty chat shows. Without it the
/// empty chat is as before.
/// </summary>
public class AgentPictureTests
{
    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Point Centre(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), relativeTo)!.Value;

    private static void AssertNear(Point expected, Point actual) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 0.5 && Math.Abs(expected.Y - actual.Y) <= 0.5, $"expected about {expected}, got {actual}");

    private static bool InTree(Visual visual) => TopLevel.GetTopLevel(visual) is not null;

    private static void SetSignInRequired(AgentChatViewModel chat, bool value) =>
        typeof(AgentChatViewModel).GetProperty(nameof(AgentChatViewModel.SignInRequired))!.GetSetMethod(true)!.Invoke(chat, [value]);

    /// <summary>A picture that counts how often it joins and leaves the visual tree, as a live avatar would start and stop.</summary>
    private sealed class Probe : Control
    {
        public int Attached { get; private set; }

        public int Detached { get; private set; }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Attached++;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Detached++;
            base.OnDetachedFromVisualTree(e);
        }
    }

    [AvaloniaFact]
    public async Task Without_a_picture_the_empty_chat_shows_the_skins_icon_as_before()
    {
        using var host = await ChatHost.OpenAsync(0, openSession: false);
        Assert.True(host.Chat.IsEmpty);
        Assert.Null(host.View.AgentPicture);

        var icon = Named<Border>(host.View, "DefaultAgentPicture");
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal(new Size(96, 96), icon.Bounds.Size);
        var glyph = icon.GetVisualDescendants().OfType<FsIcon>().Single();
        Assert.Same(Application.Current!.FindResource("Icon.SmartToy"), glyph.Data);
        Assert.Equal(48, glyph.Size);

        var slot = Named<ContentPresenter>(host.View, "AgentPictureSlot");
        Assert.False(slot.IsVisible);
        Assert.Null(slot.Content);
    }

    [AvaloniaFact]
    public async Task A_host_picture_takes_the_icons_place_and_size()
    {
        using var host = await ChatHost.OpenAsync(0, openSession: false);
        var icon = Named<Border>(host.View, "DefaultAgentPicture");
        var title = host.View.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("title") && t.IsEffectivelyVisible);
        var iconCentre = Centre(icon, host.View);
        var titleTop = title.TranslatePoint(default, host.View)!.Value.Y;

        // A control without a size of its own fills the 96 square.
        var picture = new Border { Background = Brushes.Red };
        host.View.AgentPicture = picture;
        await host.PumpAsync();
        Assert.False(icon.IsVisible);
        Assert.True(picture.IsEffectivelyVisible);
        Assert.Equal(new Size(96, 96), picture.Bounds.Size);
        AssertNear(iconCentre, Centre(picture, host.View));
        Assert.Equal(titleTop, title.TranslatePoint(default, host.View)!.Value.Y, 1);

        // One with its own size sits in the middle of the square; the one before leaves the tree.
        var small = new Border { Width = 40, Height = 40, Background = Brushes.Blue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.View.AgentPicture = small;
        await host.PumpAsync();
        Assert.False(InTree(picture));
        Assert.Equal(new Size(40, 40), small.Bounds.Size);
        AssertNear(iconCentre, Centre(small, host.View));

        // Taken away again: the skin's icon is back.
        host.View.AgentPicture = null;
        await host.PumpAsync();
        Assert.False(InTree(small));
        Assert.True(icon.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_host_picture_is_in_the_tree_only_while_the_empty_chat_shows()
    {
        using var host = await ChatHost.OpenAsync(3, openSession: false);
        var picture = new Probe();
        host.View.AgentPicture = picture;
        await host.PumpAsync();
        Assert.True(host.Chat.IsEmpty);
        Assert.True(InTree(picture));
        Assert.Equal(1, picture.Attached);

        // A conversation with turns: the empty chat is gone, and with it the picture.
        await host.Chat.OpenSessionAsync(HarnessChat.SessionId);
        await host.UntilAsync(() => !host.Chat.IsEmpty);
        await host.PumpAsync();
        Assert.False(InTree(picture));
        Assert.Equal(1, picture.Detached);

        // A new chat is empty: the picture comes back.
        host.Chat.NewChatCommand.Execute(null);
        await host.UntilAsync(() => host.Chat.IsEmpty);
        await host.PumpAsync();
        Assert.True(InTree(picture));
        Assert.Equal(2, picture.Attached);

        // The chat asks for a sign-in (the host's sign-in shows instead): the picture leaves the tree again.
        SetSignInRequired(host.Chat, true);
        await host.PumpAsync();
        Assert.False(InTree(picture));
        Assert.Equal(2, picture.Detached);
        SetSignInRequired(host.Chat, false);
        await host.PumpAsync();
        Assert.True(InTree(picture));
    }

    [AvaloniaFact]
    public async Task The_picture_follows_the_view_to_another_chat()
    {
        using var empty = await ChatHost.OpenAsync(0, openSession: false);
        using var full = await ChatHost.OpenAsync(3);
        var picture = new Probe();
        full.View.AgentPicture = picture;
        await full.PumpAsync();
        Assert.False(InTree(picture));

        // The same view shows another chat (a host switching sessions): the picture follows what that chat shows.
        full.View.DataContext = empty.Chat;
        await full.PumpAsync();
        Assert.True(InTree(picture));
        full.View.DataContext = full.Chat;
        await full.PumpAsync();
        Assert.False(InTree(picture));
    }
}

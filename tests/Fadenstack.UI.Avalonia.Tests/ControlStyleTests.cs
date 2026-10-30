using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Themes;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The skin's control styles (Themes/Controls.axaml, design 22 section 5), measured: sizes, radii, the icon in the middle
/// of its button, no grey disabled fill, the open split button, the focus ring, tooltips, scroll bars.
/// </summary>
public class ControlStyleTests
{
    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value), key);
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color Over(Color top, Color back)
    {
        var a = top.A / 255.0;
        return Color.FromRgb((byte)Math.Round(top.R * a + back.R * (1 - a)), (byte)Math.Round(top.G * a + back.G * (1 - a)), (byte)Math.Round(top.B * a + back.B * (1 - a)));
    }

    private static int Distance((byte R, byte G, byte B) p, Color c) => Math.Abs(p.R - c.R) + Math.Abs(p.G - c.G) + Math.Abs(p.B - c.B);

    private static Button IconButton(FsIcon icon, params string[] classes)
    {
        var button = new Button { Content = icon };
        button.Classes.Add("icon");
        foreach (var c in classes.Where(c => c.Length > 0))
        {
            button.Classes.Add(c);
        }
        return button;
    }

    /// <summary>
    /// Shows a control on the white surface, 4 px in from the window's corner, and captures it. The window stays open (a
    /// closed window's controls lose the application's styles); the caller closes it.
    /// </summary>
    private static (Window Window, IconTests.Frame Frame) Show(Control control, double scaling, Action<Control>? after = null)
    {
        var window = new Window
        {
            Content = new Border { Background = Brushes.White, Padding = new Thickness(4), Child = control },
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.White,
        };
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        after?.Invoke(control);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, new IconTests.Frame(window.CaptureRenderedFrame()!));
    }

    [AvaloniaTheory]
    [InlineData("large", 36.0, 20.0)]
    [InlineData("normal", 32.0, 18.0)]
    [InlineData("small", 28.0, 16.0)]
    [InlineData("large primary", 36.0, 20.0)]
    [InlineData("small primary", 28.0, 16.0)]
    public void Icon_buttons_are_round_and_sized_by_class_with_the_icon_size_to_match(string size, double edge, double iconSize)
    {
        FadenstackControls.EnsureLoaded();
        var icon = new FsIcon { Data = IconTests.Icon("Add") };
        var button = IconButton(icon, size.Split(' '));
        var (window, _) = Show(button, 1);
        Assert.Equal(new Size(edge, edge), button.Bounds.Size);
        Assert.Equal(iconSize, icon.Size);
        Assert.Equal(new Size(iconSize, iconSize), icon.Bounds.Size);
        Assert.Equal(default, button.Padding);
        Assert.True(button.CornerRadius.TopLeft >= edge / 2, "round");
        window.Close();
    }

    public static IEnumerable<object[]> Variants() =>
        from scaling in new[] { 1.0, 2.0 }
        from variant in new[] { "normal", "large", "small", "hover", "pressed", "checked", "primary", "primary-disabled" }
        select new object[] { scaling, variant };

    /// <summary>
    /// The icon's ink is where its 24 box puts it, in the middle of the button, within half a pixel: a full-box square
    /// (its ink is the box) and the chat's real glyphs (their ink centre is the box's centre moved by the glyph's own offset).
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void The_icon_sits_in_the_middle_of_its_button_within_half_a_pixel(double scaling, string variant)
    {
        FadenstackControls.EnsureLoaded();
        var glyphs = new[] { "Square", "Add", "Close", "History", "MoreVert", "ContentCopy", "Replay", "Send", "Stop", "AttachFile", "KeyboardArrowDown", "Shield" };
        foreach (var name in glyphs)
        {
            var geometry = name == "Square" ? StreamGeometry.Parse("M0 0H24V24H0Z") : IconTests.Icon(name);
            var icon = new FsIcon { Data = geometry };
            Control button = variant switch
            {
                "checked" => new ToggleButton { Content = icon, IsChecked = true, Classes = { "icon" } },
                "primary" => IconButton(icon, "large", "primary"),
                "primary-disabled" => IconButton(icon, "large", "primary"),
                _ => IconButton(icon, variant is "hover" or "pressed" ? "normal" : variant),
            };
            if (variant == "hover")
            {
                ((IPseudoClasses)button.Classes).Set(":pointerover", true);
            }
            if (variant == "pressed")
            {
                ((IPseudoClasses)button.Classes).Set(":pointerover", true);
                ((IPseudoClasses)button.Classes).Set(":pressed", true);
            }
            if (variant == "primary-disabled")
            {
                button.IsEnabled = false;
            }
            var (window, frame) = Show(button, scaling);
            var edge = button.Bounds.Width;
            var size = icon.Size;
            window.Close();
            Assert.Equal(edge, button.Bounds.Height);
            // The button's own fill, at mid height just inside its left edge (inside the circle, outside the icon box).
            var refPixel = frame.Pixel((int)Math.Round((4 + 3) * scaling), (int)Math.Round((4 + edge / 2) * scaling));
            var reference = Color.FromRgb(refPixel.R, refPixel.G, refPixel.B);
            var box = new Rect(4 + (edge - size) / 2 - 1, 4 + (edge - size) / 2 - 1, size + 2, size + 2);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            var most = 0;
            for (var y = (int)(box.Y * scaling); y < (int)Math.Ceiling(box.Bottom * scaling); y++)
            {
                for (var x = (int)(box.X * scaling); x < (int)Math.Ceiling(box.Right * scaling); x++)
                {
                    most = Math.Max(most, Distance(frame.Pixel(x, y), reference));
                }
            }
            Assert.True(most > 30, $"{name} {variant} at {scaling}x: no ink");
            for (var y = (int)(box.Y * scaling); y < (int)Math.Ceiling(box.Bottom * scaling); y++)
            {
                for (var x = (int)(box.X * scaling); x < (int)Math.Ceiling(box.Right * scaling); x++)
                {
                    // 15 % of the ink's own strength, so the thin tips of arrows and chevrons count (as in IconTests).
                    if (Distance(frame.Pixel(x, y), reference) * 100 >= most * 15)
                    {
                        x0 = Math.Min(x0, x);
                        y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x);
                        y1 = Math.Max(y1, y);
                    }
                }
            }
            var ink = new Point((x0 + x1 + 1) / 2.0 / scaling, (y0 + y1 + 1) / 2.0 / scaling);
            var s = size / 24;
            var expected = new Point(4 + edge / 2 + (geometry.Bounds.Center.X - 12) * s, 4 + edge / 2 + (geometry.Bounds.Center.Y - 12) * s);
            Assert.True(Math.Abs(ink.X - expected.X) <= 0.5 && Math.Abs(ink.Y - expected.Y) <= 0.5,
                $"{name} {variant} at {scaling}x: ink centre {ink} vs {expected}");
        }
    }

    [AvaloniaFact]
    public void Disabled_buttons_never_fill_grey()
    {
        FadenstackControls.EnsureLoaded();
        var white = Colors.White;
        var hover = Over(Token(FadenstackKeys.Hover), white);

        // An icon button: no fill at all, the icon faded.
        var plain = IconButton(new FsIcon { Data = IconTests.Icon("Add") });
        plain.IsEnabled = false;
        var (window, frame) = Show(plain, 1);
        window.Close();
        Assert.True(Distance(frame.Pixel(4 + 4, 4 + 16), white) <= 3, "a disabled icon button has no fill");

        // The primary icon button: the Hover tint, not grey.
        var send = IconButton(new FsIcon { Data = IconTests.Icon("Send") }, "large", "primary");
        send.IsEnabled = false;
        (window, frame) = Show(send, 1);
        window.Close();
        Assert.True(Distance(frame.Pixel(4 + 4, 4 + 18), hover) <= 6, $"primary icon button: {frame.Pixel(8, 22)} vs Hover {hover}");

        // Text buttons: primary the Hover tint, secondary its surface, quiet nothing.
        foreach (var (cls, fill) in new[] { ("primary", hover), ("secondary", white), ("quiet", white) })
        {
            var button = new Button { Content = "Allow", IsEnabled = false, Classes = { cls } };
            (window, frame) = Show(button, 1);
            window.Close();
            var at = frame.Pixel(4 + 6, 4 + 18);
            Assert.True(Distance(at, fill) <= 6, $"{cls}: {at} vs {fill}");
        }
    }

    [AvaloniaFact]
    public void Text_buttons_are_control_height_with_the_one_radius_and_16_aside()
    {
        FadenstackControls.EnsureLoaded();
        foreach (var cls in new[] { "primary", "secondary", "quiet" })
        {
            var button = new Button { Content = "Allow", Classes = { cls } };
            var (window, _) = Show(button, 1);
            Assert.Equal(36, button.Bounds.Height);
            Assert.Equal(new CornerRadius(12), button.CornerRadius);
            Assert.Equal(cls == "quiet" ? 8 : 16, button.Padding.Left);
            Assert.Equal(FontWeight.SemiBold, button.FontWeight);
            window.Close();
        }
        var split = new SplitButton { Content = "Allow", Classes = { "primary" }, Flyout = new MenuFlyout() };
        var (splitWindow, _) = Show(split, 1);
        Assert.Equal(36, split.Bounds.Height);
        Assert.Equal(new CornerRadius(12), split.CornerRadius);
        var arrow = split.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_SecondaryButton");
        Assert.Equal(new Size(36, 36), arrow.Bounds.Size);
        splitWindow.Close();
    }

    [AvaloniaFact]
    public void An_open_split_button_shows_its_arrow_part_pressed_and_its_main_part_unchanged()
    {
        FadenstackControls.EnsureLoaded();
        var split = new SplitButton { Content = "Allow", Classes = { "primary" }, Flyout = new MenuFlyout() };
        // Set once the template is applied: the split button sets the pseudo-class from its flyout when it applies one.
        var (window, frame) = Show(split, 1, c => ((IPseudoClasses)c.Classes).Set(":flyout-open", true));
        window.Close();
        var width = split.Bounds.Width;
        Assert.True(Distance(frame.Pixel(4 + 4, 4 + 18), Token(FadenstackKeys.Accent)) <= 3, "the main part keeps the accent");
        Assert.True(Distance(frame.Pixel((int)(4 + width - 4), 4 + 18), Token(FadenstackKeys.AccentHover)) <= 3, "the arrow part is pressed");
    }

    [AvaloniaFact]
    public void Keyboard_focus_draws_one_ring_2_px_outside_that_follows_the_corners()
    {
        FadenstackControls.EnsureLoaded();
        var round = IconButton(new FsIcon { Data = IconTests.Icon("Add") });
        var text = new Button { Content = "Allow", Classes = { "primary" } };
        var box = new TextBox { Width = 200 };
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children = { round, text, box } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var (control, radius) in new (Control, double)[] { (round, 999), (text, 12) })
        {
            control.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var ring = Assert.Single(window.GetVisualDescendants().OfType<FocusRing>());
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            Assert.Equal(new Thickness(-4), ring.Margin);
            Assert.Equal(radius + 4, ring.CornerRadius.TopLeft);
            Assert.Equal(Token(FadenstackKeys.Focus), ((ISolidColorBrush)ring.BorderBrush!).Color);
            Assert.Contains(":focus-visible", control.Classes);
        }

        // A text box shows focus by its own border, not a ring.
        box.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Empty(window.GetVisualDescendants().OfType<FocusRing>());
        var border = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal(new Thickness(2), border.BorderThickness);
        Assert.Equal(Token(FadenstackKeys.Focus), ((ISolidColorBrush)border.BorderBrush!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Tooltips_are_inverse_12_px_with_radius_8_and_no_shadow()
    {
        FadenstackControls.EnsureLoaded();
        var tip = new ToolTip { Content = "New chat (Ctrl+N)" };
        ((IPseudoClasses)tip.Classes).Set(":open", true);
        var (window, frame) = Show(tip, 1);
        Assert.Equal(Token(FadenstackKeys.Inverse), ((ISolidColorBrush)tip.Background!).Color);
        Assert.Equal(Token(FadenstackKeys.InverseForeground), ((ISolidColorBrush)tip.Foreground!).Color);
        Assert.Equal(new CornerRadius(8), tip.CornerRadius);
        Assert.Equal(12, tip.FontSize);
        Assert.Equal(new Thickness(8, 6), tip.Padding);
        Assert.True(Distance(frame.Pixel(4 + 3, 4 + (int)(tip.Bounds.Height / 2)), Token(FadenstackKeys.Inverse)) <= 3, "the inverse fill is drawn");
        window.Close();
    }

    [AvaloniaFact]
    public void Scroll_bars_show_only_while_scrolling()
    {
        FadenstackControls.EnsureLoaded();
        var lines = new StackPanel();
        for (var i = 0; i < 40; i++)
        {
            lines.Children.Add(new TextBlock { Text = "Line " + i });
        }
        var viewer = new ScrollViewer { Height = 100, Width = 200, Content = lines };
        var window = new Window { Content = viewer, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        Assert.DoesNotContain(":scrolling", bar.Classes);
        Assert.Equal(0, thumb.Opacity);
        Assert.Equal(6, thumb.Bounds.Width);

        viewer.Offset = new Vector(0, 60);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(":scrolling", bar.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void The_control_styles_are_added_to_the_application_once()
    {
        FadenstackControls.EnsureLoaded();
        FadenstackControls.EnsureLoaded();
        FadenstackControls.EnsureLoaded();
        _ = new Views.ChatView();
        Assert.Single(Application.Current!.Styles.OfType<FadenstackControls>());
    }
}

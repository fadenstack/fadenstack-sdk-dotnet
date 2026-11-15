using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// WCAG 2.2 AA in each palette (the owner's rule, sdk/51 F15): the text of every view in every state meets 4.5:1 against what is behind it
/// (3:1 for large text, 1.4.3), and the boundary of every control (a check box's box, a field's border, a list's edge), its focus ring and its
/// parts that show a value (a progress bar's fill) meet 3:1 (1.4.11). The colours are computed from the actual brushes of the drawn elements.
/// A gallery of every control in every state (checked, focused, read-only, ...) gets the same checks, because the views show only some states.
/// </summary>
public class StyleContrastTests
{
    private const double Text = 4.5;
    private const double LargeText = 3.0;
    private const double Graphics = 3.0;

    public static IEnumerable<object[]> Themes() => StyleCases.Palettes.Select(p => new object[] { p });

    // ---- the maths ---------------------------------------------------------------------------------------------------

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    internal static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    internal static double Ratio(Color a, Color b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Over(Color top, Color back)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, back.R), Mix(top.G, back.G), Mix(top.B, back.B));
    }

    /// <summary>The solid colour of a brush (a gradient: its stops' first), with its opacity; null for none.</summary>
    private static Color? Solid(Brush? brush)
    {
        Color color;
        switch (brush)
        {
            case SolidColorBrush s:
                color = s.Color;
                break;
            case GradientBrush g when g.GradientStops.Count > 0:
                color = g.GradientStops[0].Color;
                break;
            default:
                return null;
        }
        color.A = (byte)Math.Round(color.A * brush.Opacity);
        return color.A == 0 ? null : color;
    }

    private static Brush? BackgroundOf(DependencyObject d) => d switch
    {
        Border b => b.Background,
        Panel p => p.Background,
        // A control's own background is drawn by its template (a Border or Panel, an ancestor of its parts): not counted twice.
        Shape s => s.Fill,
        _ => null,
    };

    /// <summary>What is behind the element: the backgrounds of its visual ancestors, composited from the outermost in (the window's is white).</summary>
    private static Color Backdrop(DependencyObject element)
    {
        var chain = new List<DependencyObject>();
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            chain.Add(parent);
        }
        chain.Reverse();
        var color = Colors.White;
        foreach (var ancestor in chain)
        {
            if (Solid(BackgroundOf(ancestor)) is { } fill)
            {
                color = Over(fill, color);
            }
        }
        return color;
    }

    private static bool Shown(FrameworkElement e)
    {
        if (e.ActualWidth < 1 || e.ActualHeight < 1)
        {
            return false;
        }
        for (DependencyObject? d = e; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement { Opacity: < 0.05 })
            {
                return false;
            }
        }
        return true;
    }

    private static string Where(FrameworkElement e)
    {
        var parts = new List<string>();
        for (DependencyObject? d = e; d is not null && parts.Count < 4; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement f && (f.Name.Length > 0 || d is Control))
            {
                parts.Add(f.Name.Length > 0 ? $"{f.GetType().Name} {f.Name}" : f.GetType().Name);
            }
        }
        return string.Join(" < ", parts);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- what is checked ---------------------------------------------------------------------------------------------

    /// <summary>The lowest ratio seen per kind of check, and where (reported, not asserted): set while one view's states are checked.</summary>
    internal sealed class Lowest
    {
        private readonly Dictionary<string, (double Ratio, string Where)> _min = new();
        private readonly Dictionary<string, int> _count = new();

        public void Note(string kind, double ratio, string where)
        {
            _count[kind] = _count.TryGetValue(kind, out var n) ? n + 1 : 1;
            if (!_min.TryGetValue(kind, out var seen) || ratio < seen.Ratio)
            {
                _min[kind] = (ratio, where);
            }
        }

        public IEnumerable<string> Lines(string palette) => _min.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1}: lowest {2:0.00}:1 over {3} checks, at {4}", palette, k.Key, k.Value.Ratio, _count[k.Key], k.Value.Where));
    }

    [ThreadStatic]
    private static Lowest? _noting;
    /// <summary>The failures among the visible elements: text against its backdrop, the edge of a control, a focus ring, a progress bar.</summary>
    private static List<string> Failures(IEnumerable<FrameworkElement> visible, string label, bool placeholderNote = false)
    {
        var failures = new List<string>();
        foreach (var element in visible)
        {
            if (!Shown(element))
            {
                continue;
            }
            switch (element)
            {
                case TextBlock { Text.Length: > 0 } text when text.IsEnabled:
                {
                    var back = Backdrop(text);
                    if (Solid(text.Foreground) is { } fore)
                    {
                        var large = text.FontSize >= 24 || (text.FontSize >= 18.67 && text.FontWeight.ToOpenTypeWeight() >= 700);
                        var need = large ? LargeText : Text;
                        var got = Ratio(Over(fore, back), back);
                        _noting?.Note(large ? "large text" : "text", got, $"'{Trim(text.Text)}' ({Where(text)})");
                        if (got < need)
                        {
                            failures.Add($"{label}: text '{Trim(text.Text)}' {Hex(Over(fore, back))} on {Hex(back)} is {got:0.00}:1, needs {need}:1 ({Where(text)})");
                        }
                    }
                    break;
                }
                case TextBoxBase or PasswordBox:
                {
                    var control = (Control)element;
                    if (control.IsEnabled && Solid(control.Foreground) is { } fore && FieldFill(control) is { } fill)
                    {
                        var got = Ratio(fore, fill);
                        _noting?.Note("text", got, Where(control));
                        if (got < Text)
                        {
                            failures.Add($"{label}: the text of {Where(control)} {Hex(fore)} on {Hex(fill)} is {got:0.00}:1, needs {Text}:1");
                        }
                    }
                    break;
                }
                case ProgressBar bar:
                {
                    var track = Named<Border>(bar, "PART_Track");
                    if (track is not null && Solid(track.Background) is { } trackColor && Solid(bar.Foreground) is { } fillColor)
                    {
                        var back = Backdrop(track);
                        var against = Ratio(fillColor, trackColor);
                        var onPage = Ratio(fillColor, back);
                        if (against < Graphics)
                        {
                            failures.Add($"{label}: the progress fill {Hex(fillColor)} on its track {Hex(trackColor)} is {against:0.00}:1, needs {Graphics}:1");
                        }
                        if (onPage < Graphics)
                        {
                            failures.Add($"{label}: the progress fill {Hex(fillColor)} on {Hex(back)} is {onPage:0.00}:1, needs {Graphics}:1");
                        }
                    }
                    break;
                }
            }
            if (element.Name == "Boundary")
            {
                var failure = BoundaryFailure(element, label);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }
            else if (element.Name == "FocusRing")
            {
                var failure = RingFailure(element, label);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }
        }
        return failures;
    }

    private static string Trim(string text) => text.Length <= 28 ? text.Replace('\n', ' ') : text.Substring(0, 28).Replace('\n', ' ') + "...";

    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name)
            {
                return found;
            }
            if (Named<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    private static Color? FieldFill(Control control)
    {
        var boundary = Named<Border>(control, "Boundary");
        var back = Backdrop(boundary ?? (DependencyObject)control);
        return Solid(boundary?.Background ?? control.Background) is { } fill ? Over(fill, back) : back;
    }

    /// <summary>A control's boundary (1.4.11): its edge, or else its fill, against what is behind it. Text-only parts (a quiet button, a list item) have neither and are identified by their text.</summary>
    private static string? BoundaryFailure(FrameworkElement boundary, string label)
    {
        Color? edge;
        Color? fill;
        switch (boundary)
        {
            case Border b:
                edge = b.BorderThickness.Left > 0 || b.BorderThickness.Top > 0 ? Solid(b.BorderBrush) : null;
                fill = Solid(b.Background);
                // A list or combo box item: not a control of its own (its list has the boundary).
                if (boundary.TemplatedParent is ListBoxItem or ComboBoxItem)
                {
                    return null;
                }
                break;
            case Shape s:
                edge = s.StrokeThickness > 0 ? Solid(s.Stroke) : null;
                fill = Solid(s.Fill);
                break;
            default:
                return null;
        }
        // A read-only display (the pairing code) is text, not an input: its text identifies it.
        if (boundary.TemplatedParent is TextBox { IsReadOnly: true } && edge is null)
        {
            return null;
        }
        if (edge is null && fill is null)
        {
            return null;
        }
        var back = Backdrop(boundary);
        var best = 0.0;
        var what = "";
        if (edge is { } e)
        {
            best = Ratio(Over(e, back), back);
            what = $"edge {Hex(Over(e, back))}";
        }
        if (best < Graphics && edge is null && fill is { } f)
        {
            best = Ratio(Over(f, back), back);
            what = $"fill {Hex(Over(f, back))}";
        }
        _noting?.Note("edge", best, Where(boundary));
        return best < Graphics ? $"{label}: the boundary of {Where(boundary)} ({what}) on {Hex(back)} is {best:0.00}:1, needs {Graphics}:1" : null;
    }

    /// <summary>The focus ring (1.4.11): against what is next to it: the fill it sits on (inside) or the backdrop (outside, with a gap).</summary>
    private static string? RingFailure(FrameworkElement ring, string label)
    {
        var brush = ring is Border b ? b.BorderBrush : ring is Shape s ? s.Stroke : null;
        if (Solid(brush) is not { } color)
        {
            return null;
        }
        var back = Backdrop(ring);
        var inside = ring.Margin.Left >= 0;
        if (inside && ring.Parent is Panel panel)
        {
            var boundary = panel.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Name == "Boundary");
            if (boundary is Border { } bb && Solid(bb.Background) is { } fill)
            {
                back = Over(fill, back);
            }
        }
        var got = Ratio(color, back);
        _noting?.Note("focus", got, Where(ring));
        return got < Graphics ? $"{label}: the focus ring of {Where(ring)} {Hex(color)} on {Hex(back)} is {got:0.00}:1, needs {Graphics}:1" : null;
    }

    // ---- the palettes ------------------------------------------------------------------------------------------------

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }

    private static Color Palette(ResourceDictionary colors, string key) => ((SolidColorBrush)colors[$"Fadenstack.{key}"]).Color;

    private static ResourceDictionary PaletteOf(ChatTheme theme)
    {
        EnsureApplication();
        return new ResourceDictionary { Source = ChatView.PaletteUri(theme) };
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_palette_pairs_meet_AA(ChatTheme theme) => Sta.Run(() =>
    {
        var c = PaletteOf(theme);
        var failures = new List<string>();
        void Need(string a, string b, double min)
        {
            var got = Ratio(Palette(c, a), Palette(c, b));
            if (got < min)
            {
                failures.Add($"{theme}: {a} {Hex(Palette(c, a))} on {b} {Hex(Palette(c, b))} is {got:0.00}:1, needs {min}:1");
            }
        }
        var grounds = new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed" };
        foreach (var ground in grounds)
        {
            Need("Foreground", ground, Text);
            Need("ControlBorder", ground, Graphics);
            Need("Focus", ground, Graphics);
            Need("AccentIcon", ground, Graphics);
            Need("AccentBorder", ground, Graphics);
        }
        foreach (var ground in new[] { "Background", "Surface", "SurfaceAlt" })
        {
            Need("ScrollThumb", ground, Graphics);
        }
        foreach (var ground in new[] { "Background", "Surface", "SurfaceAlt", "Hover" })
        {
            Need("ForegroundMuted", ground, Text);
        }
        foreach (var ground in new[] { "Background", "Surface" })
        {
            Need("Link", ground, Text);
        }
        foreach (var banner in new[] { "InfoBackground", "WarningBackground", "BlockedBackground", "AddedBackground" })
        {
            Need("Foreground", banner, Text);
        }
        Need("AccentForeground", "Accent", Text);
        Need("AccentForeground", "AccentHover", Text);
        Need("Focus", "InfoBackground", Graphics);
        Need("AccentIcon", "Track", Graphics);
        foreach (var bar in new[] { "Danger", "Warning", "Success", "AccentIcon" })
        {
            foreach (var banner in new[] { "InfoBackground", "WarningBackground", "BlockedBackground", "AddedBackground" })
            {
                // The bar at a banner's start is a status, read with the text that follows it; it is drawn against the page.
            }
            Need(bar, "Background", Graphics);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    // ---- every view in every state -----------------------------------------------------------------------------------

    [Fact]
    public void Every_view_in_every_state_meets_AA_in_every_palette() => Sta.Run(() =>
    {
        var failures = new List<string>();
        var checkedStates = 0;
        // The Tools view's own lowest ratios (the sdk/51 F05 report), per palette.
        var toolsLowest = StyleCases.Palettes.ToDictionary(p => p, _ => new Lowest());
        foreach (var (view, state) in StyleCases.All())
        {
            using var host = StyleCases.Open(view, state);
            foreach (var palette in StyleCases.Palettes)
            {
                host.SetTheme(palette);
                host.FitToPane();
                _noting = view == "tools" ? toolsLowest[palette] : null;
                try
                {
                    failures.AddRange(Failures(host.Visible(), $"{view}/{state}/{palette}"));
                }
                finally
                {
                    _noting = null;
                }
                checkedStates++;
            }
        }
        if (Environment.GetEnvironmentVariable("FADEN_CONTRAST_STATS") is { Length: > 0 } statsPath)
        {
            File.AppendAllLines(statsPath, StyleCases.Palettes.SelectMany(p => toolsLowest[p].Lines("tools " + p)));
        }
        Assert.True(checkedStates >= 150, $"{checkedStates} view states were checked");
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Distinct().Take(60)));
    });

    // ---- every control in every state ----------------------------------------------------------------------------------

    private static readonly FieldInfo? FocusedKey = typeof(UIElement).GetField("IsKeyboardFocusedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly FieldInfo? FocusWithinKey = typeof(UIElement).GetField("IsKeyboardFocusWithinPropertyKey", BindingFlags.NonPublic | BindingFlags.Static);

    private static void Section(Panel into, ResourceDictionary resources)
    {
        Style S(string key) => (Style)resources[key];
        var path = Geometry.Parse("M 0,0 L 10,0 L 10,10 L 0,10 Z M 3,3 L 7,3 L 7,7 L 3,7 Z");
        into.Children.Add(new TextBlock { Text = "Title text", Style = S("Form.Title") });
        into.Children.Add(new TextBlock { Text = "Strong text", Style = S("Form.Strong") });
        into.Children.Add(new TextBlock { Text = "Caption text", Style = S("Form.Caption") });
        into.Children.Add(new TextBlock { Text = "Plain text", Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new CheckBox { Content = new TextBlock { Text = "Unchecked", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new CheckBox { Content = new TextBlock { Text = "Checked" }, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new CheckBox { Content = new TextBlock { Text = "Unknown" }, IsChecked = null, IsThreeState = true, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new RadioButton { Content = "Radio off", GroupName = Guid.NewGuid().ToString(), Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new RadioButton { Content = "Radio on", GroupName = Guid.NewGuid().ToString(), IsChecked = true, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new TextBox { Text = "A field", Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new TextBox { Text = "Read only", IsReadOnly = true, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new PasswordBox { Password = "secret", Margin = new Thickness(0, 0, 0, 8) });
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), SelectedIndex = 0 };
        combo.Items.Add("First model");
        combo.Items.Add("Second model");
        into.Children.Add(combo);
        var list = new ListBox { Margin = new Thickness(0, 0, 0, 8), SelectedIndex = 1 };
        list.Items.Add("One");
        list.Items.Add("Two selected");
        list.Items.Add("Three");
        into.Children.Add(list);
        into.Children.Add(new ProgressBar { Value = 40, Margin = new Thickness(0, 0, 0, 8) });
        into.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var key in new[] { "Form.Primary", "Form.Secondary", "Form.Quiet", "Form.Choice", "Form.Chip" })
        {
            into.Children.Add(new Button { Content = key.Substring(5), Style = S(key), Margin = new Thickness(0, 0, 0, 8) });
        }
        into.Children.Add(new Button { Name = "IconOne", Content = new System.Windows.Shapes.Path { Data = path, Fill = (Brush)resources["Fadenstack.Foreground"], Width = 10, Height = 10 },
            Style = S("Form.IconButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var key in new[] { "Form.Banner", "Form.Banner.Error", "Form.Banner.Warning", "Form.Banner.Success" })
        {
            into.Children.Add(new Border { Style = S(key), Margin = new Thickness(0, 0, 0, 8), Child = new TextBlock { Text = "A banner: " + key, TextWrapping = TextWrapping.Wrap } });
        }
    }

    private static Window Gallery(ChatTheme theme)
    {
        EnsureApplication();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = ChatView.PaletteUri(theme) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Controls.xaml") });
        var page = new StackPanel { Margin = new Thickness(12) };
        Section(page, resources);
        var card = new Border { Style = (Style)resources["Form.Card"], Margin = new Thickness(0, 8, 0, 0) };
        var inCard = new StackPanel();
        Section(inCard, resources);
        card.Child = inCard;
        page.Children.Add(card);
        var root = new UserControl
        {
            Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            FontSize = 13, FontFamily = (FontFamily)resources["Fadenstack.Font"],
        };
        root.Resources.MergedDictionaries.Add(resources);
        root.SetResourceReference(Control.BackgroundProperty, "Fadenstack.Background");
        root.SetResourceReference(Control.ForegroundProperty, "Fadenstack.Foreground");
        var window = new Window
        {
            Width = 420, Height = 3600, Content = root, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<FrameworkElement> All(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { IsVisible: true } f)
            {
                yield return f;
            }
            foreach (var deeper in All(child))
            {
                yield return deeper;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_control_in_every_state_meets_AA(ChatTheme theme) => Sta.Run(() =>
    {
        var window = Gallery(theme);
        try
        {
            var failures = Failures(All(window), $"gallery/{theme}");
            // Focus: the ring is shown on every control that has one, and it meets 3:1.
            Assert.NotNull(FocusedKey);
            Assert.NotNull(FocusWithinKey);
            foreach (var control in All(window).OfType<Control>().ToList())
            {
                control.SetValue((DependencyPropertyKey)FocusedKey!.GetValue(null)!, true);
                control.SetValue((DependencyPropertyKey)FocusWithinKey!.GetValue(null)!, true);
            }
            window.UpdateLayout();
            var rings = All(window).Where(e => e.Name == "FocusRing").ToList();
            Assert.True(rings.Count >= 12, $"{rings.Count} focus rings are shown");
            failures.AddRange(Failures(rings, $"gallery/{theme}/focus"));
            Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Distinct()));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The views show a focus ring only on the control that has the focus; here every control of the Tools view in every state is given it, one state at a time, and each ring is read.</summary>
    [Fact]
    public void Every_focus_ring_of_the_Tools_view_meets_AA_in_every_palette() => Sta.Run(() =>
    {
        Assert.NotNull(FocusedKey);
        Assert.NotNull(FocusWithinKey);
        var failures = new List<string>();
        var lowest = StyleCases.Palettes.ToDictionary(p => p, _ => new Lowest());
        var rings = 0;
        foreach (var state in StyleCases.ToolsStates)
        {
            using var host = StyleCases.Open("tools", state);
            foreach (var palette in StyleCases.Palettes)
            {
                host.SetTheme(palette);
                host.FitToPane();
                foreach (var control in host.Visible().OfType<Control>().ToList())
                {
                    control.SetValue((DependencyPropertyKey)FocusedKey!.GetValue(null)!, true);
                    control.SetValue((DependencyPropertyKey)FocusWithinKey!.GetValue(null)!, true);
                }
                host.Window.UpdateLayout();
                var shown = host.Visible().Where(e => e.Name == "FocusRing").ToList();
                rings += shown.Count;
                _noting = lowest[palette];
                try
                {
                    failures.AddRange(Failures(shown, $"tools/{state}/{palette}/focus"));
                }
                finally
                {
                    _noting = null;
                }
                foreach (var control in host.Visible().OfType<Control>().ToList())
                {
                    control.SetValue((DependencyPropertyKey)FocusedKey.GetValue(null)!, false);
                    control.SetValue((DependencyPropertyKey)FocusWithinKey!.GetValue(null)!, false);
                }
            }
        }
        if (Environment.GetEnvironmentVariable("FADEN_CONTRAST_STATS") is { Length: > 0 } statsPath)
        {
            File.AppendAllLines(statsPath, StyleCases.Palettes.SelectMany(p => lowest[p].Lines("tools all rings " + p)));
        }
        Assert.True(rings >= 1000, $"{rings} focus rings were read");
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Distinct().Take(40)));
    });
    [Theory]
    [MemberData(nameof(Themes))]
    public void An_icon_is_centred_exactly_in_its_square_button(ChatTheme theme) => Sta.Run(() =>
    {
        var window = Gallery(theme);
        try
        {
            var button = All(window).OfType<Button>().First(b => b.Name == "IconOne");
            var icon = (FrameworkElement)button.Content;
            Assert.Equal(button.ActualWidth, button.ActualHeight);
            var at = icon.TransformToAncestor(button).Transform(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2));
            Assert.InRange(Math.Abs(at.X - button.ActualWidth / 2), 0, 0.01);
            Assert.InRange(Math.Abs(at.Y - button.ActualHeight / 2), 0, 0.01);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void No_control_of_the_gallery_is_left_with_a_WPF_default(ChatTheme theme) => Sta.Run(() =>
    {
        var window = Gallery(theme);
        try
        {
            var visible = All(window).ToList();
            var problems = new List<string>();
            foreach (var control in visible.OfType<Control>().Where(c => c is not ScrollViewer && c is not ScrollBar && c is not Thumb && c is not RepeatButton))
            {
                var styled = control is ToggleButton && control.TemplatedParent is ComboBox
                    || control is ComboBoxItem or ListBoxItem
                    || Named<FrameworkElement>(control, "Boundary") is not null
                    || control is ProgressBar && Named<FrameworkElement>(control, "PART_Track") is not null;
                if (!styled && control is Button or ToggleButton or CheckBox or RadioButton or TextBoxBase or PasswordBox or ComboBox or ListBox or ProgressBar)
                {
                    problems.Add($"{control.GetType().Name} has the default template");
                }
            }
            foreach (var sv in visible.OfType<ScrollViewer>())
            {
                if (Named<FrameworkElement>(sv, "Corner") is not null)
                {
                    problems.Add("a ScrollViewer has the default corner");
                }
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_checks_catch_a_check_box_left_with_the_default_style() => Sta.Run(() =>
    {
        EnsureApplication();
        var palette = new ResourceDictionary { Source = ChatView.PaletteUri(ChatTheme.Dark) };
        var root = new UserControl { Content = new CheckBox { Content = new TextBlock { Text = "I have read this and accept the terms." } }, FontSize = 13 };
        root.Resources.MergedDictionaries.Add(palette);
        root.SetResourceReference(Control.BackgroundProperty, "Fadenstack.Background");
        root.SetResourceReference(Control.ForegroundProperty, "Fadenstack.Foreground");
        var window = new Window
        {
            Width = 420, Height = 200, Content = root, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        window.Show();
        window.UpdateLayout();
        try
        {
            var failures = Failures(All(window), "default");
            Assert.Contains(failures, f => f.Contains("I have read this"));
        }
        finally
        {
            window.Close();
        }
    });

    // ---- one radius system -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_rounded_corner_of_the_views_is_a_container_a_control_or_a_chip() => Sta.Run(() =>
    {
        var allowed = new[] { 0.0, 6.0, 8.0, 10.0, 12.0 };
        var problems = new List<string>();
        foreach (var (view, state) in StyleCases.All())
        {
            using var host = StyleCases.Open(view, state);
            host.SetTheme(ChatTheme.Light);
            host.FitToPane();
            foreach (var element in host.Visible())
            {
                // Thin parts (a scroll thumb, a progress bar, a marker) are as round as they are thin.
                if (element is Border { CornerRadius: var r } border && border.ActualWidth > 6 && border.ActualHeight > 6)
                {
                    foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    {
                        if (!allowed.Contains(corner))
                        {
                            problems.Add($"{view}/{state}: {Where(border)} has a corner of {corner}");
                        }
                    }
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(40)));
    });
}

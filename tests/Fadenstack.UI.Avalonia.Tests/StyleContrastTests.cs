using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Themes;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// WCAG 2.2 AA in each palette (the owner's rule, sdk/51 F15), as the WPF skin's StyleContrastTests: the text of every view in every
/// state meets 4.5:1 against what is behind it (3:1 for large text, 1.4.3), and the boundary of every control (a check box's box, a
/// field's border, a button's edge or fill), its focus ring and its parts that show a value (a progress bar's fill) meet 3:1 (1.4.11).
/// The colours are computed from the actual brushes of the drawn elements. Disabled controls are exempt (1.4.3). The focus ring is
/// measured by really focusing the control with the keyboard (Tab) and reading the adorner the skin shows.
/// </summary>
public class StyleContrastTests
{
    private const double Text = 4.5;
    private const double LargeText = 3.0;
    private const double Graphics = 3.0;

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

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The solid colour of a brush (a gradient: its first stop), with its opacity; null for none.</summary>
    private static Color? Solid(IBrush? brush, double opacity = 1)
    {
        Color color;
        switch (brush)
        {
            case ISolidColorBrush s:
                color = s.Color;
                break;
            case IGradientBrush { GradientStops.Count: > 0 } g:
                color = g.GradientStops[0].Color;
                break;
            default:
                return null;
        }
        var alpha = color.A * brush.Opacity * opacity;
        return alpha < 0.5 ? null : Color.FromArgb((byte)Math.Round(alpha), color.R, color.G, color.B);
    }

    // ---- the palette -------------------------------------------------------------------------------------------------

    private static Color Palette(string key)
    {
        Assert.True(Application.Current!.TryGetResource("Fadenstack." + key, Application.Current.ActualThemeVariant, out var value), key);
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>Every solid colour of the current palette (all the brush keys of FadenstackKeys), transparent included.</summary>
    private static HashSet<Color> PaletteColours()
    {
        var set = new HashSet<Color> { Colors.Transparent };
        foreach (var field in typeof(FadenstackKeys).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            if (Application.Current!.TryGetResource((string)field.GetRawConstantValue()!, Application.Current.ActualThemeVariant, out var value) && value is ISolidColorBrush brush)
            {
                set.Add(brush.Color);
            }
        }
        return set;
    }

    // ---- what is behind an element ---------------------------------------------------------------------------------

    private static IBrush? BackgroundOf(Visual v) => v switch
    {
        Border b => b.Background,
        Panel p => p.Background,
        // A control's own background is drawn by its template (a presenter, a border or a panel, an ancestor of its parts): not counted twice.
        ContentPresenter c => c.Background,
        Shape s => s.Fill,
        _ => null,
    };

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    /// <summary>
    /// What is behind the element: the backgrounds of its visual ancestors, composited from the outermost in (the page colour at the
    /// bottom), and of the earlier siblings in a panel that cover it (a field's frame is a sibling of its text, drawn behind it).
    /// </summary>
    private static Color Backdrop(Visual element, Color page)
    {
        var chain = element.GetVisualAncestors().Reverse().ToList();
        var root = element.GetVisualAncestors().LastOrDefault();
        var color = page;
        for (var i = 0; i < chain.Count; i++)
        {
            var ancestor = chain[i];
            var child = i + 1 < chain.Count ? chain[i + 1] : element;
            if (Solid(BackgroundOf(ancestor), ancestor.Opacity) is { } fill)
            {
                color = Over(fill, color);
            }
            if (ancestor is Panel panel && child is Control c && root is not null)
            {
                var at = panel.Children.IndexOf(c);
                var box = RectIn(element, root);
                for (var j = 0; j < at; j++)
                {
                    var sibling = panel.Children[j];
                    if (sibling.IsEffectivelyVisible && RectIn(sibling, root).Inflate(0.5).Contains(box) && Solid(BackgroundOf(sibling), sibling.Opacity) is { } behind)
                    {
                        color = Over(behind, color);
                    }
                }
            }
        }
        return color;
    }

    private static double OpacityOf(Visual v)
    {
        var opacity = 1.0;
        for (Visual? d = v; d is not null; d = d.GetVisualParent())
        {
            opacity *= d.Opacity;
        }
        return opacity;
    }

    private static bool Shown(Control e)
    {
        if (e.Bounds.Width < 1 || e.Bounds.Height < 1)
        {
            return false;
        }
        for (Visual? d = e; d is not null; d = d.GetVisualParent())
        {
            if (d.Opacity < 0.05)
            {
                return false;
            }
        }
        return true;
    }

    private static string Where(Visual e)
    {
        var parts = new List<string>();
        for (Visual? d = e; d is not null && parts.Count < 4; d = d.GetVisualParent())
        {
            if (d is Control f && (!string.IsNullOrEmpty(f.Name) || d is TemplatedControl))
            {
                parts.Add(string.IsNullOrEmpty(f.Name) ? f.GetType().Name : $"{f.GetType().Name} {f.Name}");
            }
        }
        return parts.Count == 0 ? e.GetType().Name : string.Join(" < ", parts);
    }

    private static string Trim(string text) => text.Length <= 28 ? text.Replace('\n', ' ') : text.Substring(0, 28).Replace('\n', ' ') + "...";

    private static T? Part<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    // ---- what is checked ---------------------------------------------------------------------------------------------

    /// <summary>The lowest ratio seen per palette and kind of check, and where (reported, not asserted).</summary>
    internal sealed class Lowest
    {
        private readonly Dictionary<string, (double Ratio, string Where)> _min = new();
        private readonly Dictionary<string, int> _count = new();

        public void Note(string kind, double ratio, string where)
        {
            _count[kind] = _count.GetValueOrDefault(kind) + 1;
            if (!_min.TryGetValue(kind, out var seen) || ratio < seen.Ratio)
            {
                _min[kind] = (ratio, where);
            }
        }

        public IEnumerable<string> Lines(string palette) => _min.OrderBy(k => k.Key)
            .Select(k => string.Create(CultureInfo.InvariantCulture, $"{palette} {k.Key}: lowest {k.Value.Ratio:0.00}:1 over {_count[k.Key]} checks, at {k.Value.Where}"));
    }

    private static IEnumerable<(string Text, IBrush? Fore)> Runs(TextBlock t)
    {
        if (t.Inlines is { Count: > 0 } inlines)
        {
            foreach (var run in Flatten(inlines, t.Foreground))
            {
                yield return run;
            }
        }
        else if (!string.IsNullOrEmpty(t.Text))
        {
            yield return (t.Text, t.Foreground);
        }
    }

    private static IEnumerable<(string Text, IBrush? Fore)> Flatten(IEnumerable<Inline> inlines, IBrush? inherited)
    {
        foreach (var inline in inlines)
        {
            var fore = inline.Foreground ?? inherited;
            switch (inline)
            {
                case Run { Text.Length: > 0 } run:
                    yield return (run.Text, fore);
                    break;
                case Span span when span.Inlines is not null:
                    foreach (var inner in Flatten(span.Inlines, fore))
                    {
                        yield return inner;
                    }
                    break;
            }
        }
    }

    /// <summary>The failures among the visible elements: text against its backdrop, the edge or fill of a control, a progress bar, an icon.</summary>
    private static List<string> Failures(IEnumerable<Control> visible, string label, Color page, Lowest? lowest = null)
    {
        var failures = new List<string>();
        void Need(string kind, double got, double need, string failure, string where)
        {
            lowest?.Note(kind, got, where);
            if (got < need)
            {
                failures.Add($"{label}: {failure} is {got.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs {need.ToString("0.0", CultureInfo.InvariantCulture)}:1");
            }
        }
        foreach (var element in visible)
        {
            if (!Shown(element) || !element.IsEffectivelyEnabled)
            {
                continue;
            }
            switch (element)
            {
                case TextBlock text:
                {
                    var back = Backdrop(text, page);
                    var opacity = OpacityOf(text);
                    foreach (var (run, brush) in Runs(text))
                    {
                        if (Solid(brush, opacity) is { } fore)
                        {
                            var large = text.FontSize >= 24 || (text.FontSize >= 18.67 && (int)text.FontWeight >= 700);
                            var composed = Over(fore, back);
                            Need(large ? "large text" : "text", Ratio(composed, back), large ? LargeText : Text,
                                $"text '{Trim(run)}' {Hex(composed)} on {Hex(back)} ({Where(text)})", $"'{Trim(run)}' ({Where(text)})");
                        }
                    }
                    break;
                }
                case TextBox box:
                {
                    var border = Part<Border>(box, "PART_BorderElement");
                    if (!string.IsNullOrEmpty(box.Text) && Solid(box.Foreground) is { } fore)
                    {
                        var back = border is null ? Backdrop(box, page) : Backdrop(border, page);
                        var fill = border is not null && Solid(border.Background, border.Opacity) is { } f ? Over(f, back) : back;
                        Need("text", Ratio(fore, fill), Text, $"the text of {Where(box)} {Hex(fore)} on {Hex(fill)}", $"the text of {Where(box)}");
                    }
                    if (border is not null)
                    {
                        Boundary(border, border.BorderBrush, border.BorderThickness, border.Background, box.IsReadOnly, Where(box));
                    }
                    break;
                }
                case ComboBox combo:
                    if (Part<Border>(combo, "FsField") is { } field)
                    {
                        Boundary(field, field.BorderBrush, field.BorderThickness, field.Background, false, Where(combo));
                    }
                    break;
                case CheckBox check:
                    if (Part<Border>(check, "FsBox") is { } square)
                    {
                        Boundary(square, square.BorderBrush, square.BorderThickness, square.Background, false, Where(check));
                        if (check.IsChecked != false && Part<FsIcon>(check, check.IsChecked == true ? "FsMark" : "FsDash") is { } mark && Solid(mark.Foreground) is { } markColour && Solid(square.Background) is { } markFill)
                        {
                            Need("boundary", Ratio(markColour, markFill), Graphics, $"the mark of {Where(check)} {Hex(markColour)} on {Hex(markFill)}", $"the mark of {Where(check)}");
                        }
                    }
                    break;
                case ToggleSwitch toggle:
                    if (Part<Border>(toggle, "FsTrack") is { } rail)
                    {
                        Boundary(rail, rail.BorderBrush, rail.BorderThickness, rail.Background, false, Where(toggle));
                    }
                    break;
                case Button button:
                    if (Part<ContentPresenter>(button, "PART_ContentPresenter") is { } presenter)
                    {
                        Boundary(presenter, presenter.BorderBrush, presenter.BorderThickness, presenter.Background, false, Where(button));
                    }
                    break;
                case ProgressBar bar:
                {
                    if (Solid(bar.Foreground) is { } fillColour)
                    {
                        var back = Backdrop(bar, page);
                        var track = Solid(bar.Background) is { } t ? Over(t, back) : back;
                        Need("progress", Ratio(fillColour, track), Graphics, $"the progress fill {Hex(fillColour)} on its track {Hex(track)}", Where(bar));
                        Need("progress", Ratio(fillColour, back), Graphics, $"the progress fill {Hex(fillColour)} on {Hex(back)}", Where(bar));
                    }
                    break;
                }
                case FsIcon icon:
                    if (Solid(icon.Foreground, OpacityOf(icon)) is { } ink)
                    {
                        var back = Backdrop(icon, page);
                        var composed = Over(ink, back);
                        Need("icon", Ratio(composed, back), Graphics, $"the icon {Where(icon)} {Hex(composed)} on {Hex(back)}", Where(icon));
                    }
                    break;
            }
        }
        return failures;

        // A control's boundary (1.4.11): its edge, or else its fill, against what is behind it. Text-only parts (a quiet button) have neither and are identified by their text.
        void Boundary(Visual part, IBrush? edgeBrush, Thickness thickness, IBrush? fillBrush, bool readOnly, string where)
        {
            var edge = thickness.Left > 0 || thickness.Top > 0 ? Solid(edgeBrush, part.Opacity) : null;
            var fill = Solid(fillBrush, part.Opacity);
            // A read-only display (the pairing code) is text, not an input: its text identifies it.
            if ((readOnly && edge is null) || (edge is null && fill is null))
            {
                return;
            }
            var back = Backdrop(part, page);
            if (edge is { } e)
            {
                Need("boundary", Ratio(Over(e, back), back), Graphics, $"the edge of {where} {Hex(Over(e, back))} on {Hex(back)}", where);
            }
            else if (fill is { } f)
            {
                Need("boundary", Ratio(Over(f, back), back), Graphics, $"the fill of {where} {Hex(Over(f, back))} on {Hex(back)}", where);
            }
        }
    }

    /// <summary>The focus rings of the visible controls (1.4.11): each control that takes the keyboard focus is focused with Tab and the ring the skin shows is read.</summary>
    private static List<string> RingFailures(StyleHost host, string label, Color page, Lowest lowest, ref int rings)
    {
        var failures = new List<string>();
        var window = host.Window;
        var controls = host.Visible().Where(c => c.IsEffectivelyEnabled && c.Focusable && Shown(c) && c.TemplatedParent is null
            && (c is Button or TextBox or ComboBox or CheckBox or ToggleSwitch or RadioButton or ScrollViewer)).ToList();
        foreach (var control in controls)
        {
            if (!control.Focus(NavigationMethod.Tab))
            {
                continue;
            }
            Dispatcher.UIThread.RunJobs();
            var layer = AdornerLayer.GetAdornerLayer(control);
            var ring = layer?.Children.OfType<FocusRing>().LastOrDefault(r => AdornerLayer.GetAdornedElement(r) is { } adorned && (adorned == control || adorned.GetVisualAncestors().Contains(control)));
            Color? colour;
            Visual surrounded;
            string how;
            if (ring is not null)
            {
                colour = Solid(ring.BorderBrush);
                surrounded = (Visual)AdornerLayer.GetAdornedElement(ring)!;
                how = "focus ring";
            }
            else if (control is TextBox box && Part<Border>(box, "PART_BorderElement") is { } border)
            {
                // A text box shows its focus as a 2 px border in the focus colour (Controls.axaml); there is no adorner for it.
                colour = Solid(border.BorderBrush);
                surrounded = border;
                how = "focus border";
            }
            else
            {
                failures.Add($"{label}: {Where(control)} shows no focus indicator");
                continue;
            }
            rings++;
            if (colour is not { } ink)
            {
                failures.Add($"{label}: the {how} of {Where(control)} has no colour");
                continue;
            }
            var back = Backdrop(surrounded, page);
            var got = Ratio(ink, back);
            lowest.Note("focus", got, Where(control));
            if (got < Graphics)
            {
                failures.Add($"{label}: the {how} of {Where(control)} {Hex(ink)} on {Hex(back)} is {got.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs {Graphics.ToString("0.0", CultureInfo.InvariantCulture)}:1");
            }
        }
        host.View.Focus();
        window.Focus();
        Dispatcher.UIThread.RunJobs();
        return failures;
    }

    private static void Report(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        foreach (var line in all)
        {
            TestContext.Current.SendDiagnosticMessage(line);
        }
        if (Environment.GetEnvironmentVariable("FADEN_CONTRAST_STATS") is { Length: > 0 } path)
        {
            File.AppendAllLines(path, all);
        }
    }

    private static Color Page() => Palette("Background");

    /// <summary>Puts the application in the palette; the skin's styles and keys are loaded first (a view loads them when it opens, a test without a view does not).</summary>
    private static Task Use(StylePalette palette)
    {
        FadenstackControls.EnsureLoaded();
        return StyleCases.UseAsync(palette);
    }

    // ---- the palettes ------------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(StylePalette.Light)]
    [InlineData(StylePalette.Dark)]
    [InlineData(StylePalette.HighContrast)]
    public async Task The_palette_pairs_meet_AA(StylePalette palette)
    {
        await Use(palette);
        try
        {
            var failures = new List<string>();
            // A translucent ground (a hover, a tint, a banner) is drawn over the page or over a card: the worse of the two counts.
            Color[] Grounds(string name) => name is "Background" or "Surface" or "SurfaceAlt"
                ? new[] { Palette(name) }
                : new[] { Over(Palette(name), Palette("Background")), Over(Palette(name), Palette("Surface")) };
            void Need(string fore, string ground, double min)
            {
                foreach (var back in Grounds(ground))
                {
                    var ink = Over(Palette(fore), back);
                    var got = Ratio(ink, back);
                    if (got < min)
                    {
                        failures.Add($"{palette}: {fore} {Hex(ink)} on {ground} {Hex(back)} is {got.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs {min.ToString("0.0", CultureInfo.InvariantCulture)}:1");
                    }
                }
            }
            var grounds = new[] { "Background", "Surface", "SurfaceAlt", "Hover", "Pressed" };
            var banners = new[] { "InfoBackground", "WarningBackground", "BlockedBackground", "SuccessBackground" };
            foreach (var ground in grounds)
            {
                Need("Foreground", ground, Text);
                // The boundary of a field, a check box, a secondary button, a choice and a toggle switch's track.
                Need("BorderStrong", ground, Graphics);
                Need("Focus", ground, Graphics);
                Need("AccentIcon", ground, Graphics);
                Need("AccentText", ground, Text);
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
                Need("Accent", ground, Graphics);
            }
            foreach (var banner in banners)
            {
                Need("Foreground", banner, Text);
                Need("Focus", banner, Graphics);
            }
            // Status text and icons on the page, on a card and on their own banner or badge.
            foreach (var (ink, own) in new[] { ("DangerText", "BlockedBackground"), ("WarningText", "WarningBackground"), ("SuccessText", "SuccessBackground") })
            {
                // The warning text is drawn only on its own banner or badge; the error and success texts also on the page and a card (a field's error message).
                if (ink != "WarningText")
                {
                    Need(ink, "Background", Text);
                    Need(ink, "Surface", Text);
                }
                Need(ink, own, Text);
            }
            Need("AccentText", "InfoBackground", Text);
            Need("AccentText", "Tint", Text);
            Need("Foreground", "Tint", Text);
            Need("Success", "SuccessBackground", Graphics);
            Need("Foreground", "SurfaceSubtle", Text);
            // The primary button and the switch that is on: the text or knob on the accent.
            Need("AccentForeground", "Accent", Text);
            Need("AccentForeground", "AccentHover", Text);
            Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures));
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
    }

    // ---- every view in every state -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Every_view_in_every_state_meets_AA_in_every_palette()
    {
        var failures = new List<string>();
        var checkedStates = 0;
        var rings = 0;
        var lowest = StyleCases.Palettes.ToDictionary(p => p, _ => new Lowest());
        // The Tools view's own lowest ratios (the sdk/51 F05 report), apart from the other views'.
        var toolsLowest = StyleCases.Palettes.ToDictionary(p => p, _ => new Lowest());
        try
        {
            foreach (var (view, state) in StyleCases.All())
            {
                using var host = await StyleCases.OpenAsync(view, state);
                foreach (var palette in StyleCases.Palettes)
                {
                    await Use(palette);
                    await host.FitToPaneAsync();
                    var label = $"{view}/{state}/{palette}";
                    failures.AddRange(Failures(host.Visible(), label, Page(), lowest[palette]));
                    failures.AddRange(RingFailures(host, label, Page(), lowest[palette], ref rings));
                    if (view == "tools")
                    {
                        var again = 0;
                        Failures(host.Visible(), label, Page(), toolsLowest[palette]);
                        RingFailures(host, label, Page(), toolsLowest[palette], ref again);
                    }
                    checkedStates++;
                }
            }
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
        Report(StyleCases.Palettes.SelectMany(p => lowest[p].Lines(p.ToString()))
            .Concat(StyleCases.Palettes.SelectMany(p => toolsLowest[p].Lines("tools " + p)))
            .Append($"view states checked: {checkedStates} ({checkedStates / StyleCases.Palettes.Length} per palette), focus rings read: {rings}"));
        Assert.True(checkedStates >= 150, $"{checkedStates} view states were checked");
        Assert.True(rings >= 150, $"{rings} focus rings were read");
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Distinct().Take(80)));
    }

    // ---- no control is left with a Fluent default ---------------------------------------------------------------------

    /// <summary>The brushes a visual draws with: its text, fill, background and border.</summary>
    private static IEnumerable<(string Property, IBrush? Brush)> Drawn(Visual v)
    {
        switch (v)
        {
            case ContentPresenter c:
                yield return ("Foreground", c.Foreground);
                yield return ("Background", c.Background);
                yield return ("BorderBrush", c.BorderBrush);
                break;
            case TemplatedControl t:
                yield return ("Foreground", t.Foreground);
                yield return ("Background", t.Background);
                yield return ("BorderBrush", t.BorderBrush);
                break;
            case Border b:
                yield return ("Background", b.Background);
                yield return ("BorderBrush", b.BorderBrush);
                break;
            case Panel p:
                yield return ("Background", p.Background);
                break;
            case TextBlock tb:
                yield return ("Foreground", tb.Foreground);
                yield return ("Background", tb.Background);
                break;
            case Shape s:
                yield return ("Fill", s.Fill);
                yield return ("Stroke", s.Stroke);
                break;
            case FsIcon i:
                yield return ("Foreground", i.Foreground);
                break;
        }
    }

    private static List<string> Defaults(IEnumerable<Control> visible, HashSet<Color> palette, string label)
    {
        var problems = new List<string>();
        foreach (var element in visible.Where(Shown))
        {
            foreach (var (property, brush) in Drawn(element))
            {
                switch (brush)
                {
                    case null:
                        break;
                    case ISolidColorBrush s when palette.Contains(s.Color):
                        break;
                    case ISolidColorBrush s:
                        problems.Add($"{label}: {element.GetType().Name} in {Where(element)} {property} is {s.Color} (not a colour of the palette)");
                        break;
                    default:
                        problems.Add($"{label}: {Where(element)} {property} is a {brush.GetType().Name} (not a palette brush)");
                        break;
                }
            }
        }
        return problems;
    }

    [AvaloniaFact]
    public async Task No_control_of_the_views_is_left_with_a_Fluent_default_in_any_palette()
    {
        var problems = new List<string>();
        var elements = 0;
        try
        {
            foreach (var (view, state) in StyleCases.All())
            {
                using var host = await StyleCases.OpenAsync(view, state);
                foreach (var palette in StyleCases.Palettes)
                {
                    await Use(palette);
                    await host.FitToPaneAsync();
                    var colours = PaletteColours();
                    var visible = host.Visible().ToList();
                    elements += visible.Count;
                    problems.AddRange(Defaults(visible, colours, $"{view}/{state}/{palette}"));
                    // Plain text takes what its view inherits: that must be the palette's text colour itself, not a colour that happens to be in the palette.
                    if (host.View.GetValue(TextElement.ForegroundProperty) is not ISolidColorBrush inherited || inherited.Color != Palette("Foreground"))
                    {
                        problems.Add($"{view}/{state}/{palette}: the text colour the view passes on is {host.View.GetValue(TextElement.ForegroundProperty)} (Fadenstack.Foreground is {Hex(Palette("Foreground"))})");
                    }
                }
            }
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
        TestContext.Current.SendDiagnosticMessage($"visible elements whose brushes were checked: {elements}");
        Assert.True(elements > 10000, $"{elements} elements were checked");
        Assert.True(problems.Count == 0, problems.Count + " problems:\n" + string.Join("\n", problems.Distinct().Take(80)));
    }

    // ---- the checks catch a dark-on-dark label ------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_checks_catch_a_dark_on_dark_label()
    {
        await Use(StylePalette.Light);
        var lightText = Palette("Foreground");
        await Use(StylePalette.Dark);
        Window? window = null;
        try
        {
            var wrong = new CheckBox { Content = new TextBlock { Text = "I have read this and accept the terms.", Foreground = new SolidColorBrush(lightText) } };
            var right = new CheckBox { Content = new TextBlock { Text = "A label in the dark palette's own text colour." } };
            window = new Window { Width = 420, Height = 200, Content = new StackPanel { Children = { wrong, right } } };
            window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("Fadenstack.Background"));
            window.Show();
            await StyleHost.PumpAsync();
            var failures = Failures(window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible), "negative", Page());
            Assert.Contains(failures, f => f.Contains("I have read this"));
            Assert.DoesNotContain(failures, f => f.Contains("A label in the dark"));
        }
        finally
        {
            window?.Close();
            await StyleCases.ResetAsync();
        }
    }

    // ---- icons in square buttons -------------------------------------------------------------------------------------

    private static void CentredIcons(IEnumerable<Control> visible, string label, List<string> problems, ref int found)
    {
        foreach (var button in visible.OfType<ContentControl>().Where(b => b is Button or ToggleButton && b.Classes.Contains("icon") && b.Bounds.Width > 0).ToList())
        {
            found++;
            if (Math.Abs(button.Bounds.Width - button.Bounds.Height) > 0.01)
            {
                problems.Add($"{label}: {Where(button)} is {button.Bounds.Width}x{button.Bounds.Height}, not square");
            }
            if (button.GetVisualDescendants().OfType<FsIcon>().FirstOrDefault() is not { } icon)
            {
                continue;
            }
            var at = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button);
            if (at is not { } centre || Math.Abs(centre.X - button.Bounds.Width / 2) > 0.01 || Math.Abs(centre.Y - button.Bounds.Height / 2) > 0.01)
            {
                problems.Add($"{label}: the icon of {Where(button)} is centred at {at} in a {button.Bounds.Width}x{button.Bounds.Height} button");
            }
        }
    }

    [AvaloniaFact]
    public async Task An_icon_is_centred_exactly_in_its_square_button()
    {
        var problems = new List<string>();
        var inViews = 0;
        var synthetic = 0;
        try
        {
            // The four views: wherever one has a square icon button (none has today; the check stays for the next).
            await Use(StylePalette.Light);
            foreach (var (view, state) in StyleCases.All())
            {
                using var host = await StyleCases.OpenAsync(view, state);
                await host.FitToPaneAsync();
                CentredIcons(host.Visible(), $"{view}/{state}", problems, ref inViews);
            }
            // The skin's own icon buttons, in every size and as a toggle, in every palette.
            foreach (var palette in StyleCases.Palettes)
            {
                await Use(palette);
                Button Icon(params string[] classes)
                {
                    var button = new Button { Content = new FsIcon { Data = (Geometry)Application.Current!.FindResource("Icon.Add")! }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
                    button.Classes.Add("icon");
                    foreach (var c in classes)
                    {
                        button.Classes.Add(c);
                    }
                    return button;
                }
                var toggle = new ToggleButton { Content = new FsIcon { Data = (Geometry)Application.Current!.FindResource("Icon.Add")! }, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
                toggle.Classes.Add("icon");
                var window = new Window { Width = 200, Height = 300, Content = new StackPanel { Children = { Icon(), Icon("large"), Icon("small"), Icon("primary"), toggle } } };
                window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("Fadenstack.Background"));
                window.Show();
                await StyleHost.PumpAsync();
                CentredIcons(window.GetVisualDescendants().OfType<Control>(), $"synthetic/{palette}", problems, ref synthetic);
                window.Close();
            }
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
        TestContext.Current.SendDiagnosticMessage($"square icon buttons in the four views: {inViews}; in the synthetic window: {synthetic}");
        Assert.Equal(5 * StyleCases.Palettes.Length, synthetic);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    // ---- one radius system -------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Every_rounded_corner_of_the_views_is_a_container_a_control_or_a_chip()
    {
        var allowed = new[] { 0.0, 6.0, 8.0, 10.0, 12.0 };
        var problems = new List<string>();
        var checkedCorners = 0;
        var thin = 0;
        var pill = 0;
        var bars = 0;
        try
        {
            await Use(StylePalette.Light);
            foreach (var (view, state) in StyleCases.All())
            {
                using var host = await StyleCases.OpenAsync(view, state);
                await host.FitToPaneAsync();
                foreach (var element in host.Visible())
                {
                    var r = element switch
                    {
                        Border b => b.CornerRadius,
                        TemplatedControl t => t.CornerRadius,
                        ContentPresenter c => c.CornerRadius,
                        _ => (CornerRadius?)null,
                    };
                    if (r is not { } radius)
                    {
                        continue;
                    }
                    // A progress bar and a scroll bar (their parts too) are as round as they are thin.
                    if (element is ProgressBar || element.GetVisualAncestors().Any(a => a is ProgressBar))
                    {
                        bars++;
                        continue;
                    }
                    if (element is ScrollBar || element.GetVisualAncestors().Any(a => a is ScrollBar))
                    {
                        bars++;
                        continue;
                    }
                    // A toggle switch's track and knob are a pill and a circle.
                    if (element.GetVisualAncestors().Any(a => a is ToggleSwitch))
                    {
                        pill++;
                        continue;
                    }
                    // Thin parts (a marker, a rule) are as round as they are thin.
                    if (element.Bounds.Width <= 6 || element.Bounds.Height <= 6)
                    {
                        thin++;
                        continue;
                    }
                    checkedCorners++;
                    foreach (var corner in new[] { radius.TopLeft, radius.TopRight, radius.BottomLeft, radius.BottomRight })
                    {
                        if (!allowed.Contains(corner))
                        {
                            problems.Add($"{view}/{state}: {Where(element)} has a corner of {corner}");
                        }
                    }
                }
            }
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
        TestContext.Current.SendDiagnosticMessage($"corners checked: {checkedCorners}; exempt: {thin} thin parts (<= 6 px), {pill} parts of a toggle switch, {bars} parts of a progress bar or a scroll bar");
        Assert.True(checkedCorners > 500, $"{checkedCorners} elements were checked");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(60)));
    }
}

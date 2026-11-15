using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The styling pass's evidence for the Avalonia skin (sdk/51 F15): every view in every state in each palette, drawn at Office's
/// task-pane width to <c>&lt;dir&gt;/&lt;palette&gt;/&lt;view&gt;-&lt;state&gt;.png</c> (opt-in: FADEN_RENDER_DIR), the side-by-side images of two
/// such folders (FADEN_COMPARE_BEFORE, FADEN_COMPARE_AFTER, FADEN_COMPARE_OUT), and the snapshot of the automation names and ids the
/// views expose, which a UI driver finds controls by and which a restyle must not change. The opt-in tests do nothing when their
/// variable is not set.
/// </summary>
public class StyleRenderTests
{
    [AvaloniaFact]
    public async Task Every_view_in_every_state_renders_in_every_palette()
    {
        if (Environment.GetEnvironmentVariable("FADEN_RENDER_DIR") is not { Length: > 0 } folder)
        {
            return;
        }
        // FADEN_RENDER_VIEW names one view (such as tools) to draw alone.
        var only = Environment.GetEnvironmentVariable("FADEN_RENDER_VIEW");
        var cases = StyleCases.All().Where(c => string.IsNullOrEmpty(only) || c.View == only).ToList();
        var written = 0;
        try
        {
            foreach (var (view, state) in cases)
            {
                using var host = await StyleCases.OpenAsync(view, state);
                foreach (var palette in StyleCases.Palettes)
                {
                    await StyleCases.UseAsync(palette);
                    await host.FitToPaneAsync();
                    await host.RenderAsync(Path.Combine(folder, palette.ToString(), $"{view}-{Slug(state)}.png"));
                    written++;
                }
            }
        }
        finally
        {
            await StyleCases.ResetAsync();
        }
        Assert.Equal(cases.Count * StyleCases.Palettes.Length, written);
    }

    [AvaloniaFact]
    public void Before_and_after_are_composed_side_by_side()
    {
        if (Environment.GetEnvironmentVariable("FADEN_COMPARE_OUT") is not { Length: > 0 } output)
        {
            return;
        }
        var before = Environment.GetEnvironmentVariable("FADEN_COMPARE_BEFORE")!;
        var after = Environment.GetEnvironmentVariable("FADEN_COMPARE_AFTER")!;
        var count = 0;
        foreach (var palette in StyleCases.Palettes)
        {
            foreach (var (view, state) in StyleCases.All())
            {
                var name = $"{view}-{Slug(state)}.png";
                var left = Path.Combine(before, palette.ToString(), name);
                var right = Path.Combine(after, palette.ToString(), name);
                Assert.True(File.Exists(left) && File.Exists(right), name);
                Compose(left, right, $"{view} / {state} / {palette}   (before | after)", palette, Path.Combine(output, palette.ToString(), name));
                count++;
            }
        }
        Assert.True(count > 0);
    }

    internal static string Slug(string state) => state.ToLowerInvariant();

    private static void Compose(string left, string right, string label, StylePalette palette, string path)
    {
        using var a = new Bitmap(left);
        using var b = new Bitmap(right);
        const int labelHeight = 36;
        const int divider = 3;
        var width = a.PixelSize.Width + divider + b.PixelSize.Width;
        var height = Math.Max(a.PixelSize.Height, b.PixelSize.Height) + labelHeight;
        var light = palette == StylePalette.Light;
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(light ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x14, 0x14, 0x14)), new Rect(0, 0, width, height));
            context.DrawImage(a, new Rect(a.Size), new Rect(0, labelHeight, a.PixelSize.Width, a.PixelSize.Height));
            context.DrawImage(b, new Rect(b.Size), new Rect(a.PixelSize.Width + divider, labelHeight, b.PixelSize.Width, b.PixelSize.Height));
            context.FillRectangle(light ? Brushes.DimGray : Brushes.Gray, new Rect(a.PixelSize.Width, labelHeight, divider, height - labelHeight));
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 20, light ? Brushes.Black : Brushes.White);
            context.DrawText(text, new Point(10, 6));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        target.Save(path, new PngBitmapEncoderOptions());
    }

    // ---- the automation names and ids ---------------------------------------------------------------------------------

    private static readonly string SnapshotFile = Path.Combine(AppContext.BaseDirectory, "automation-before.json");

    /// <summary>
    /// What each view exposes to a UI driver in each state: for every element with an automation name or id, and every control a
    /// person can reach with the keyboard (not a part of another control's template), its type, its x:Name, its name and id as set,
    /// and the name and id its automation peer reports.
    /// </summary>
    private static async Task<SortedDictionary<string, List<string>>> SnapshotAsync()
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (view, state) in StyleCases.All())
        {
            using var host = await StyleCases.OpenAsync(view, state);
            var lines = new List<string>();
            foreach (var control in host.Visible())
            {
                var name = AutomationProperties.GetName(control);
                var id = AutomationProperties.GetAutomationId(control);
                var own = control.TemplatedParent is null;
                var isControl = own && control.Focusable;
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id) && !isControl)
                {
                    continue;
                }
                var peer = isControl ? ControlAutomationPeer.CreatePeerForElement(control) : null;
                lines.Add($"{control.GetType().Name}|{(own ? control.Name : "")}|{name}|{id}|{peer?.GetName()}|{peer?.GetAutomationId()}");
            }
            lines.Sort(StringComparer.Ordinal);
            result[$"{view}/{state}"] = lines;
        }
        return result;
    }

    [AvaloniaFact]
    public async Task Write_the_automation_snapshot()
    {
        if (Environment.GetEnvironmentVariable("FADEN_AUTOMATION_OUT") is not { Length: > 0 } path)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(await SnapshotAsync(), new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_views_expose_the_same_automation_names_and_ids_as_before_the_styling_pass()
    {
        Assert.True(File.Exists(SnapshotFile), "automation-before.json is missing from the test output");
        var before = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(SnapshotFile))!;
        var now = await SnapshotAsync();
        var problems = new List<string>();
        // A scroll bar shows only when the content is taller than the window, which the compact layout changes: not part of the contract.
        static bool Scroll(string line) => line.StartsWith("ScrollBar|", StringComparison.Ordinal);
        foreach (var (key, recorded) in before)
        {
            var expected = recorded.Where(l => !Scroll(l)).ToList();
            if (!now.TryGetValue(key, out var actual))
            {
                problems.Add($"{key}: the state is gone");
                continue;
            }
            actual = actual.Where(l => !Scroll(l)).ToList();
            foreach (var line in expected.Except(actual))
            {
                problems.Add($"{key}: lost    {line}");
            }
            foreach (var line in actual.Except(expected))
            {
                problems.Add($"{key}: new     {line}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}

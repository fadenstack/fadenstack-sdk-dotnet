using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The styling pass's evidence (sdk/51 F15): every view in every state in each palette, drawn at Office's task-pane width to
/// <c>&lt;dir&gt;/&lt;palette&gt;/&lt;view&gt;-&lt;state&gt;.png</c> (opt-in: set FADEN_RENDER_DIR), the side-by-side images of two such folders
/// (FADEN_COMPARE_BEFORE, FADEN_COMPARE_AFTER, FADEN_COMPARE_OUT), and the snapshot of the automation names and ids the views expose, which
/// Office's UI driver finds controls by and which a restyle must not change.
/// </summary>
public class StyleRenderTests
{
    [OptInFact("FADEN_RENDER_DIR")]
    public void Every_view_in_every_state_renders_in_every_palette() => Sta.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable("FADEN_RENDER_DIR")!;
        // FADEN_RENDER_VIEW names one view (such as tools) to draw alone.
        var only = Environment.GetEnvironmentVariable("FADEN_RENDER_VIEW");
        var cases = StyleCases.All().Where(c => string.IsNullOrEmpty(only) || c.View == only).ToList();
        var written = 0;
        foreach (var (view, state) in cases)
        {
            using var host = StyleCases.Open(view, state);
            foreach (var palette in StyleCases.Palettes)
            {
                host.SetTheme(palette);
                host.FitToPane();
                host.Render(Path.Combine(folder, palette.ToString(), $"{view}-{Slug(state)}.png"));
                written++;
            }
        }
        Assert.Equal(cases.Count * StyleCases.Palettes.Length, written);
    });

    [OptInFact("FADEN_COMPARE_OUT")]
    public void Before_and_after_are_composed_side_by_side() => Sta.Run(() =>
    {
        var before = Environment.GetEnvironmentVariable("FADEN_COMPARE_BEFORE")!;
        var after = Environment.GetEnvironmentVariable("FADEN_COMPARE_AFTER")!;
        var output = Environment.GetEnvironmentVariable("FADEN_COMPARE_OUT")!;
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
    });

    internal static string Slug(string state) => state.ToLowerInvariant();

    private static BitmapSource Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static void Compose(string left, string right, string label, ChatTheme palette, string path)
    {
        var a = Load(left);
        var b = Load(right);
        const int labelHeight = 36;
        const int divider = 3;
        var width = a.PixelWidth + divider + b.PixelWidth;
        var height = Math.Max(a.PixelHeight, b.PixelHeight) + labelHeight;
        var dark = palette != ChatTheme.Light;
        var paper = dark ? Color.FromRgb(0x14, 0x14, 0x14) : Color.FromRgb(0xE8, 0xE8, 0xE8);
        var ink = dark ? Brushes.White : Brushes.Black;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(paper), null, new Rect(0, 0, width, height));
            context.DrawImage(a, new Rect(0, labelHeight, a.PixelWidth, a.PixelHeight));
            context.DrawImage(b, new Rect(a.PixelWidth + divider, labelHeight, b.PixelWidth, b.PixelHeight));
            context.DrawRectangle(dark ? Brushes.Gray : Brushes.DimGray, null, new Rect(a.PixelWidth, labelHeight, divider, height - labelHeight));
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 20, ink, 1.0);
            context.DrawText(text, new Point(10, 6));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---- the automation names and ids ---------------------------------------------------------------------------------

    private static readonly string SnapshotFile = Path.Combine(AppContext.BaseDirectory, "automation-before.json");

    /// <summary>What each view exposes to a UI driver in each state: for every element with a name or an id, its type, its x:Name, its name and its id.</summary>
    private static SortedDictionary<string, List<string>> Snapshot()
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (view, state) in StyleCases.All())
        {
            using var host = StyleCases.Open(view, state);
            host.SetTheme(ChatTheme.Light);
            var lines = new List<string>();
            var controls = new HashSet<FrameworkElement>(host.Controls());
            foreach (var element in host.Visible())
            {
                var name = AutomationProperties.GetName(element);
                var id = AutomationProperties.GetAutomationId(element);
                var isControl = controls.Contains(element);
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id) && !isControl)
                {
                    continue;
                }
                var peer = isControl ? UIElementAutomationPeer.CreatePeerForElement(element) : null;
                lines.Add($"{element.GetType().Name}|{(element.TemplatedParent is null ? element.Name : "")}|{name}|{id}|{peer?.GetName()}|{peer?.GetAutomationId()}");
            }
            lines.Sort(StringComparer.Ordinal);
            result[$"{view}/{state}"] = lines;
        }
        return result;
    }

    [OptInFact("FADEN_AUTOMATION_OUT")]
    public void Write_the_automation_snapshot() => Sta.Run(() =>
    {
        var path = Environment.GetEnvironmentVariable("FADEN_AUTOMATION_OUT")!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
    });

    [Fact]
    public void The_views_expose_the_same_automation_names_and_ids_as_before_the_styling_pass() => Sta.Run(() =>
    {
        Assert.True(File.Exists(SnapshotFile), "automation-before.json is missing from the test output");
        var before = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(SnapshotFile))!;
        var now = Snapshot();
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
    });
}

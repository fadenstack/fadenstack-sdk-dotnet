using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Fadenstack.Agents;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents.Sessions;
using Fadenstack.Models;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// An audit, not a test: renders the chat view headlessly (Skia, no window on screen) in states, sizes and themes into a
/// folder, plus 2x close-ups and measurements of the icon buttons. It runs only when FADEN_AUDIT_OUT names a folder.
/// </summary>
public class ChatViewAuditRenders
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("FADEN_AUDIT_OUT");
    private static readonly StringBuilder Metrics = new();
    private static int _shots;

    private sealed class Rig : IDisposable
    {
        public required HarnessChat Harness;
        public required ChatView View;
        public Window Window = null!;
        public double W, H;
        public AgentChatViewModel Chat => Harness.Chat;

        public void Host(double scale)
        {
            if (Window is not null)
            {
                if (Window.Content is LayoutTransformControl lt) lt.Child = null;
                Window.Content = null;
                Window.Close();
            }
            if (scale == 1)
            {
                View.Width = double.NaN;
                View.Height = double.NaN;
                Window = new Window { Width = W, Height = H, Content = View };
            }
            else
            {
                View.Width = W;
                View.Height = H;
                Window = new Window { Width = W * scale, Height = H * scale, Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = View } };
            }
            Window.Show();
        }

        public void Dispose()
        {
            Window?.Close();
            Harness.Dispose();
        }
    }

    private static async Task Pump(int rounds = 8)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }

    private static async Task Until(Func<bool> done, int ms = 4000)
    {
        for (var t = 0; t < ms / 8 && !done(); t += 1)
        {
            await Pump(1);
        }
    }

    private static async Task<Rig> Open(Func<ISessionStore, Task> seed, double w, double h, bool dark, bool openSession)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var harness = await HarnessChat.CreateAsync(seed, new AvaloniaUiDispatcher());
        var rig = new Rig { Harness = harness, View = new ChatView { DataContext = harness.Chat }, W = w, H = h };
        rig.Host(1);
        if (openSession)
        {
            await harness.Chat.OpenSessionAsync(HarnessChat.SessionId);
        }
        await Pump();
        return rig;
    }

    private static Task Start(ISessionStore s) => SyntheticSession.StartAsync(s, DemoGateway_Agent, HarnessChat.SessionId);

    private const string DemoGateway_Agent = "demo-assistant";

    private const string LongAnswer = """
        ## Quarterly summary

        Here is what changed in the **third quarter**, with the numbers that matter and a short script to reproduce them. The most important point is that revenue grew while the cost per ticket fell, see [the report](https://example.org/report) for the details and `ticket_cost` for the metric.

        ### Highlights

        - Revenue grew 12 % against the previous quarter
        - Support tickets fell from 1 480 to 1 120, a drop of 24 percent
        - A very long list entry that has to wrap over several lines in a narrow side panel so that we can see how the hanging indent and the line height behave
        1. First numbered step
        2. Second numbered step

        > Note: the figures are provisional until the audit closes.

        | Region | Revenue | Tickets | Change |
        |:-------|--------:|--------:|:------:|
        | Germany | 4.2 M | 410 | +12 % |
        | France | 2.9 M | 305 | +8 % |
        | A region with a long name | 1.1 M | 120 | -2 % |

        ```python
        def cost_per_ticket(total_cost, tickets):
            # a long line that is wider than a side panel so the code block has to scroll or wrap
            return round(total_cost / max(tickets, 1), 2)

        print(cost_per_ticket(184_000, 1_120))
        ```

        ---

        That is all for now.
        """;

    private static async Task Conversation(ISessionStore s)
    {
        await Start(s);
        await SyntheticSession.AppendTurnAsync(s, DemoGateway_Agent, HarnessChat.SessionId, "t0", "Summarise the third quarter for me, please, with a table and a script.", LongAnswer);
    }

    private static string Dir => Out!;

    private static void Save(WriteableBitmap bmp, string name)
    {
        bmp.Save(Path.Combine(Dir, name + ".png"), new PngBitmapEncoderOptions());
        _shots++;
    }

    private static async Task Shot(Rig rig, string name)
    {
        await Pump();
        Save(rig.Window.CaptureRenderedFrame()!, name);
    }

    private static (byte[] Data, int W, int H, int Stride, bool Bgra) Pixels(WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        var data = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, data, 0, data.Length);
        return (data, fb.Size.Width, fb.Size.Height, fb.RowBytes, fb.Format == PixelFormat.Bgra8888);
    }

    private static void SaveCrop(WriteableBitmap bmp, Rect r, string name)
    {
        var (data, w, h, stride, _) = Pixels(bmp);
        var x0 = Math.Max(0, (int)Math.Floor(r.X)); var y0 = Math.Max(0, (int)Math.Floor(r.Y));
        var x1 = Math.Min(w, (int)Math.Ceiling(r.Right)); var y1 = Math.Min(h, (int)Math.Ceiling(r.Bottom));
        if (x1 <= x0 || y1 <= y0) return;
        using var fb0 = bmp.Lock();
        var crop = new WriteableBitmap(new PixelSize(x1 - x0, y1 - y0), new Vector(96, 96), fb0.Format, AlphaFormat.Premul);
        using (var fb = crop.Lock())
        {
            for (var y = y0; y < y1; y++)
            {
                Marshal.Copy(data, y * stride + x0 * 4, fb.Address + (y - y0) * fb.RowBytes, (x1 - x0) * 4);
            }
        }
        Save(crop, name);
    }

    private static Rect RectIn(Visual v, Visual root)
    {
        var p = v.TranslatePoint(default, root) ?? default;
        return new Rect(p, v.Bounds.Size);
    }

    /// <summary>Renders the view at 2x, crops the named regions, measures where the icon sits in each icon button.</summary>
    private static async Task CloseUps(Rig rig, string scenario, string theme, Func<ChatView, IEnumerable<(string Name, Control C)>> regions, bool measure = true)
    {
        rig.Host(2);
        await Pump(12);
        var frame = rig.Window.CaptureRenderedFrame()!;
        foreach (var (name, c) in regions(rig.View))
        {
            var r = RectIn(c, rig.View);
            var pad = 6;
            SaveCrop(frame, new Rect(r.X * 2 - pad, r.Y * 2 - pad, r.Width * 2 + pad * 2, r.Height * 2 + pad * 2), $"zoom2x-{scenario}-{name}-{theme}");
        }
        if (measure)
        {
            MeasureIcons(rig, frame, scenario, theme);
        }
        rig.Host(1);
        await Pump();
    }

    private static string Label(Control c) =>
        (global::Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } n ? n : null)
        ?? (ToolTip.GetTip(c) as string) ?? c.Name ?? "";

    private static void MeasureIcons(Rig rig, WriteableBitmap frame, string scenario, string theme)
    {
        var (data, fw, fh, stride, bgra) = Pixels(frame);
        (int R, int G, int B) At(int x, int y)
        {
            var o = y * stride + x * 4;
            return bgra ? (data[o + 2], data[o + 1], data[o]) : (data[o], data[o + 1], data[o + 2]);
        }
        var seen = new HashSet<string>();
        foreach (var p in rig.View.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>().Where(p => p.IsEffectivelyVisible && p.Bounds.Width > 0 && p.Bounds.Width < 20 && p.Classes.Contains("icon")))
        {
            var pr = RectIn(p, rig.View);
            var x0 = (int)Math.Round(pr.X * 2) - 4; var y0 = (int)Math.Round(pr.Y * 2) - 4;
            var x1 = Math.Min(fw, (int)Math.Round(pr.Right * 2) + 4); var y1 = Math.Min(fh, (int)Math.Round(pr.Bottom * 2) + 4);
            if (x0 < 0 || y0 < 0) continue;
            var refc = At(x0, y0);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var c = At(x, y);
                    if (Math.Abs(c.R - refc.R) + Math.Abs(c.G - refc.G) + Math.Abs(c.B - refc.B) > 80)
                    { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                }
            if (maxX < 0) continue;
            var cx = (minX + maxX + 1) / 2.0 - (pr.X * 2 + pr.Width); // ink centre minus box centre, device px
            var cy = (minY + maxY + 1) / 2.0 - (pr.Y * 2 + pr.Height);
            var key = $"{string.Join(' ', p.Classes)}|{p.Data}|{theme}";
            if (!seen.Add(key)) continue;
            Metrics.AppendLine($"ICONPATH {scenario}/{theme} cls='{string.Join(' ', p.Classes)}' box={pr.Width:0.#}x{pr.Height:0.#} ink={(maxX - minX + 1) / 2.0:0.0}x{(maxY - minY + 1) / 2.0:0.0} inkCentreOffset dx={cx / 2:+0.00;-0.00;0.00}px dy={cy / 2:+0.00;-0.00;0.00}px data={p.Data?.ToString()?[..Math.Min(30, p.Data.ToString()!.Length)]}");
        }
        foreach (var b in rig.View.GetVisualDescendants().OfType<Button>().Where(b => b is not RepeatButton && b.IsEffectivelyVisible && b.Bounds.Width > 0))
        {
            var path = b.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>().FirstOrDefault(p => p.IsEffectivelyVisible);
            if (path is null) continue;
            var r = RectIn(b, rig.View);
            var x0 = (int)Math.Round(r.X * 2); var y0 = (int)Math.Round(r.Y * 2);
            var x1 = Math.Min(fw, (int)Math.Round(r.Right * 2)); var y1 = Math.Min(fh, (int)Math.Round(r.Bottom * 2));
            if (x1 - x0 < 8 || y1 - y0 < 8) continue;
            // the reference: the button's own background, a few device pixels in from the left edge at mid height
            var refc = At(x0 + Math.Max(4, (int)b.Padding.Left), (y0 + y1) / 2);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = y0 + 2; y < y1 - 2; y++)
            {
                for (var x = x0 + 2; x < x1 - 2; x++)
                {
                    var c = At(x, y);
                    if (Math.Abs(c.R - refc.R) + Math.Abs(c.G - refc.G) + Math.Abs(c.B - refc.B) > 60)
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                }
            }
            var pr = RectIn(path, rig.View);
            var ink = maxX < 0 ? "none" :
                $"inkL={(minX - x0) / 2.0:0.0} inkR={(x1 - 1 - maxX) / 2.0:0.0} inkT={(minY - y0) / 2.0:0.0} inkB={(y1 - 1 - maxY) / 2.0:0.0} => dx={(((minX - x0) - (x1 - 1 - maxX)) / 4.0):+0.00;-0.00;0.00} dy={(((minY - y0) - (y1 - 1 - maxY)) / 4.0):+0.00;-0.00;0.00}";
            var content = b.Content is string s ? $"\"{s}\"" : b.Content?.GetType().Name ?? "null";
            Metrics.AppendLine($"ICONBTN {scenario}/{theme} [{Label(b)}] classes='{string.Join(' ', b.Classes)}' size={r.Width:0.#}x{r.Height:0.#} pad={b.Padding} content={content} path={pr.Width:0.#}x{pr.Height:0.#}@({pr.X - r.X:0.#},{pr.Y - r.Y:0.#}) {ink}");
        }
    }

    private static void Dump(Rig rig, string scenario)
    {
        var all = rig.View.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
        Metrics.AppendLine($"--- DUMP {scenario} {rig.W}x{rig.H}");
        foreach (var c in all.Where(c => (c is Button or ToggleButton or SplitButton or TextBox) && c is not RepeatButton))
        {
            var r = RectIn(c, rig.View);
            var content = (c as ContentControl)?.Content is string s ? $"\"{s}\"" : (c as ContentControl)?.Content?.GetType().Name ?? (c as TextBox)?.PlaceholderText;
            var tip = ToolTip.GetTip(c) as string;
            var auto = global::Avalonia.Automation.AutomationProperties.GetName(c);
            var radius = (c as TemplatedControl)?.CornerRadius;
            Metrics.AppendLine($"CTRL {c.GetType().Name} cls='{string.Join(' ', c.Classes)}' {r.Width:0.#}x{r.Height:0.#}@({r.X:0},{r.Y:0}) pad={(c as TemplatedControl)?.Padding} radius={radius} fs={(c as TemplatedControl)?.FontSize} content={content} tip={(tip is null ? "NO" : "yes")} auto={(string.IsNullOrEmpty(auto) ? "NO" : "yes")}");
        }
        foreach (var b in all.OfType<Border>().Where(b => b.CornerRadius != default))
        {
            Metrics.AppendLine($"BORDER cls='{string.Join(' ', b.Classes)}' {b.Bounds.Width:0.#}x{b.Bounds.Height:0.#} radius={b.CornerRadius} pad={b.Padding}");
        }
        foreach (var g in all.OfType<TextBlock>().GroupBy(t => t.FontSize).OrderBy(g => g.Key))
        {
            Metrics.AppendLine($"FONT size={g.Key} count={g.Count()} e.g. '{Trunc(g.First().Text)}'");
        }
        foreach (var c in all.Where(c => RectIn(c, rig.View).Right > rig.W + 0.5 || RectIn(c, rig.View).X < -0.5))
        {
            Metrics.AppendLine($"OVERFLOW {c.GetType().Name} cls='{string.Join(' ', c.Classes)}' x={RectIn(c, rig.View).X:0.#} right={RectIn(c, rig.View).Right:0.#} of {rig.W}");
        }
        var rows = all.OfType<TurnStackPanel>().Count();
    }

    private static string Trunc(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;

    private sealed record Scenario(string Name, Func<ISessionStore, Task> Seed, bool OpenSession, Func<Rig, Task>? Setup, Func<ChatView, IEnumerable<(string, Control)>>? Zoom = null);

    private static IEnumerable<T> Find<T>(ChatView v, Func<T, bool>? f = null) where T : Visual =>
        v.GetVisualDescendants().OfType<T>().Where(x => x.IsEffectivelyVisible && (f is null || f(x)));

    private static IEnumerable<(string, Control)> ComposerZoom(ChatView v)
    {
        yield return ("composer", (Control)v.FindControl<Border>("ComposerBox")!);
        yield return ("header", Find<Border>(v, b => b.Classes.Contains("header")).First());
    }

    private static IEnumerable<(string, Control)> ConversationZoom(ChatView v)
    {
        foreach (var z in ComposerZoom(v)) yield return z;
        var copy = Find<Button>(v, b => b.Classes.Contains("hidedisabled")).FirstOrDefault();
        if (copy?.Parent is Control row) yield return ("turn-actions", row);
    }

    private static async Task Run(Scenario s, bool zoomsFor380Only = true)
    {
        foreach (var (w, h) in new[] { (380.0, 800.0), (900.0, 800.0) })
        {
            foreach (var dark in new[] { false, true })
            {
                var theme = dark ? "dark" : "light";
                using var rig = await Open(s.Seed, w, h, dark, s.OpenSession);
                if (s.Setup is not null) await s.Setup(rig);
                await Shot(rig, $"{s.Name}-{w}-{theme}");
                if (w == 380)
                {
                    if (!dark) Dump(rig, s.Name);
                    if (s.Zoom is not null) await CloseUps(rig, s.Name, theme, s.Zoom);
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task Render_the_audit_states()
    {
        if (string.IsNullOrEmpty(Out)) return;
        Directory.CreateDirectory(Out);
        try
        {
            await RenderAll();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Out.TrimEnd('\\', '/'))!, "audit-metrics.txt"), Metrics.ToString());
        }
    }

    private static T Priv<T>(object o, string name) where T : class => (T)o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;

    private static async Task RenderAll()
    {
        // 1. Empty chat: greeting and suggestions
        await Run(new Scenario("01-empty", s => Start(s), false, null, ComposerZoom));

        // 2. A conversation with a long Markdown answer
        await Run(new Scenario("02-markdown", Conversation, true, null, ConversationZoom));

        await Run(new Scenario("02b-markdown-top", Conversation, true, async rig =>
        {
            await Pump(8);
            rig.View.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Name == "Scroller").ScrollToHome();
            await Pump(10);
        }));

        // 3. A tool call in the turn (a read runs without asking), expanded
        await Run(new Scenario("03-toolcall", Conversation, true, async rig =>
        {
            await rig.Chat.SendAsync("Read page 3");
            await Pump(10);
            foreach (var p in rig.Chat.Turns[^1].Parts.OfType<ToolCallPart>()) p.ToggleCommand.Execute(null);
            await Pump(10);
            rig.View.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Name == "Scroller").ScrollToEnd();
            await Pump(10);
        }, v => Find<Button>(v, b => b.DataContext is ToolCallPart).Take(1).Select(b => ("toolline", (Control)b))));

        // 4. A running turn (thinking / streaming), early and later
        await Run(new Scenario("04-running", Conversation, true, async rig =>
        {
            rig.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(40);
            _ = rig.Chat.SendAsync("stream long and detailed");
            await Until(() => rig.Chat.IsBusy);
            await Task.Delay(500);
            await Pump(12);
        }, ComposerZoom));

        // 5. Approvals: destructive (plain Allow) and a write with the split button, menu open
        await Run(new Scenario("05-approval-destructive", Conversation, true, async rig =>
        {
            rig.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);
            _ = rig.Chat.SendAsync("Delete page 2");
            await Until(() => rig.Chat.Turns[^1].Parts.OfType<ApprovalPart>().Any());
            await Pump(10);
        }, v => Find<Border>(v, b => b.Classes.Contains("approval")).Take(1).Select(b => ("approval", (Control)b))));

        foreach (var (w, h) in new[] { (380.0, 800.0), (900.0, 800.0) })
        {
            foreach (var dark in new[] { false, true })
            {
                var theme = dark ? "dark" : "light";
                using var rig = await Open(Conversation, w, h, dark, true);
                rig.Harness.Gateway.WordDelay = TimeSpan.FromMilliseconds(1);
                _ = rig.Chat.SendAsync("Add a comment to page 2");
                await Until(() => rig.Chat.Turns[^1].Parts.OfType<ApprovalPart>().Any());
                await Pump(10);
                await Shot(rig, $"06-approval-write-{w}-{theme}");
                if (w == 380)
                {
                    if (!dark) Dump(rig, "06-approval-write");
                    var split = Find<SplitButton>(rig.View).FirstOrDefault();
                    if (split is not null)
                    {
                        split.Flyout!.ShowAt(split);
                        await Pump(12);
                        await Shot(rig, $"06-approval-write-menu-{w}-{theme}");
                        var popup = typeof(FlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(split.Flyout) as Popup;
                        if (popup?.GetType().GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(popup) is TopLevel tl && !ReferenceEquals(tl, rig.Window))
                        {
                            Save(tl.CaptureRenderedFrame()!, $"06-approval-write-menu-popuproot-{w}-{theme}");
                        }
                        split.Flyout.Hide();
                        await Pump(6);
                        await CloseUps(rig, "06-approval-write", theme, v => Find<Border>(v, b => b.Classes.Contains("approval")).Take(1).Select(b => ("approval", (Control)b)));
                    }
                }
                else
                {
                    var split = Find<SplitButton>(rig.View).FirstOrDefault();
                    split?.Flyout!.ShowAt(split);
                    await Pump(12);
                    await Shot(rig, $"06-approval-write-menu-{w}-{theme}");
                    split?.Flyout!.Hide();
                }
                var part = rig.Chat.Turns[^1].Parts.OfType<ApprovalPart>().First();
                part.DenyCommand.Execute(null);
                await Pump(10);
            }
        }

        // 7. Cards from a module: card, table, diff, progress, citations, notices (there is no question card in the SDK view)
        await Run(new Scenario("07-parts", Conversation, true, rig =>
        {
            var turn = rig.Chat.Turns[^1];
            turn.AddPart(new CardPart("Meeting request", new[] { new CardField("When", "Tuesday 14:00"), new CardField("Where", "Room 4.12, Building B"), new CardField("Attendees", "Anna Schmidt, Jonas Weber, Ayse Yilmaz") }, "Needs your answer"));
            turn.AddPart(new TablePart(new[] { "Item", "Qty", "Price" }, new[] { (IReadOnlyList<string>)new[] { "Widget", "2", "9.00" }, new[] { "A long product name that wraps", "10", "120.50" } }, "Order"));
            turn.AddPart(new DiffPart("Page 2", new[] { new DiffLine(DiffLineKind.Context, "Intro text"), new DiffLine(DiffLineKind.Removed, "old line"), new DiffLine(DiffLineKind.Added, "new line") }));
            turn.AddPart(new ProgressPart("Indexing documents", 0.4));
            turn.AddPart(new CitationsPart(new[] { new Citation("Handbook.pdf", "page 4", 0.9), new Citation("Policy.docx", "page 1", 0.7) }));
            turn.AddPart(new NoticePart(NoticeSeverity.Info, "Information", "A neutral notice from a module."));
            turn.AddPart(new NoticePart(NoticeSeverity.Warning, "Careful", "This action is not allowed in this mode, pick another mode.", "decision 1234"));
            turn.AddPart(new NoticePart(NoticeSeverity.Blocked, "Blocked", "The policy blocked this message.", "decision 5678"));
            rig.View.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Name == "Scroller").ScrollToEnd();
            return Pump(12);
        }));

        // 8. Attached context chips in the composer, skills, and a sent turn with chips
        await Run(new Scenario("08-chips", Conversation, true, async rig =>
        {
            rig.Chat.Attachments.Add(new AttachmentViewModel("Selection: Page 3, paragraph 2", "The current selection", _ => Task.FromResult<MessageAttachment?>(null), isImplicit: true));
            rig.Chat.Attachments.Add(new AttachmentViewModel("Quarterly-report-final-version-2026-Q3-reviewed.docx", "document", _ => Task.FromResult<MessageAttachment?>(null)));
            rig.Chat.Attachments.Add(new AttachmentViewModel("screenshot.png", "picture", _ => Task.FromResult<MessageAttachment?>(null)) { Image = SyntheticSession.Picture });
            rig.Chat.PickedSkills.Add(new SkillOption { Id = "1", Slug = "summarise", Name = "Summarise documents", Description = "Short summaries" });
            rig.Chat.PickedSkills.Add(new SkillOption { Id = "2", Slug = "translate", Name = "Translate", Description = "" });
            rig.Chat.Input = "Please look at these and summarise.";
            await Pump(12);
        }, ComposerZoom));

        // 9. Errors: the banner, a failed turn with the message under it
        await Run(new Scenario("09-error", Conversation, true, async rig =>
        {
            var turn = rig.Chat.Turns[^1];
            typeof(TurnViewModel).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(turn, new object[] { "The server answered 503: the model is not reachable. Try again in a moment.", DateTimeOffset.Now });
            rig.Chat.PictureNotRead("holiday-photo-from-the-mountains.heic");
            await Pump(12);
        }, v => new (string, Control)[] { ("banner", Find<Border>(v, b => b.Classes.Contains("notice")).Last()) }));

        // 10. Sign-in required (the view shows the same banner as for any error; SignInRequired is not bound)
        await Run(new Scenario("10-signin", Start, false, async rig =>
        {
            typeof(AgentChatViewModel).GetProperty("SignInRequired")!.GetSetMethod(true)!.Invoke(rig.Chat, new object[] { true });
            typeof(AgentChatViewModel).GetProperty("Error")!.GetSetMethod(true)!.Invoke(rig.Chat, new object[] { "Sign in to Fadenstack to use this agent." });
            await Pump(10);
        }));

        // 11. Menus: history, mode menu, settings (popups), about
        await Run(new Scenario("11-history", Conversation, true, async rig => { rig.Chat.IsHistoryOpen = true; await Pump(12); }));
        await Run(new Scenario("12-mode-menu", Conversation, true, async rig => { rig.Chat.IsModeMenuOpen = true; await Pump(14); }));
        await Run(new Scenario("13-settings-menu", Conversation, true, async rig => { rig.Chat.IsSettingsOpen = true; await Pump(14); }));
        await Run(new Scenario("14-about", Conversation, true, async rig => { rig.Chat.OpenAboutCommand.Execute(null); await Pump(12); }));

        // 15. Hover / pressed / focus on the header and the turn actions (pseudo-classes set by hand)
        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            using var rig = await Open(Conversation, 380, 800, dark, true);
            rig.Host(2);
            await Pump(12);
            var buttons = Find<Button>(rig.View, b => b.Classes.Contains("ghost") || b.Classes.Contains("primary")).Take(6).ToList();
            var toggles = Find<ToggleButton>(rig.View).ToList();
            var all = buttons.Cast<Control>().Concat(toggles).ToList();
            var states = new[] { ":pointerover", ":pressed", ":focus-visible", ":focus" };
            foreach (var st in states)
            {
                foreach (var c in all) ((IPseudoClasses)c.Classes).Set(st, true);
                await Pump(6);
                var frame = rig.Window.CaptureRenderedFrame()!;
                SaveCrop(frame, new Rect(0, 0, 380 * 2, 56 * 2), $"15-state{st.Replace(":", "-")}-header-{theme}");
                var row = Find<Button>(rig.View, b => b.Classes.Contains("hidedisabled")).FirstOrDefault()?.Parent as Control;
                if (row is not null)
                {
                    var r = RectIn(row, rig.View);
                    SaveCrop(frame, new Rect(r.X * 2 - 6, r.Y * 2 - 6, r.Width * 2 + 12, r.Height * 2 + 12), $"15-state{st.Replace(":", "-")}-turnactions-{theme}");
                }
                foreach (var c in all) ((IPseudoClasses)c.Classes).Set(st, false);
            }
        }
        Metrics.AppendLine($"SHOTS {_shots}");
    }
}

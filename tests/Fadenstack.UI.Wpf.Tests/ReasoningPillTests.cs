using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The thinking pill in the Office-looking skin: it is in the header beside the tool mode, shows the levels the profile
/// offers and nothing with one, and fits a 300 px panel. When FADEN_WPF_RENDERS names a folder the header and the open menu
/// are drawn offscreen to PNG files there (no window opens).
/// </summary>
public class ReasoningPillTests
{
    private const string Capped = """{"max":"low"}""";

    private sealed class Profile : HttpMessageHandler
    {
        private readonly string? _reasoning;

        public Profile(string? reasoning) => _reasoning = reasoning;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var ui = _reasoning is null ? "{}" : "{\"reasoning\":" + _reasoning + "}";
            var body = path.EndsWith("/profile", StringComparison.Ordinal)
                ? """{"id":"a","slug":"word","name":"Word assistant","version":1,"host_tool_contract":[],"local_mcp":{"mode":"disable","developer_mode":false},"tool_modes":{"default":"ask","ceiling":null},"ui":""" + ui
                  + ""","required_client_modules":[],"edition":"community","features":[]}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (ChatView View, AgentChatViewModel Chat, FadenstackAgent Agent) Open(string? reasoning, string theme, double width, string language = "en")
    {
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new Profile(reasoning)))
            .UseAgent("word")
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals(broker)
            .Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance,
            new AgentChatOptions { AutomaticLanguage = new System.Globalization.CultureInfo(language), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero });
        chat.InitializeAsync().GetAwaiter().GetResult();
        var view = new ChatView { Theme = Enum.Parse<ChatTheme>(theme), DataContext = chat, Width = width, Height = 560 };
        view.Measure(new Size(width, 560));
        view.Arrange(new Rect(0, 0, width, 560));
        view.UpdateLayout();
        return (view, chat, agent);
    }

    private static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);

    private static void Render(FrameworkElement element, Brush canvas, string name)
    {
        if (Environment.GetEnvironmentVariable("FADEN_WPF_RENDERS") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        const double pad = 8;
        var width = element.ActualWidth + 2 * pad;
        var height = element.ActualHeight + 2 * pad;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(canvas, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(pad, pad, element.ActualWidth, element.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private static void RenderHeaderAndMenu(ChatView view, string name)
    {
        var header = Named<Border>(view, "Header");
        Render(header, (Brush)view.FindResource("Fadenstack.Background"), name + "-header");
        var menu = (FrameworkElement)Named<Popup>(view, "ReasoningMenu").Child;
        menu.Measure(new Size(400, 400));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        Render(menu, (Brush)view.FindResource("Fadenstack.Background"), name + "-menu");
    }

    [Theory]
    [InlineData("Light", 280)]
    [InlineData("Light", 300)]
    [InlineData("Light", 360)]
    [InlineData("Dark", 280)]
    [InlineData("Dark", 300)]
    [InlineData("Dark", 360)]
    [InlineData("DarkGray", 300)]
    public void Five_levels_show_in_the_header_and_keep_the_avatar_and_title(string theme, int width) => Sta.Run(() =>
    {
        var (view, chat, agent) = Open(null, theme, width);
        using (agent)
        {
            var pill = Named<ToggleButton>(view, "ReasoningButton");
            Assert.Equal(Visibility.Visible, pill.Visibility);
            AssertHeaderFits(view, pill);
            Assert.Equal(new[] { "Auto", "Off", "Low", "Medium", "High" }, chat.ReasoningOptions.Select(o => o.Label));
            Assert.Equal("Thinking: Auto", System.Windows.Automation.AutomationProperties.GetName(pill));
            RenderHeaderAndMenu(view, $"wpf-five-{theme.ToLowerInvariant()}-{width}");
        }
    });

    [Theory]
    [InlineData("Light", 280)]
    [InlineData("Light", 300)]
    [InlineData("Light", 360)]
    [InlineData("Dark", 280)]
    [InlineData("Dark", 300)]
    [InlineData("Dark", 360)]
    public void A_maximum_hides_levels_and_the_pill_stays_compact(string theme, int width) => Sta.Run(() =>
    {
        var (view, chat, agent) = Open(Capped, theme, width);
        using (agent)
        {
            var pill = Named<ToggleButton>(view, "ReasoningButton");
            Assert.Equal(new[] { "Auto", "Off", "Low" }, chat.ReasoningOptions.Select(o => o.Label));
            chat.SelectReasoningCommand.Execute(chat.ReasoningOptions[1]);
            Assert.Equal("Off", chat.ReasoningPillText);
            Assert.Equal("Thinking: Off", System.Windows.Automation.AutomationProperties.GetName(pill));
            var tip = Assert.IsType<string>(pill.ToolTip);
            Assert.Contains("How long it thinks", tip);
            Assert.Contains("Thinking: Off", tip);
            AssertHeaderFits(view, pill);
            RenderHeaderAndMenu(view, $"wpf-max-{theme.ToLowerInvariant()}-{width}");
        }
    });

    [Fact]
    public void The_menu_rows_have_names_and_one_is_marked() => Sta.Run(() =>
    {
        var (view, chat, agent) = Open(null, "Light", 300);
        using (agent)
        {
            Assert.Single(chat.ReasoningOptions, o => o.IsSelected);
            chat.SelectReasoningCommand.Execute(chat.ReasoningOptions[3]);
            Assert.Equal(new[] { false, false, false, true, false }, chat.ReasoningOptions.Select(o => o.IsSelected));
            Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Named<ToggleButton>(view, "ReasoningButton"))));
        }
    });

    /// <summary>The avatar and the title show, trimmed if need be; the pill, the buttons and the avatar overlap nothing and stay inside.</summary>
    private static void AssertHeaderFits(ChatView view, FrameworkElement pill)
    {
        var header = Named<Border>(view, "Header");
        Rect Box(FrameworkElement e) => e.TransformToAncestor(header).TransformBounds(new Rect(e.RenderSize));
        var avatar = Named<FrameworkElement>(view, "HeaderAvatar");
        var title = Named<TextBlock>(view, "HeaderTitle");
        var a = Box(avatar);
        var t = Box(title);
        var p = Box(pill);
        Assert.True(a.Width >= 24 && a.Left >= 0, $"avatar {a}");
        Assert.True(t.Width >= 24, $"the title has {t.Width} px");
        Assert.True(a.Right <= t.Left + 0.5, $"avatar {a} and title {t}");
        Assert.True(t.Right <= p.Left + 12, $"title {t} and pill {p}"); // the mode pill sits before it
        Assert.True(p.Right <= header.ActualWidth + 0.5, $"the pill ends at {p.Right} of {header.ActualWidth}");
        var buttons = new[] { "HistoryButton", "SettingsButton" }.Select(n => Box(Named<FrameworkElement>(view, n))).ToList();
        Assert.All(buttons, b => Assert.True(b.Right <= header.ActualWidth + 0.5 && b.Left >= p.Right - 0.5, $"button {b} pill {p}"));
    }
}

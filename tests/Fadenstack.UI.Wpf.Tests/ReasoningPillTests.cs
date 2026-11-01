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
    private const string Switch = """{"default":"none","levels":["none","high"]}""";

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
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("DarkGray")]
    public void Five_levels_show_in_the_header_and_fit_300_px(string theme) => Sta.Run(() =>
    {
        var (view, chat, agent) = Open(null, theme, 300);
        using (agent)
        {
            var pill = Named<ToggleButton>(view, "ReasoningButton");
            Assert.Equal(Visibility.Visible, pill.Visibility);
            AssertInside(view, pill);
            Assert.Equal(5, chat.ReasoningOptions.Count);
            Assert.Equal(new[] { "Auto", "Off", "Low", "Medium", "High" }, chat.ReasoningOptions.Select(o => o.Label));
            Assert.Equal("Name", System.Windows.Automation.AutomationProperties.GetName(pill) is { Length: > 0 } ? "Name" : "");
            RenderHeaderAndMenu(view, "wpf-five-" + theme.ToLowerInvariant());
        }
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Off_and_on_read_thinking_off_and_on(string theme) => Sta.Run(() =>
    {
        var (view, chat, agent) = Open(Switch, theme, 300);
        using (agent)
        {
            Assert.Equal("Thinking: Off", chat.ReasoningPillText);
            AssertInside(view, Named<ToggleButton>(view, "ReasoningButton"));
            Assert.Equal(new[] { "Off", "On" }, chat.ReasoningOptions.Select(o => o.Label));
            RenderHeaderAndMenu(view, "wpf-switch-" + theme.ToLowerInvariant());
        }
    });

    [Fact]
    public void With_one_level_the_pill_is_collapsed() => Sta.Run(() =>
    {
        var (view, _, agent) = Open("""{"levels":["high"]}""", "Light", 300);
        using (agent)
        {
            Assert.Equal(Visibility.Collapsed, Named<ToggleButton>(view, "ReasoningButton").Visibility);
            RenderHeaderAndMenu(view, "wpf-hidden-light");
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

    /// <summary>The pill lies inside the header and does not push the title out of sight.</summary>
    private static void AssertInside(ChatView view, FrameworkElement pill)
    {
        var header = Named<Border>(view, "Header");
        var box = pill.TransformToAncestor(header).TransformBounds(new Rect(pill.RenderSize));
        Assert.True(box.Right <= header.ActualWidth + 0.5, $"the pill ends at {box.Right} of {header.ActualWidth}");
        Assert.True(box.Left >= 0, $"the pill starts at {box.Left}");
    }
}

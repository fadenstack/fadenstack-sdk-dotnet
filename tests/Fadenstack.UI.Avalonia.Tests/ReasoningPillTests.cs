using System.Globalization;
using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The thinking pill beside the tool-mode pill: shown with the levels the profile offers, hidden with one, and the same
/// at the narrowest chat panel (300 px) in both themes. Each case also renders the pill closed and its menu open into
/// <c>FADEN_EFFORT_OUT</c> (a folder in the temporary folder by default) so the pictures can be looked at.
/// </summary>
public class ReasoningPillTests
{
    private static readonly string Out = Environment.GetEnvironmentVariable("FADEN_EFFORT_OUT")
        ?? Path.Combine(Path.GetTempPath(), "faden-effort-renders");

    /// <summary>The harness gateway, with <c>ui.reasoning</c> added to the profile it serves.</summary>
    private sealed class ProfileWith : DelegatingHandler
    {
        private readonly string? _reasoning;

        public ProfileWith(string? reasoning) : base(new HarnessGateway()) => _reasoning = reasoning;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (_reasoning is null || !request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal))
            {
                return response;
            }
            var json = (await response.Content.ReadAsStringAsync(cancellationToken)).Replace("\"ui\":{", "\"ui\":{\"reasoning\":" + _reasoning + ",");
            return new HttpResponseMessage(response.StatusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Rig : IDisposable
    {
        public required Window Window;
        public required ChatView View;
        public required AgentChatViewModel Chat;
        public required FadenstackAgent Agent;

        /// <summary>Fails when the pill is cut off by the panel's edge or lies under the send button.</summary>
        public void AssertPillFits()
        {
            var pill = Pill!;
            var box = new Rect(pill.TranslatePoint(default, View)!.Value, pill.Bounds.Size);
            Assert.True(box.Right <= View.Bounds.Width - 4, $"the pill ends at {box.Right} of {View.Bounds.Width}");
            foreach (var send in View.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && b.IsVisible && b.IsEffectivelyVisible))
            {
                var other = new Rect(send.TranslatePoint(default, View)!.Value, send.Bounds.Size);
                Assert.False(box.Intersects(other), $"the pill {box} lies under the button {other}");
            }
        }

        public Control? Pill => View.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == "ReasoningButton");

        public void Dispose()
        {
            Window.Close();
            Chat.Dispose();
            Agent.Dispose();
        }
    }

    private static async Task Pump(int rounds = 10)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }

    private static async Task<Rig> OpenAsync(string? reasoning, bool dark, double width, string language = "en")
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new ProfileWith(reasoning)))
            .UseAgent("demo-assistant")
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals(broker)
            .Build();
        var chat = new AgentChatViewModel(agent, broker, new AvaloniaUiDispatcher(),
            new AgentChatOptions { AutomaticLanguage = CultureInfo.GetCultureInfo(language), Preferences = new MemoryPreferenceStore() });
        await chat.InitializeAsync();
        var view = new ChatView { DataContext = chat };
        var window = new Window { Width = width, Height = 560, Content = view };
        window.Show();
        await Pump();
        return new Rig { Window = window, View = view, Chat = chat, Agent = agent };
    }

    private static async Task Save(Rig rig, string name)
    {
        await Pump();
        Directory.CreateDirectory(Out);
        rig.Window.CaptureRenderedFrame()!.Save(Path.Combine(Out, name + ".png"), new PngBitmapEncoderOptions());
    }

    private const string Capped = """{"max":"low"}""";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Five_levels_sit_beside_the_tool_mode_inside_a_300_px_panel(bool dark)
    {
        var theme = dark ? "dark" : "light";
        using var rig = await OpenAsync(null, dark, 300);
        var pill = rig.Pill!;
        Assert.True(pill.IsVisible);
        Assert.True(pill.Bounds.Width > 40);
        rig.AssertPillFits();
        await Save(rig, $"pill-five-{theme}-300");

        rig.Chat.IsReasoningMenuOpen = true;
        await Pump(14);
        await Save(rig, $"menu-five-{theme}-300");
        // Keyboard: the rows are buttons the Tab key reaches, with names.
        Assert.Equal(5, rig.Chat.ReasoningOptions.Count);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_maximum_hides_the_levels_above_it(bool dark)
    {
        var theme = dark ? "dark" : "light";
        using var rig = await OpenAsync(Capped, dark, 300);
        Assert.True(rig.Pill!.IsVisible);
        rig.AssertPillFits();
        Assert.Equal("Auto", rig.Chat.ReasoningPillText);
        await Save(rig, $"pill-max-{theme}-300");
        rig.Chat.IsReasoningMenuOpen = true;
        await Pump(14);
        await Save(rig, $"menu-max-{theme}-300");
        Assert.Equal(new[] { "Auto", "Off", "Low" }, rig.Chat.ReasoningOptions.Select(o => o.Label));
    }

    [AvaloniaFact]
    public async Task The_longest_words_still_fit_in_german_and_at_300_px()
    {
        using var rig = await OpenAsync(null, false, 300, "de");
        rig.Chat.SelectReasoningCommand.Execute(rig.Chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Medium));
        rig.Chat.SelectedToolMode = rig.Chat.ToolModes.Single(m => m.Mode == Fadenstack.Tools.ToolMode.ReadOnly);
        await Pump();
        rig.AssertPillFits();
        await Save(rig, "pill-de-readonly-300");
        rig.Chat.IsReasoningMenuOpen = true;
        await Pump(14);
        await Save(rig, "menu-de-300");
    }
}

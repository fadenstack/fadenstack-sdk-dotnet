using System.Globalization;
using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// "Stop everything" and the notice of an agent the organisation turned off, in the Avalonia skin: the icon button's name, tooltip and result line,
/// and the chat's notice with "Check again" and a disabled composer, in every language.
/// </summary>
public class StopEverythingViewTests
{
    public static TheoryData<string> Each() => new("en", "de", "fr", "es", "zh-Hans");

    private sealed class Server : HttpMessageHandler
    {
        public volatile bool Off = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
                ? """{"id":"a","slug":"word","name":"Word assistant","version":1,"enabled":""" + (Off ? "false" : "true")
                  + ""","host_tool_contract":[],"local_mcp":{"mode":"disable","developer_mode":false},"tool_modes":{"default":"ask","ceiling":null},"required_client_modules":[],"edition":"community","features":[]}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (FadenstackAgent Agent, Server Server, ApprovalBroker Broker) NewAgent(bool off)
    {
        var server = new Server { Off = off };
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(server))
            .UseAgent("word").UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).UseApprovals(broker).Build();
        return (agent, server, broker);
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
        Assert.True(done(), "the condition did not hold in time");
    }

    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaTheory]
    [MemberData(nameof(Each))]
    public async Task The_chat_shows_the_notice_and_check_again_with_the_composer_off_and_clears_it_when_the_agent_is_back(string language)
    {
        var (agent, server, broker) = NewAgent(off: true);
        using var _ = agent;
        var chat = new AgentChatViewModel(agent, broker, new AvaloniaUiDispatcher(), new AgentChatOptions
        {
            AutomaticLanguage = CultureInfo.GetCultureInfo(language), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero,
        });
        await chat.InitializeAsync();
        var view = new ChatView { DataContext = chat };
        var window = new Window { Width = 420, Height = 640, Content = view };
        window.Show();
        await UntilAsync(() => view.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AgentOffNotice"));
        var notice = Find<Border>(view, "AgentOffNotice");
        var composer = Find<TextBox>(view, "Composer");
        var button = Find<Button>(view, "CheckAgainButton");
        var strings = chat.Strings;

        Assert.True(notice.IsEffectivelyVisible);
        Assert.False(composer.IsEnabled);
        Assert.Equal(strings.AgentTurnedOffCheckAgain, button.Content);
        Assert.Equal(strings.AgentTurnedOffCheckAgain, AutomationProperties.GetName(button));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == strings.AgentTurnedOff && t.IsEffectivelyVisible);

        server.Off = false;
        button.Command!.Execute(null);
        await UntilAsync(() => !chat.IsAgentOff);
        await UntilAsync(() => !notice.IsEffectivelyVisible && composer.IsEnabled);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Each))]
    public async Task The_stop_button_has_its_name_and_tooltip_and_says_what_it_did_in_every_language(string language)
    {
        var (agent, _, _) = NewAgent(off: false);
        using var _ = agent;
        await agent.StartAsync();
        var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        var model = new StopEverythingViewModel(() => new[] { agent }, new AvaloniaUiDispatcher(), () => strings);
        var view = new StopEverythingButton { DataContext = model };
        var window = new Window { Width = 480, Height = 120, Content = view };
        window.Show();
        await UntilAsync(() => view.GetVisualDescendants().OfType<Button>().Any());
        var button = Find<Button>(view, "StopButton");
        var label = Find<TextBlock>(view, "ResultLabel");

        Assert.Equal(strings.StopEverything, AutomationProperties.GetName(button));
        Assert.Equal(strings.StopEverythingTip, ToolTip.GetTip(button));
        Assert.False(label.IsEffectivelyVisible);

        button.Command!.Execute(null);
        await UntilAsync(() => model.Result is not null && !model.IsBusy && label.Text == strings.StopEverythingNothing);

        Assert.True(label.IsEffectivelyVisible);
        window.Close();
    }
}

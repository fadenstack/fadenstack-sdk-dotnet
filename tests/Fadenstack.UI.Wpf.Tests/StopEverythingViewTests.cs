using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Services;
using Fadenstack.UI.Settings;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// "Stop everything" and the notice of an agent the organisation turned off, in the Office-looking skin: the icon button's name, tooltip and result
/// line, and the chat's notice with "Check again" and a disabled composer, in every language, without a binding error.
/// </summary>
public class StopEverythingViewTests
{
    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    public static TheoryData<string> Each() => new(Languages);

    private sealed class Server : HttpMessageHandler
    {
        public volatile bool Off = true;

        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
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

    /// <summary>Lets the dispatcher run what is queued until <paramref name="done"/> holds (at most about three seconds).</summary>
    private static void Until(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        Assert.True(done(), "the condition did not hold in time");
    }

    private static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);

    [Theory]
    [MemberData(nameof(Each))]
    public void The_chat_shows_the_notice_and_check_again_with_the_composer_off_and_clears_it_when_the_agent_is_back(string language) => Sta.Run(() =>
    {
        var (agent, server, broker) = NewAgent(off: true);
        using var _ = agent;
        var chat = new AgentChatViewModel(agent, broker, new WpfDispatcher(), new AgentChatOptions
        {
            AutomaticLanguage = new System.Globalization.CultureInfo(language), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero,
        });
        chat.InitializeAsync().GetAwaiter().GetResult();
        var view = new ChatView { DataContext = chat, Width = 420, Height = 560 };
        view.Measure(new Size(420, 560));
        view.Arrange(new Rect(0, 0, 420, 560));
        view.UpdateLayout();
        var strings = chat.Strings;
        var notice = Named<Border>(view, "AgentOffNotice");
        var composer = Named<TextBox>(view, "Composer");
        var button = Named<Button>(view, "CheckAgainButton");

        Assert.Equal(Visibility.Visible, notice.Visibility);
        Assert.False(composer.IsEnabled);
        Assert.Equal(strings.AgentTurnedOff, chat.AgentOffText);
        Assert.Equal(strings.AgentTurnedOffCheckAgain, button.Content);
        Assert.Equal(strings.AgentTurnedOffCheckAgain, AutomationProperties.GetName(button));
        Assert.True(button.Command.CanExecute(null));

        server.Off = false;
        button.Command.Execute(null);
        Until(() => !chat.IsAgentOff);
        Until(() => notice.Visibility == Visibility.Collapsed);
        view.UpdateLayout();

        Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Assert.True(composer.IsEnabled);
    });

    [Theory]
    [MemberData(nameof(Each))]
    public void The_ai_notice_is_the_message_field_s_help_text_so_Narrator_says_it_on_focus(string language) => Sta.Run(() =>
    {
        // AI Act Art. 50(5): at the first interaction. Measured by the apps session: Narrator reached the notice only six items
        // after the field.
        var (agent, _, broker) = NewAgent(off: false);
        using var _ = agent;
        var chat = new AgentChatViewModel(agent, broker, new WpfDispatcher(), new AgentChatOptions
        {
            AutomaticLanguage = new System.Globalization.CultureInfo(language), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero,
        });
        chat.InitializeAsync().GetAwaiter().GetResult();
        var view = new ChatView { DataContext = chat, Width = 420, Height = 560 };
        view.Measure(new Size(420, 560));
        view.Arrange(new Rect(0, 0, 420, 560));
        view.UpdateLayout();

        Assert.False(string.IsNullOrEmpty(chat.Strings.AiNotice));
        Assert.Equal(chat.Strings.AiNotice, AutomationProperties.GetHelpText(Named<TextBox>(view, "Composer")));
    });

    [Theory]
    [MemberData(nameof(Each))]
    public void The_stop_button_has_its_name_and_tooltip_and_says_what_it_did_in_every_language(string language) => Sta.Run(() =>
    {
        var (agent, _, _) = NewAgent(off: false);
        using var _ = agent;
        agent.StartAsync().GetAwaiter().GetResult();
        var strings = UiStrings.For(new System.Globalization.CultureInfo(language));
        var model = new StopEverythingViewModel(() => new[] { agent }, new WpfDispatcher(), () => strings);
        var view = new StopEverythingButton { DataContext = model };
        var window = new Window { Content = view, Width = 480, Height = 120, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        window.Show();
        try
        {
            var button = Named<Button>(view, "StopButton");
            var label = Named<TextBlock>(view, "ResultLabel");

            Assert.Equal(strings.StopEverything, AutomationProperties.GetName(button));
            Assert.Equal(strings.StopEverythingTip, button.ToolTip);
            Assert.Equal(Visibility.Collapsed, label.Visibility); // nothing said before the first press

            button.Command.Execute(null);
            Until(() => model.Result is not null && !model.IsBusy);
            Until(() => label.Text == strings.StopEverythingNothing);

            Assert.Equal(strings.StopEverythingNothing, label.Text);
            Assert.Equal(Visibility.Visible, label.Visibility);
        }
        finally
        {
            window.Close();
        }
    });
}

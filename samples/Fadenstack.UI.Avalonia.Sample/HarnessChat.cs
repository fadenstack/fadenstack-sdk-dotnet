using System.Globalization;
using System.Net.Http;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Samples.Wpf;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
/// A real <see cref="AgentChatViewModel"/> on a seeded session: the demo gateway answers, the session store is in memory
/// and holds <see cref="SyntheticSession"/>. Nothing here is faked on the view side.
/// </summary>
internal sealed class HarnessChat : IDisposable
{
    public const string SessionId = "s-synthetic";

    private readonly FadenstackAgent _agent;

    private HarnessChat(FadenstackAgent agent, AgentChatViewModel chat, HarnessGateway gateway)
    {
        _agent = agent;
        Chat = chat;
        Gateway = gateway;
    }

    public AgentChatViewModel Chat { get; }

    public HarnessGateway Gateway { get; }

    /// <summary>Seeds <paramref name="turns"/> turns and starts the chat on them (the session is not opened yet).</summary>
    public static Task<HarnessChat> CreateAsync(int turns, IUiDispatcher dispatcher, IClipboard? clipboard = null) =>
        CreateAsync(store => SyntheticSession.SeedAsync(store, DemoGateway.Agent, SessionId, turns), dispatcher, clipboard);

    /// <summary>Starts the chat on a session <paramref name="seed"/> writes (under <see cref="SessionId"/>).</summary>
    public static async Task<HarnessChat> CreateAsync(Func<ISessionStore, Task> seed, IUiDispatcher dispatcher, IClipboard? clipboard = null)
    {
        var store = new MemorySessionStore();
        await seed(store);
        var gateway = new HarnessGateway();
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(gateway))
            .UseAgent(DemoGateway.Agent)
            .UseSessionStore(store)
            .UseApprovalStore(new MemoryApprovalStore())
            .AddTools(new Fadenstack.Samples.DemoTools())
            .UseApprovals(broker)
            .UseHistoryBudget(3000) // the model sees the newest turns, not ten thousand
            .Build();
        var chat = new AgentChatViewModel(agent, broker, dispatcher,
            new AgentChatOptions { Clipboard = clipboard ?? NoClipboard.Instance, AutomaticLanguage = CultureInfo.GetCultureInfo("en"), Preferences = new MemoryPreferenceStore() });
        await chat.InitializeAsync();
        return new HarnessChat(agent, chat, gateway);
    }

    public void Dispose()
    {
        Chat.Dispose();
        _agent.Dispose();
    }
}

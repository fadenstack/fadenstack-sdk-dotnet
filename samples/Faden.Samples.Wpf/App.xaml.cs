using System.Net.Http;
using System.Windows;
using Faden.Agents;
using Faden.UI.Services;
using Faden.UI.ViewModels;
using Faden.UI.Wpf.Services;

namespace Faden.Samples.Wpf;

/// <summary>
/// The composition root: builds the agent, the chat view model and the window. Nothing else has code.
///
///   Faden.Samples.Wpf.exe --demo                                   (no server: a built-in demo gateway)
///   Faden.Samples.Wpf.exe --server https://faden.example --agent eplan-assistant [--api ...] [--login-token ...]
/// </summary>
public partial class App : Application
{
    private AgentChatViewModel? _chat;
    private FadenAgent? _agent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name, string env)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable(env);
        }

        var broker = new ApprovalBroker();
        var builder = FadenAgent.CreateBuilder().AddTools(new Faden.Samples.DemoTools()).UseApprovals(broker);
        if (args.Contains("--demo") || Arg("server", "FADEN_SERVER") is null)
        {
            builder.UseServer(new Uri("https://demo.invalid"), new Faden.Auth.StaticTokenProvider("demo"),
                    o => o.HttpClient = new HttpClient(new DemoGateway()))
                .UseAgent(DemoGateway.Agent)
                .UseSessionStore(new Faden.Agents.Sessions.MemorySessionStore())
                .UseApprovalStore(new MemoryApprovalStore());
        }
        else
        {
            var slug = Arg("agent", "FADEN_AGENT") ?? throw new ArgumentException("--agent is required.");
            var client = FadenClient.ForAgent(new Uri(Arg("server", "FADEN_SERVER")!), slug, out var tokens,
                configure: o => o.ApiUrl = Arg("api", "FADEN_API") is { } api ? new Uri(api) : null);
            if (!await tokens.IsSignedInAsync() && Arg("login-token", "FADEN_LOGIN_TOKEN") is { } login)
            {
                await tokens.SignInAsync(login, $"{Environment.MachineName} (WPF sample)");
            }
            builder.UseClient(client).UseAgent(slug);
        }
        _agent = builder.Build();
        _chat = new AgentChatViewModel(_agent, broker, new WpfDispatcher(), new AgentChatOptions { Clipboard = new WpfClipboard() });
        MainWindow = new MainWindow { DataContext = _chat };
        MainWindow.Show();
        await _chat.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _chat?.Dispose();
        _agent?.Dispose();
        base.OnExit(e);
    }
}

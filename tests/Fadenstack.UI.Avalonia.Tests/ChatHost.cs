using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents.Sessions;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>A <see cref="ChatView"/> in a headless window on a real view model (the harness' session and gateway).</summary>
internal sealed class ChatHost : IDisposable
{
    private ChatHost(Window window, ChatView view, HarnessChat harness)
    {
        Window = window;
        View = view;
        Harness = harness;
    }

    public Window Window { get; }

    public ChatView View { get; }

    public HarnessChat Harness { get; }

    public AgentChatViewModel Chat => Harness.Chat;

    public ScrollViewer Scroller => View.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");

    public ItemsControl TurnList => View.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "Turns");

    /// <summary>Realized turn containers: what the panel holds, whatever the session's length.</summary>
    public int Realized => TurnList.ItemsPanelRoot!.Children.Count;

    public static async Task<ChatHost> OpenAsync(int turns, bool openSession = true, double width = 480, double height = 800) =>
        await OpenAsync(HarnessChat.CreateAsync(turns, new AvaloniaUiDispatcher()), openSession, width, height);

    public static async Task<ChatHost> OpenAsync(Func<ISessionStore, Task> seed, bool openSession = true) =>
        await OpenAsync(HarnessChat.CreateAsync(seed, new AvaloniaUiDispatcher()), openSession, 480, 800);

    private static async Task<ChatHost> OpenAsync(Task<HarnessChat> creating, bool openSession, double width, double height)
    {
        var harness = await creating;
        var view = new ChatView { DataContext = harness.Chat };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        var host = new ChatHost(window, view, harness);
        if (openSession)
        {
            await harness.Chat.OpenSessionAsync(HarnessChat.SessionId);
        }
        await host.PumpAsync();
        return host;
    }

    /// <summary>Lets the dispatcher run what is queued (layout, render, the render batcher's flushes) a few times over.</summary>
    public async Task PumpAsync(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
    }

    /// <summary>Pumps until <paramref name="done"/> holds (at most about two seconds).</summary>
    public async Task UntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await PumpAsync(1);
        }
        Assert.True(done(), "the condition did not hold in time");
    }

    public bool AtEnd => Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 2;

    /// <summary>Where a turn's container is in the viewer, or null when it is not realized.</summary>
    public double? TopOf(TurnViewModel turn) => TurnList.ContainerFromItem(turn)?.TranslatePoint(default, Scroller)?.Y;

    public void Dispose()
    {
        Window.Close();
        Harness.Dispose();
    }
}

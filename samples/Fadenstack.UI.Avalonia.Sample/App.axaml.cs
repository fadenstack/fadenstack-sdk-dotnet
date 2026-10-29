using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>The composition root: builds the chat view model on a seeded session and the window. Nothing else has code.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await StartAsync(window, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var options = Program.Options;
        using var harness = await HarnessChat.CreateAsync(options.Turns, new AvaloniaUiDispatcher(), new AvaloniaClipboard(() => TopLevel.GetTopLevel(window.ChatPanel)));
        window.ChatPanel.DataContext = harness.Chat;
        if (options.Turns > 0 && !options.Bench)
        {
            await harness.Chat.OpenSessionAsync(HarnessChat.SessionId);
            await Task.Delay(Timeout.Infinite);
        }
        if (options.Bench)
        {
            var results = await new Benchmark(window, window.ChatPanel, harness).RunAsync(options.Turns, options.Only);
            var table = Benchmark.Table(results);
            Console.WriteLine(table);
            if (options.Output is not null)
            {
                File.WriteAllText(options.Output, table);
            }
            desktop.Shutdown();
        }
    }
}

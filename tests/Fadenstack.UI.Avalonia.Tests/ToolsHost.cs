using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Tests.ToolsView;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The Tools view in a headless window over the real view model and the fake catalog in one of the states of <see cref="ToolsScenes"/>
/// (the way a person gets to it), in a language of the test's choice. Binding errors the view logs while it runs are kept in
/// <see cref="BindingErrors"/>.
/// </summary>
internal sealed class ToolsHost : IDisposable
{
    private readonly ILogSink? _sink;
    private readonly CaptureSink _capture = new();

    private ToolsHost(ToolsScene scene, double width, double height)
    {
        Scene = scene;
        View = new ToolsView { DataContext = scene.Page };
        Window = new Window { Width = width, Height = height, Content = View };
        _sink = Logger.Sink;
        Logger.Sink = _capture;
    }

    public ToolsScene Scene { get; }

    public ToolsViewModel Page => Scene.Page;

    public FakeLocalToolsCatalog Catalog => Scene.Catalog;

    public Window Window { get; }

    public ToolsView View { get; }

    public IReadOnlyList<string> BindingErrors => _capture.Errors;

    public static async Task<ToolsHost> OpenAsync(string state, string language = "en", double width = 380, double height = 900)
    {
        var host = new ToolsHost(ToolsScenes.Create(state, language), width, height);
        host.Window.Show();
        await host.PumpAsync();
        return host;
    }

    public async Task PumpAsync(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
    }

    /// <summary>Everything the view shows right now: its controls that are effectively visible.</summary>
    public IEnumerable<Control> Visible() => View.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible);

    /// <summary>The texts on screen: text blocks (a button's and a check box's label are text blocks too).</summary>
    public IReadOnlyList<string> Texts() => Visible().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    public T Find<T>(string name) where T : Control => View.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    public bool Has(string name) => View.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    public object? Focused() => Window.FocusManager?.GetFocusedElement();

    public void Dispose()
    {
        Logger.Sink = _sink;
        Window.Close();
        Scene.Dispose();
    }

    /// <summary>Keeps what the view's bindings complain about.</summary>
    private sealed class CaptureSink : ILogSink
    {
        public List<string> Errors { get; } = new();

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate + " " + string.Join(", ", propertyValues));
            }
        }
    }
}

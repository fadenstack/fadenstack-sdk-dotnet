using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Endpoints;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// A <see cref="WizardView"/> in a headless window on a real <see cref="ConnectionWizardViewModel"/> with the wizard's fakes
/// (the same ones the view model's own tests use), in a language of the test's choice. Binding errors the view logs while it
/// runs are kept in <see cref="BindingErrors"/>.
/// </summary>
internal sealed class WizardHost : IDisposable
{
    private static readonly PropertyInfo StepProperty = typeof(ConnectionWizardViewModel).GetProperty(nameof(ConnectionWizardViewModel.Step))!;

    private readonly ILogSink? _sink;
    private readonly CaptureSink _capture = new();

    private WizardHost(string language, WizardOptions options, double width, double height)
    {
        Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        Options = options;
        Sign = new FakeServerSignIn(() => Clock.Now);
        Wizard = new ConnectionWizardViewModel(options, Store, Sign, Keys, Checker, Clock, new AvaloniaUiDispatcher(), Clipboard, Links,
            () => Strings);
        View = new WizardView { DataContext = Wizard, AppName = options.AppName };
        Window = new Window { Width = width, Height = height, Content = View };
        _sink = Logger.Sink;
        Logger.Sink = _capture;
    }

    public UiStrings Strings { get; }

    public WizardOptions Options { get; }

    public Window Window { get; }

    public WizardView View { get; }

    public ConnectionWizardViewModel Wizard { get; }

    public FakeConnectionStore Store { get; } = new();

    public FakeServerSignIn Sign { get; }

    public FakeKeyStore Keys { get; } = new();

    public FakeEndpointChecker Checker { get; } = new();

    public ManualClock Clock { get; } = new();

    public FakeLinks Links { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public IReadOnlyList<string> BindingErrors => _capture.Errors;

    public static WizardOptions DefaultOptions { get; } = new()
    {
        AppName = "Test app", TermsVersion = "2026-11", Agents = new[] { "main-agent" }, PasswordFallback = true, OffersDemo = true,
    };

    public static async Task<WizardHost> OpenAsync(Func<WizardOptions, WizardOptions>? configure = null, string language = "en",
        bool start = false, double width = 420, double height = 800)
    {
        var host = new WizardHost(language, configure is null ? DefaultOptions : configure(DefaultOptions), width, height);
        host.Window.Show();
        if (start)
        {
            _ = host.Wizard.StartAsync();
        }
        await host.PumpAsync();
        return host;
    }

    /// <summary>Puts the view model at a step without walking there (its setter is private): for the views of steps that last a moment.</summary>
    public async Task ShowAsync(WizardStep step)
    {
        StepProperty.GetSetMethod(true)!.Invoke(Wizard, new object[] { step });
        await PumpAsync();
    }

    /// <summary>Sets one of the view model's state properties (their setters are private).</summary>
    public void Set(string property, object? value) =>
        typeof(ConnectionWizardViewModel).GetProperty(property)!.GetSetMethod(true)!.Invoke(Wizard, new[] { value });

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

    public void Dispose()
    {
        Logger.Sink = _sink;
        Wizard.Dispose();
        Window.Close();
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

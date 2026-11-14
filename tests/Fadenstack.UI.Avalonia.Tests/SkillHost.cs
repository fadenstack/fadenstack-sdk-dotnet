using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Tests;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// One of the skill views (the library, the editor or the draft) in a headless window on the real view models over a real workshop in a
/// temporary folder (<see cref="SkillRig"/>), in a language of the test's choice. Binding errors the view logs while it runs are kept in
/// <see cref="BindingErrors"/>.
/// </summary>
internal sealed class SkillHost : IDisposable
{
    private readonly ILogSink? _sink;
    private readonly CaptureSink _capture = new();

    private SkillHost(string language, Func<SkillHost, Control> view, SkillRig rig, double width, double height)
    {
        Rig = rig;
        Language = language;
        Strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        View = view(this);
        Window = new Window { Width = width, Height = height, Content = View };
        _sink = Logger.Sink;
        Logger.Sink = _capture;
    }

    public SkillRig Rig { get; }

    public string Language { get; }

    public UiStrings Strings { get; }

    public Window Window { get; }

    public Control View { get; }

    public SkillLibraryViewModel Library { get; private set; } = null!;

    public SkillEditorViewModel Editor { get; private set; } = null!;

    public SkillDraftViewModel Draft { get; private set; } = null!;

    public IReadOnlyList<string> BindingErrors => _capture.Errors;

    private SkillLibraryViewModel NewLibrary(IChatClient? model) =>
        new(Rig.Workshop, Rig.Files, model, new AvaloniaUiDispatcher(), () => Strings);

    /// <summary>The library view over a library that has read its skills (the bundled one and, if asked, the person's own).</summary>
    public static async Task<SkillHost> OpenLibraryAsync(string language = "en", bool own = true, bool bundled = true, IChatClient? model = null,
        double width = 560, double height = 900)
    {
        var rig = new SkillRig();
        if (!bundled)
        {
            rig.Options.Bundled.Clear();
        }
        if (own)
        {
            await rig.Real.SaveAsync(SkillRig.Doc("contract-review", "Reviews a contract clause by clause."));
        }
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            return new SkillLibraryView { DataContext = h.Library, Strings = h.Strings };
        }, rig, width, height);
        await host.Library.RefreshAsync();
        await host.LoadedAsync();
        host.Window.Show();
        await host.PumpAsync();
        return host;
    }

    /// <summary>The editor view over a new skill (<paramref name="name"/> empty), one of the person's own, or the bundled one to read.</summary>
    public static async Task<SkillHost> OpenEditorAsync(string language = "en", string mode = "new", IChatClient? model = null, double width = 560, double height = 1400)
    {
        var rig = new SkillRig();
        if (mode == "edit")
        {
            await rig.Real.SaveAsync(SkillRig.Doc("contract-review"));
        }
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            return new SkillEditorView { Strings = h.Strings };
        }, rig, width, height);
        await host.Library.RefreshAsync();
        await host.LoadedAsync();
        switch (mode)
        {
            case "edit":
                await host.Library.EditCommand.ExecuteAsync(host.Library.Rows.First(r => r.Name == "contract-review"));
                break;
            case "view":
                await host.Library.EditCommand.ExecuteAsync(host.Library.Rows.First(r => r.Name == "slide-design"));
                break;
            default:
                host.Library.NewCommand.Execute(null);
                break;
        }
        await host.PumpAsync();
        host.Editor = host.Library.Editor!;
        host.View.DataContext = host.Editor;
        host.Window.Show();
        await host.PumpAsync();
        return host;
    }

    /// <summary>The draft view over a draft from a short chat; the model answers as scripted (null: none).</summary>
    public static async Task<SkillHost> OpenDraftAsync(string language = "en", IChatClient? model = null, bool chat = true, double width = 420, double height = 600)
    {
        var rig = new SkillRig();
        var host = new SkillHost(language, h =>
        {
            h.Library = h.NewLibrary(model);
            h.Draft = h.Library.CreateDraft(() => chat
                ? new[] { new SkillDraftMessage(true, "Please review this contract."), new SkillDraftMessage(false, "Clause 4 caps liability.") }
                : Array.Empty<SkillDraftMessage>());
            return new SkillDraftView { DataContext = h.Draft, Strings = h.Strings };
        }, rig, width, height);
        host.Window.Show();
        await host.PumpAsync();
        return host;
    }

    /// <summary>The library's list arrives through the dispatcher (a pool thread posts it): wait for it.</summary>
    private async Task LoadedAsync()
    {
        for (var i = 0; i < 200 && Library.Rows.Count == 0 && !Library.IsEmpty; i++)
        {
            await PumpAsync(1);
        }
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

    /// <summary>The visible button that says <paramref name="text"/>.</summary>
    public Button ButtonSaying(string text) => Visible().OfType<Button>().First(b => b.Content as string == text);

    public IEnumerable<Button> ButtonsSaying(string text) => Visible().OfType<Button>().Where(b => b.Content as string == text);

    public void Dispose()
    {
        Logger.Sink = _sink;
        Window.Close();
        Rig.Dispose();
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

using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The tests that point <see cref="Fadenstack.Storage.FadenstackPaths"/> at a temporary folder run alone: the root is process-wide, and a
/// test running beside them (an agent with default stores) would put its stores in a folder that is deleted under it.
/// </summary>
[CollectionDefinition("File logger in the process", DisableParallelization = true)]
public sealed class FileLoggerInTheProcessCollection;

/// <summary>
/// "Report a problem" in the WPF skin (SDK 0.3.1): About's button, the sheet over the chat with its buttons and read-only preview, the
/// focus that moves into it and back to what opened it, Escape and Close, and the link on a failed turn. The tests run in a window far off screen.
/// </summary>
[Collection("File logger in the process")]
public class ReportProblemViewTests
{
    private sealed class Host : IDisposable
    {
        private readonly TraceListener _trace = new Trace();

        public Host(string? policy, string language = "en")
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            var broker = new ApprovalBroker();
            var builder = FadenstackAgent.CreateBuilder()
                .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new NoNetwork()))
                .UseAgent("word").UseHookContent()
                .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore()).UseApprovals(broker);
            if (policy is not null)
            {
                builder.UseMachinePolicy(MachinePolicy.FromJson(policy));
            }
            Clipboard = new NoClipboard();
            Agent = builder.Build();
            Chat = new AgentChatViewModel(Agent, broker, new WpfDispatcher(), new AgentChatOptions
            {
                AutomaticLanguage = new CultureInfo(language), Preferences = new MemoryPreferenceStore(), RenderInterval = TimeSpan.Zero, Clipboard = Clipboard,
            });
            View = new ChatView { DataContext = Chat };
            Window = new Window
            {
                Width = 480, Height = 640, Content = View, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            Window.Show();
            Pump();
        }

        public NoClipboard Clipboard { get; }

        public FadenstackAgent Agent { get; }

        public AgentChatViewModel Chat { get; }

        public ChatView View { get; }

        public Window Window { get; }

        public List<string> BindingErrors => ((Trace)_trace).Errors;

        public void Pump()
        {
            // Twice: layout raises Loaded and Unloaded, and what they queue (the focus moving in and back) runs on the next turn.
            for (var i = 0; i < 2; i++)
            {
                Window.UpdateLayout();
                Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>A named element of the view, also inside a template, or null: the sheet is in the tree only while it is open.</summary>
        public T? Named<T>(string name) where T : class
        {
            foreach (var d in Descendants(View))
            {
                if (d is FrameworkElement { } e && e.Name == name && e is T found)
                {
                    return found;
                }
                if (d is TextBlock text)
                {
                    foreach (var link in text.Inlines.OfType<Hyperlink>())
                    {
                        if (link.Name == name && link is T inlineFound)
                        {
                            return inlineFound;
                        }
                    }
                }
            }
            return null;
        }

        public bool IsShown(string name) => Named<FrameworkElement>(name) is { IsVisible: true };

        public void Press(UIElement target, Key key)
        {
            var source = PresentationSource.FromVisual(target)!;
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var inner in Descendants(child))
                {
                    yield return inner;
                }
            }
        }

        public void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;
            Window.Close();
            Chat.Dispose();
            Agent.Dispose();
        }

        private sealed class Trace : TraceListener
        {
            private string _line = "";

            public List<string> Errors { get; } = new();

            public override void Write(string? message) => _line += message;

            public override void WriteLine(string? message)
            {
                Errors.Add(_line + message);
                _line = "";
            }
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in a view test");
    }

    private const string Mail = """{"supportEmail":"it-help@example.org"}""";

    [Fact]
    public void About_has_a_Report_a_problem_button_that_opens_the_sheet_and_the_sheet_is_not_there_before() => Sta.Run(() =>
    {
        using var host = new Host(Mail);
        host.Chat.OpenAboutCommand.Execute(null);
        host.Pump();

        var button = host.Named<Button>("AboutReport")!;
        Assert.True(button.IsVisible);
        Assert.Same(host.Chat.OpenReportCommand, button.Command);
        Assert.Equal("Report a problem", button.Content);
        Assert.False(host.IsShown("ReportSheet"));
        Assert.Null(host.Named<Border>("ReportSheet"));

        button.Command!.Execute(null);
        host.Pump();

        Assert.True(host.IsShown("ReportSheet"));
        Assert.Equal("Report a problem", AutomationProperties.GetName(host.Named<Border>("ReportSheet")!));
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void With_an_address_the_sheet_shows_where_it_goes_the_preview_and_every_button_with_a_name() => Sta.Run(() =>
    {
        using var host = new Host(Mail);
        host.Chat.OpenReportCommand.Execute(null);
        host.Pump();

        Assert.Equal("This goes to it-help@example.org.", host.Named<TextBlock>("ReportGoesTo")!.Text);
        Assert.False(host.IsShown("ReportAskIt"));
        Assert.False(((TextBlock)host.Named<Hyperlink>("ReportHelpLink")!.Parent).IsVisible); // the organisation's Help only without an address
        var preview = host.Named<TextBox>("ReportPreview")!;
        Assert.True(preview.IsReadOnly);
        Assert.Equal("This report contains:", AutomationProperties.GetName(preview));
        Assert.Contains("What happened:", preview.Text);
        Assert.Contains("Mode: Organisation", preview.Text);
        Assert.Contains("Server: https://demo.invalid", preview.Text);
        foreach (var name in new[] { "ReportWriteMail", "ReportCopy", "ReportCloseButton" })
        {
            var button = host.Named<Button>(name)!;
            Assert.True(button.IsVisible, name);
            Assert.False(string.IsNullOrWhiteSpace(button.Content as string), name);
        }
        Assert.False(host.IsShown("ReportOpenLogs")); // no file log in this process
        Assert.Same(host.Chat.Report.WriteMailCommand, host.Named<Button>("ReportWriteMail")!.Command);
        Assert.Equal("Write mail", host.Named<Button>("ReportWriteMail")!.Content);
        Assert.Equal("Copy details", host.Named<Button>("ReportCopy")!.Content);
        Assert.Equal("Close", AutomationProperties.GetName(host.Named<Button>("ReportClose")!));

        host.Named<Button>("ReportCopy")!.Command!.Execute(null);
        host.Pump();
        Assert.StartsWith("To: it-help@example.org\nSubject: ", host.Clipboard.LastText);
        Assert.Equal("Copied", host.Named<TextBlock>("ReportCopied")!.Text);
        Assert.True(host.IsShown("ReportCopied"));
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void Without_an_address_there_is_no_Write_mail_the_organisations_help_is_a_link_and_Copy_details_stays() => Sta.Run(() =>
    {
        using var host = new Host(null);
        host.Chat.OpenReportCommand.Execute(null);
        host.Pump();

        Assert.False(host.IsShown("ReportGoesTo"));
        Assert.Equal("Ask your organisation's IT for help. You can copy the details below for them.", host.Named<TextBlock>("ReportAskIt")!.Text);
        Assert.True(host.IsShown("ReportAskIt"));
        Assert.False(host.IsShown("ReportWriteMail"));
        Assert.True(host.IsShown("ReportCopy"));
        var help = host.Named<Hyperlink>("ReportHelpLink")!;
        Assert.Equal("https://demo.invalid/help/", ((PanelLink)help.CommandParameter).Url.AbsoluteUri);
        Assert.Same(host.Chat.OpenLinkCommand, help.Command);
        Assert.DoesNotContain("To:", host.Named<TextBox>("ReportPreview")!.Text);
        Assert.Empty(host.BindingErrors);
    });

    [Fact]
    public void The_focus_moves_into_the_sheet_Escape_closes_it_and_the_focus_goes_back_to_the_button_that_opened_it() => Sta.Run(() =>
    {
        using var host = new Host(Mail);
        host.Chat.OpenAboutCommand.Execute(null);
        host.Pump();
        var opener = host.Named<Button>("AboutReport")!;
        opener.Focus();
        host.Pump();
        Assert.Same(opener, Keyboard.FocusedElement);

        opener.Command!.Execute(null);
        host.Pump();

        var close = host.Named<Button>("ReportClose")!;
        Assert.Same(close, Keyboard.FocusedElement);
        Assert.True(host.Chat.IsAboutOpen); // the press in the sheet is not a press outside About

        host.Press(close, Key.Escape);

        Assert.False(host.Chat.Report.IsOpen);
        Assert.Null(host.Named<Border>("ReportSheet"));
        Assert.True(host.Chat.IsAboutOpen);
        Assert.Empty(host.BindingErrors); // the focus going back to the opener is DrawerFocus's Unloaded, which a window off screen never raises
    });

    [Fact]
    public void Close_closes_the_sheet_and_the_focus_goes_back() => Sta.Run(() =>
    {
        using var host = new Host(Mail);
        host.Chat.OpenAboutCommand.Execute(null);
        host.Pump();
        var opener = host.Named<Button>("AboutReport")!;
        opener.Focus();
        host.Chat.OpenReportCommand.Execute(null);
        host.Pump();

        host.Named<Button>("ReportCloseButton")!.Command!.Execute(null);
        host.Pump();

        Assert.False(host.IsShown("ReportSheet"));
    });

    [Fact]
    public void A_failed_turn_has_the_link_and_it_opens_the_sheet_with_the_errors_code() => Sta.Run(() =>
    {
        using var host = new Host(Mail);
        var make = typeof(TurnViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var turn = (TurnViewModel)make.Invoke(new object?[] { host.Chat, "Hello", DateTimeOffset.Now, 4000, TurnState.Running, false });
        typeof(TurnViewModel).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(turn, new object?[] { "The server answered 503.", DateTimeOffset.Now, "http_503" });
        host.Chat.Turns.Add(turn);
        host.Pump();

        var link = host.Named<Hyperlink>("ReportErrorLink")!;
        Assert.Same(turn.ReportCommand, link.Command);
        Assert.True(turn.CanReport);
        link.Command!.Execute(null);
        host.Pump();

        Assert.True(host.IsShown("ReportSheet"));
        Assert.Contains("Error: http_503", host.Named<TextBox>("ReportPreview")!.Text);
        Assert.DoesNotContain("The server answered 503.", host.Named<TextBox>("ReportPreview")!.Text);
        Assert.Empty(host.BindingErrors);
    });

    [Theory]
    [InlineData("de", "Ein Problem melden", "E-Mail schreiben")]
    [InlineData("fr", "Signaler un problème", "Écrire un e-mail")]
    [InlineData("es", "Informar de un problema", "Escribir correo")]
    [InlineData("zh-Hans", "报告问题", "撰写邮件")]
    public void The_sheet_speaks_the_panels_language(string language, string title, string writeMail) => Sta.Run(() =>
    {
        using var host = new Host(Mail, language);
        host.Chat.OpenReportCommand.Execute(null);
        host.Pump();

        Assert.Equal(title, AutomationProperties.GetName(host.Named<Border>("ReportSheet")!));
        Assert.Equal(writeMail, host.Named<Button>("ReportWriteMail")!.Content);
        Assert.Empty(host.BindingErrors);
    });

    private static List<string> Recording(out Action restore)
    {
        var started = new List<string>();
        var old = Fadenstack.UI.Wpf.Behaviors.MarkdownText.StartProcess;
        Fadenstack.UI.Wpf.Behaviors.MarkdownText.StartProcess = started.Add;
        restore = () => Fadenstack.UI.Wpf.Behaviors.MarkdownText.StartProcess = old;
        return started;
    }

    [Fact]
    public void A_link_in_an_answer_opens_web_links_only_and_the_sheets_own_links_open_through_the_panel() => Sta.Run(() =>
    {
        var started = Recording(out var restore);
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "faden-wpf-links-" + Guid.NewGuid().ToString("N"));
        using var root = Fadenstack.Storage.FadenstackPaths.UseRootForTests(folder);
        try
        {
            using var host = new Host(Mail);
            var hook = Fadenstack.UI.Wpf.Behaviors.MarkdownText.OpenLink;
            Assert.Same(Fadenstack.UI.Wpf.Behaviors.MarkdownText.DefaultOpenLink, hook);

            hook(new Uri("mailto:a@b.example?body=secret"));
            hook(new Uri("file:///C:/Windows"));
            hook(new Uri(System.IO.Path.GetFullPath(Fadenstack.Storage.FadenstackPaths.LogRoot) + System.IO.Path.DirectorySeparatorChar));
            Assert.Empty(started);
            hook(new Uri("https://fadenstack.com/"));
            Assert.Equal(new[] { "https://fadenstack.com/" }, started);
            started.Clear();

            // the sheet's own commands
            host.Chat.OpenReportCommand.Execute(null);
            host.Pump();
            host.Chat.Report.WriteMailCommand.Execute(null);
            Assert.Equal(host.Chat.Report.Mailto, Assert.Single(started));
            started.Clear();
            using (new Fadenstack.Logging.FadenstackFileLoggerProvider("wpf-links"))
            {
                host.Chat.Report.OpenLogFolderCommand.Execute(null);
            }
            Assert.Equal(System.IO.Path.GetFullPath(Fadenstack.Storage.FadenstackPaths.LogRoot).TrimEnd('\\', '/'), Assert.Single(started).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase);
            started.Clear();

            // a link the panel did not make is still refused by the panel's opener
            Fadenstack.UI.Wpf.Behaviors.MarkdownText.OpenPanelLink(new Uri("file:///C:/Windows"));
            Fadenstack.UI.Wpf.Behaviors.MarkdownText.OpenPanelLink(new Uri("ftp://example.org/"));
            Assert.Empty(started);

            // a host's own hook gets every panel link
            var seen = new List<Uri>();
            Fadenstack.UI.Wpf.Behaviors.MarkdownText.OpenLink = seen.Add;
            try
            {
                host.Chat.Report.WriteMailCommand.Execute(null);
            }
            finally
            {
                Fadenstack.UI.Wpf.Behaviors.MarkdownText.OpenLink = Fadenstack.UI.Wpf.Behaviors.MarkdownText.DefaultOpenLink;
            }
            Assert.Equal(host.Chat.Report.Mailto, Assert.Single(seen).OriginalString);
            Assert.Empty(started);
        }
        finally
        {
            restore();
            try
            {
                System.IO.Directory.Delete(folder, true);
            }
            catch (System.IO.IOException)
            {
            }
        }
    });
}

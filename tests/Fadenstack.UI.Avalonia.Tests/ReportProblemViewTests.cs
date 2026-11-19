using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The tests that point <see cref="Fadenstack.Storage.FadenstackPaths"/> at a temporary folder run alone: the root is process-wide, and a
/// test running beside them (an agent with default stores) would put its stores in a folder that is deleted under it.
/// </summary>
[CollectionDefinition("File logger in the process", DisableParallelization = true)]
public sealed class FileLoggerInTheProcessCollection;

/// <summary>"Report a problem" in the Avalonia skin (SDK 0.3.1): About's button, the sheet with its buttons and read-only preview, the focus into it, Escape and Close.</summary>
[Collection("File logger in the process")]
public class ReportProblemViewTests
{
    private static T? Named<T>(ChatHost host, string name) where T : Control =>
        host.View.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static object? Focused(ChatHost host) => TopLevel.GetTopLevel(host.View)!.FocusManager!.GetFocusedElement();

    [AvaloniaFact]
    public async Task About_has_the_button_the_sheet_is_not_there_before_and_has_every_part_named_after_it()
    {
        using var host = await ChatHost.OpenAsync(2);
        host.Chat.OpenAboutCommand.Execute(null);
        await host.PumpAsync();

        var button = Named<Button>(host, "AboutReport")!;
        Assert.True(button.IsEffectivelyVisible);
        Assert.Same(host.Chat.OpenReportCommand, button.Command);
        Assert.Equal("Report a problem", button.Content);
        Assert.Null(Named<Border>(host, "ReportSheet"));

        button.Command!.Execute(null);
        await host.PumpAsync();

        var sheet = Named<Border>(host, "ReportSheet")!;
        Assert.True(sheet.IsEffectivelyVisible);
        Assert.Equal("Report a problem", AutomationProperties.GetName(sheet));
        var preview = Named<TextBox>(host, "ReportPreview")!;
        Assert.True(preview.IsReadOnly);
        Assert.Equal("This report contains:", AutomationProperties.GetName(preview));
        Assert.Contains("What happened:", preview.Text);
        Assert.Contains("Mode: Organisation", preview.Text);
        // the harness has no machine policy: governed, so no address, no mail, the organisation's Help and the copy
        Assert.True(Named<TextBlock>(host, "ReportAskIt")!.IsEffectivelyVisible);
        Assert.False(Named<TextBlock>(host, "ReportGoesTo")!.IsEffectivelyVisible);
        Assert.False(Named<Button>(host, "ReportWriteMail")!.IsEffectivelyVisible);
        Assert.True(Named<Button>(host, "ReportHelpLink")!.IsEffectivelyVisible);
        Assert.False(Named<Button>(host, "ReportOpenLogs")!.IsEffectivelyVisible);
        foreach (var name in new[] { "ReportCopy", "ReportCloseButton" })
        {
            Assert.True(Named<Button>(host, name)!.IsEffectivelyVisible, name);
            Assert.False(string.IsNullOrWhiteSpace(Named<Button>(host, name)!.Content as string), name);
        }
        Assert.Equal("Close", AutomationProperties.GetName(Named<Button>(host, "ReportClose")!));

        Named<Button>(host, "ReportCopy")!.Command!.Execute(null);
        await host.PumpAsync();
        Assert.Equal("Copied", Named<TextBlock>(host, "ReportCopied")!.Text);
    }

    [AvaloniaFact]
    public async Task The_focus_moves_into_the_sheet_and_Escape_and_Close_close_it_with_About_still_open()
    {
        using var host = await ChatHost.OpenAsync(2);
        host.Chat.OpenAboutCommand.Execute(null);
        await host.PumpAsync();
        var opener = Named<Button>(host, "AboutReport")!;
        opener.Focus();
        await host.PumpAsync();

        opener.Command!.Execute(null);
        await host.PumpAsync();
        Assert.Same(Named<Button>(host, "ReportClose"), Focused(host));

        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        host.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await host.PumpAsync();
        Assert.False(host.Chat.Report.IsOpen);
        Assert.Null(Named<Border>(host, "ReportSheet"));
        Assert.True(host.Chat.IsAboutOpen);
        Assert.Same(opener, Focused(host));

        opener.Command!.Execute(null);
        await host.PumpAsync();
        Named<Button>(host, "ReportCloseButton")!.Command!.Execute(null);
        await host.PumpAsync();
        Assert.False(host.Chat.Report.IsOpen);
        Assert.Same(opener, Focused(host));
    }

    [AvaloniaFact]
    public async Task A_failed_turn_has_the_link_and_it_opens_the_sheet_with_the_errors_code()
    {
        using var host = await ChatHost.OpenAsync(2);
        var turn = host.Chat.Turns[^1];
        typeof(Fadenstack.UI.ViewModels.TurnViewModel).GetMethod("Fail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(turn, new object?[] { "The server answered 503.", DateTimeOffset.Now, "http_503" });
        await host.PumpAsync();

        var link = host.View.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ReportErrorLink" && b.IsEffectivelyVisible);
        Assert.Same(turn.ReportCommand, link.Command);
        link.Command!.Execute(null);
        await host.PumpAsync();

        Assert.Contains("Error: http_503", Named<TextBox>(host, "ReportPreview")!.Text);
    }

    [AvaloniaFact]
    public async Task A_link_in_an_answer_opens_web_links_only_and_the_panels_own_links_open_through_the_panel()
    {
        var started = new List<string>();
        var old = Fadenstack.UI.Avalonia.Behaviors.MarkdownText.StartProcess;
        Fadenstack.UI.Avalonia.Behaviors.MarkdownText.StartProcess = started.Add;
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "faden-av-links-" + Guid.NewGuid().ToString("N"));
        using var root = Fadenstack.Storage.FadenstackPaths.UseRootForTests(folder);
        try
        {
            using var host = await ChatHost.OpenAsync(2);
            var hook = Fadenstack.UI.Avalonia.Behaviors.MarkdownText.OpenLink;
            Assert.Same(Fadenstack.UI.Avalonia.Behaviors.MarkdownText.DefaultOpenLink, hook);
            hook(new Uri("mailto:a@b.example?body=secret"));
            hook(new Uri("file:///C:/Windows"));
            hook(new Uri(System.IO.Path.GetFullPath(Fadenstack.Storage.FadenstackPaths.LogRoot) + System.IO.Path.DirectorySeparatorChar));
            Assert.Empty(started);
            hook(new Uri("https://fadenstack.com/"));
            Assert.Equal(new[] { "https://fadenstack.com/" }, started);
            started.Clear();

            // the sheet's own commands go through the view's handler (LinkRequested)
            Fadenstack.UI.Avalonia.Behaviors.MarkdownText.OpenPanelLink(new Uri("mailto:it@example.org?subject=x"));
            Assert.Equal("mailto:it@example.org?subject=x", Assert.Single(started));
            started.Clear();
            host.Chat.OpenReportCommand.Execute(null);
            await host.PumpAsync();
            using (new Fadenstack.Logging.FadenstackFileLoggerProvider("av-links"))
            {
                host.Chat.Report.OpenLogFolderCommand.Execute(null);
            }
            Assert.Equal(System.IO.Path.GetFullPath(Fadenstack.Storage.FadenstackPaths.LogRoot).TrimEnd('\\', '/'), Assert.Single(started).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase);
            started.Clear();
            Fadenstack.UI.Avalonia.Behaviors.MarkdownText.OpenPanelLink(new Uri("file:///C:/Windows"));
            Assert.Empty(started);
        }
        finally
        {
            Fadenstack.UI.Avalonia.Behaviors.MarkdownText.StartProcess = old;
            try
            {
                System.IO.Directory.Delete(folder, true);
            }
            catch (System.IO.IOException)
            {
            }
        }
    }
}

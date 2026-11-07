using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// What the person let the agent do for longer than one call, in this skin (sdk/38 §7): the grant card above the composer,
/// the marker band under the header, and Settings → Approvals.
/// </summary>
public class GrantViewTests
{
    private static readonly GrantOffer Offer = new()
    {
        Places = new[] { GrantPlace.Document(@"C:\Docs\Plan.docx"), GrantPlace.Folder(@"C:\Docs\Notes", "Notes") },
        Classes = new[] { "read", "write" },
        Durations = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) },
        Preselected = TimeSpan.FromMinutes(30),
    };

    /// <summary>A grant card as the chat makes it, and the answer its buttons give.</summary>
    private static (GrantPart Part, Task<GrantAnswer> Answer) Card(UiStrings strings)
    {
        var broker = new ApprovalBroker();
        PendingGrant? pending = null;
        broker.GrantRequested += p => pending = p;
        var answer = broker.RequestGrantAsync(Offer, CancellationToken.None);
        var part = (GrantPart)typeof(GrantPart).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(PendingGrant), typeof(UiStrings) }, null)!.Invoke(new object[] { pending!, strings });
        return (part, answer);
    }

    /// <summary>When FADEN_AUDIT_OUT names a folder, the window as drawn goes there as a PNG.</summary>
    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FADEN_AUDIT_OUT") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        window.CaptureRenderedFrame()!.Save(Path.Combine(folder, name + ".png"), new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static IEnumerable<Control> InPopups(Control root) => root.GetVisualDescendants().OfType<Popup>()
        .Where(p => p.IsOpen && p.Child is not null)
        .SelectMany(p => new[] { p.Child! }.Concat(p.Child!.GetVisualDescendants().OfType<Control>()));

    [AvaloniaFact]
    public async Task The_grant_card_shows_the_places_and_the_time_and_allows_what_was_kept_for_the_time_picked()
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var (part, answer) = Card(strings);
        var template = new ChatView().DataTemplates.OfType<DataTemplate>().First(t => t.DataType == typeof(GrantPart));
        var card = new ContentControl { Content = part, ContentTemplate = template };
        ChatText.SetStrings(card, strings);
        var window = new Window { Width = 480, Height = 640, Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(strings.GrantTitle, texts);
        Assert.Contains("Plan.docx", texts);
        Assert.Contains("Notes", texts);
        Assert.Contains(part.MayText, texts);
        Assert.Contains(strings.GrantStillAsks, texts);
        Render(window, "grant-card-480");
        var time = card.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(new DurationOption(TimeSpan.FromMinutes(30), "30 min"), time.SelectedItem); // 30 min to start
        var buttons = card.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        Assert.Equal(new[] { strings.GrantNotNow, strings.GrantAllow },
            buttons.Where(b => b.Command == part.NotNowCommand || b.Command == part.AllowCommand).Select(b => b.Content as string));

        // Taking a place out strikes it through and offers it back; the time is picked in the box.
        buttons.First(b => b.Content as string == strings.GrantRemovePlace).Command!.Execute(null);
        time.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Plan.docx" && t.Classes.Contains("dropped"));
        Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => b.Content as string == strings.GrantKeepPlace && b.IsEffectivelyVisible);
        part.AllowCommand.Execute(null);
        var given = await answer;
        Assert.Equal((true, TimeSpan.FromMinutes(60)), (given.Allowed, given.Duration));
        Assert.Equal(new[] { "Notes" }, given.Places.Select(p => p.Label));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == strings.GrantAllowed && t.IsEffectivelyVisible);
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<Button>(), b => b.Command == part.AllowCommand && b.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_card_waits_above_the_composer_the_marker_band_waits_under_the_header_and_settings_list_the_approvals()
    {
        using var host = await ChatHost.OpenAsync(0, openSession: false);
        var strings = host.Chat.Strings;

        // No grant runs: the band under the header is there, hidden.
        var marker = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("grant-marker"));
        Assert.False(marker.IsEffectivelyVisible);
        var header = host.View.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("header"));
        Assert.Same(header.GetVisualParent(), marker.GetVisualParent());

        // A card waiting for an answer sits above the composer, and goes once answered.
        var (part, answer) = Card(strings);
        host.Chat.Grants.Cards.Add(part);
        await host.PumpAsync();
        var shown = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("grant"));
        Assert.True(shown.IsEffectivelyVisible);
        var composer = host.View.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ComposerBox");
        Assert.True(shown.TranslatePoint(default, host.View)!.Value.Y < composer.TranslatePoint(default, host.View)!.Value.Y);
        Render(host.Window, "grant-card-in-chat-480");
        part.NotNowCommand.Execute(null);
        Assert.False((await answer).Allowed);
        await host.PumpAsync();
        Assert.False(shown.IsEffectivelyVisible);

        // Settings → Approvals: nothing allowed for longer than one call here.
        host.Chat.IsSettingsOpen = true;
        await host.PumpAsync();
        var texts = InPopups(host.View).OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(strings.SettingsApprovals, texts);
        Assert.Contains(strings.SettingsApprovalsEmpty, texts);
        host.Chat.IsSettingsOpen = false;
    }
}

using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// Bypass in this skin (sdk/09 L-58, L-63; sdk/40 A3.4): the confirmation above the composer says what no longer asks, what
/// still asks, what the server records and for how long; Not now comes first; Enter or Esc on the focused switch never
/// switches Bypass on, only a click does; the Bypass band and the line that says why it ended sit under the header.
/// </summary>
public class BypassViewTests
{
    private static readonly GrantOffer Offer = new()
    {
        Bypass = true, Evidence = "decisions", ConversationId = "c-1",
        Durations = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) },
        Preselected = TimeSpan.FromMinutes(15),
    };

    /// <summary>A confirmation as the chat makes it, and the answer its buttons give.</summary>
    private static (BypassPart Part, Task<GrantAnswer> Answer) Card(UiStrings strings)
    {
        var broker = new ApprovalBroker();
        PendingGrant? pending = null;
        broker.GrantRequested += p =>
        {
            p.Claim();
            pending = p;
        };
        var answer = broker.RequestGrantAsync(Offer, CancellationToken.None);
        var part = (BypassPart)typeof(BypassPart).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(PendingGrant), typeof(UiStrings) }, null)!.Invoke(new object[] { pending!, strings });
        return (part, answer);
    }

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FADEN_AUDIT_OUT") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        window.CaptureRenderedFrame()!.Save(Path.Combine(folder, name + ".png"), new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task The_confirmation_says_it_all_and_only_a_click_on_the_switch_answers_it_never_enter_or_esc()
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var (part, answer) = Card(strings);
        var template = new ChatView().DataTemplates.OfType<DataTemplate>().First(t => t.DataType == typeof(BypassPart));
        var card = new ContentControl { Content = part, ContentTemplate = template };
        ChatText.SetStrings(card, strings);
        var window = new Window { Width = 480, Height = 760, Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        foreach (var said in new[] { strings.BypassTitle, strings.BypassNoLongerAsks, strings.BypassStillAsks, strings.BypassRecords("decisions"),
            strings.BypassNoExtension, strings.BypassReason, strings.BypassReasonSeen })
        {
            Assert.Contains(said, texts);
        }
        Assert.Equal(new DurationOption(TimeSpan.FromMinutes(15), "15 min"), card.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem);
        Render(window, "bypass-card-480");
        var buttons = card.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        var actions = buttons.Where(b => b.Command == part.NotNowCommand || b.Command == part.AllowCommand).ToList();
        Assert.Equal(new[] { strings.GrantNotNow, strings.BypassAllow }, actions.Select(b => b.Content as string)); // Not now first
        var allow = actions[1];
        Assert.False(allow.IsDefault);
        Assert.False(allow.IsCancel);
        Assert.DoesNotContain("primary", allow.Classes);

        allow.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(allow.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BypassCardState.Pending, part.State);
        Assert.False(answer.IsCompleted);

        var middle = allow.TranslatePoint(new Point(allow.Bounds.Width / 2, allow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var given = await answer;
        Assert.Equal((true, TimeSpan.FromMinutes(15)), (given.Allowed, given.Duration));
        Assert.Equal(BypassCardState.Confirming, part.State);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == strings.BypassConfirming && t.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_card_waits_above_the_composer_and_the_bypass_band_and_its_end_wait_under_the_header()
    {
        using var host = await ChatHost.OpenAsync(0, openSession: false);
        var strings = host.Chat.Strings;
        var header = host.View.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("header"));
        var bands = host.View.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("grant-marker")).ToList();

        // The work band, the Bypass band and the line that says why Bypass ended: under the header, hidden while nothing runs.
        Assert.Equal(3, bands.Count);
        Assert.All(bands, b => Assert.Same(header.GetVisualParent(), b.GetVisualParent()));
        Assert.All(bands, b => Assert.False(b.IsEffectivelyVisible));
        var bypass = bands.Single(b => b.Classes.Contains("bypass"));
        Assert.Contains(bypass.GetVisualDescendants().OfType<Button>(), b => b.Command == host.Chat.Grants.StopBypassCommand
            && AutomationProperties.GetName(b) == strings.BypassStopName);

        // A confirmation waiting for the person sits above the composer, and goes once declined.
        var (part, answer) = Card(strings);
        typeof(GrantsViewModel).GetProperty(nameof(GrantsViewModel.BypassCard))!.SetValue(host.Chat.Grants, part);
        await host.PumpAsync();
        var shown = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("bypass") && b.Classes.Contains("card"));
        Assert.True(shown.IsEffectivelyVisible);
        var composer = host.View.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ComposerBox");
        Assert.True(shown.TranslatePoint(default, host.View)!.Value.Y < composer.TranslatePoint(default, host.View)!.Value.Y);
        Render(host.Window, "bypass-card-in-chat-480");
        part.NotNowCommand.Execute(null);
        Assert.False((await answer).Allowed);
        await host.PumpAsync();
        Assert.False(shown.IsEffectivelyVisible);
    }
}

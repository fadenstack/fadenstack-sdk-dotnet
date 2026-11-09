using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fadenstack.Agents;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The WPF skin's Bypass confirmation (sdk/09 L-58), laid out offscreen: it loads, says what no longer asks, what still asks
/// and what the server records, puts Not now first, and its switch is never the default or cancel button and takes no Enter.
/// </summary>
public class BypassCardTests
{
    private static (BypassPart Part, Task<GrantAnswer> Answer) Part()
    {
        var broker = new ApprovalBroker();
        PendingGrant? pending = null;
        broker.GrantRequested += p =>
        {
            p.Claim();
            pending = p;
        };
        var answer = broker.RequestGrantAsync(new GrantOffer
        {
            Bypass = true, Evidence = "calls", ConversationId = "c-1", Preselected = TimeSpan.FromMinutes(15),
            Durations = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30) },
        }, CancellationToken.None);
        var part = (BypassPart)typeof(BypassPart).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(PendingGrant), typeof(UiStrings) }, null)!.Invoke(new object[] { pending!, UiStrings.For(CultureInfo.GetCultureInfo("en")) });
        return (part, answer);
    }

    private static ResourceDictionary Load()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var all = new ResourceDictionary();
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Colors.Light.xaml") });
        all.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fadenstack.UI.Wpf;component/Themes/Chat.xaml") });
        return all;
    }

    private static IEnumerable<DependencyObject> Visual(DependencyObject root) =>
        new[] { root }.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)).Select(i => VisualTreeHelper.GetChild(root, i)).SelectMany(Visual));

    /// <summary>Shown as laid out offscreen: it and every parent Visible (IsVisible needs a window).</summary>
    private static bool Shown(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void The_confirmation_loads_says_it_all_and_its_switch_is_never_the_default_button() => Sta.Run(() =>
    {
        var (part, answer) = Part();
        var resources = Load();
        var card = (FrameworkElement)((DataTemplate)resources[new DataTemplateKey(typeof(BypassPart))]).LoadContent();
        card.Resources.MergedDictionaries.Add(resources);
        card.DataContext = part;
        card.Measure(new Size(480, 1000));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();

        var texts = Visual(card).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ToList();
        foreach (var said in new[] { part.Title, part.NoLongerAsksText, part.StillAsksText, part.RecordsText, part.NoExtensionText, part.ReasonSeenText })
        {
            Assert.Contains(said, texts);
        }
        var buttons = Visual(card).OfType<Button>().Where(b => Shown(b) && (b.Command == part.NotNowCommand || b.Command == part.AllowCommand)).ToList();
        Assert.Equal(new object[] { "Not now", "Switch Bypass on" }, buttons.Select(b => b.Content).ToArray());
        var allow = buttons[1];
        Assert.False(allow.IsDefault);
        Assert.False(allow.IsCancel);
        Assert.False(KeyboardNavigation.GetAcceptsReturn(allow));
        Assert.False(answer.IsCompleted);

        if (Environment.GetEnvironmentVariable("FADEN_AUDIT_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)card.ActualWidth * 2, (int)card.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, "wpf-bypass-card.png"));
            encoder.Save(file);
        }
    });
}

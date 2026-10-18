using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Behaviors;
using Microsoft.Win32;

namespace Fadenstack.UI.Wpf.Views;

public enum ChatTheme
{
    /// <summary>Follows Windows' app mode (light or dark).</summary>
    System,
    Light,
    Dark,

    /// <summary>Dark text surfaces on mid-grey: for hosts whose chrome is grey, not black (Office's Dark Gray).</summary>
    DarkGray,
}

/// <summary>
/// The chat panel for an <see cref="ViewModels.AgentChatViewModel"/> (set it as the DataContext). Everything it
/// shows comes from the view model, its texts too (<see cref="AgentChatViewModel.Strings"/>); the code here only
/// picks the colours, gives the panel the texts' language and direction, writes the line under the composer, moves
/// the keyboard in and out of About, and opens the panel's links through <see cref="MarkdownText.OpenLink"/>.
/// </summary>
public partial class ChatView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(ChatView), new PropertyMetadata(ChatTheme.System, (d, _) => ((ChatView)d).ApplyTheme()));

    /// <summary>Extra content at the composer's bottom left, for the host's own buttons (e.g. "Add context").</summary>
    public static readonly DependencyProperty ComposerToolbarProperty = DependencyProperty.Register(
        nameof(ComposerToolbar), typeof(object), typeof(ChatView), new PropertyMetadata(null, (d, _) => ((ChatView)d).ShareTheme()));

    /// <summary>The host's own items in the panel's settings, below the language (sign out, its own options).</summary>
    public static readonly DependencyProperty SettingsContentProperty = DependencyProperty.Register(
        nameof(SettingsContent), typeof(object), typeof(ChatView), new PropertyMetadata(null, (d, _) => ((ChatView)d).ApplySettingsContent()));

    private AgentChatViewModel? _chat;

    private ResourceDictionary? _colors;
    private readonly List<FrameworkElement> _sharedWith = new();
    private ResourceDictionary? _shared;
    private bool _aboutHadKeyboard;
    private bool _aboutClosedByPress;

    public ChatView()
    {
        InitializeComponent();
        ApplyTheme();
        DataContextChanged += (_, _) => ApplyStrings();
        PreviewMouseDown += CloseAboutOnPressOutside;
        AboutCard.IsKeyboardFocusWithinChanged += (_, e) => _aboutHadKeyboard |= (bool)e.NewValue;
        PlaceAtEnd(SettingsMenu);
        PlaceAtEnd(ModeMenu);
        PlaceAtEnd(HistoryMenu);
        UpdateFooter();
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public object? ComposerToolbar
    {
        get => GetValue(ComposerToolbarProperty);
        set => SetValue(ComposerToolbarProperty, value);
    }

    public object? SettingsContent
    {
        get => GetValue(SettingsContentProperty);
        set => SetValue(SettingsContentProperty, value);
    }

    /// <summary>The view model's texts for everything in the panel (<see cref="ChatText"/>), with their language and direction.</summary>
    private void ApplyStrings()
    {
        if (!ReferenceEquals(_chat, DataContext))
        {
            if (_chat is not null)
            {
                _chat.PropertyChanged -= OnChatChanged;
                _chat.LinkRequested -= OpenLink;
            }
            _chat = DataContext as AgentChatViewModel;
            if (_chat is not null)
            {
                _chat.PropertyChanged += OnChatChanged;
                _chat.LinkRequested += OpenLink;
            }
        }
        UpdateSettingsMenu();
        var strings = _chat?.Strings;
        ChatText.SetStrings(this, strings);
        UpdateFooter();
        if (strings is null)
        {
            ClearValue(LanguageProperty);
            ClearValue(FlowDirectionProperty);
            return;
        }
        Language = XmlLanguage.GetLanguage(strings.Language);
        FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    /// <summary>
    /// The user picked another language: the panel's texts, language and direction follow. The footer's link changed, or
    /// About opened or closed.
    /// </summary>
    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentChatViewModel.Strings) or null or "":
                ApplyStrings();
                break;
            case nameof(AgentChatViewModel.BrandingLink):
                UpdateFooter();
                break;
            case nameof(AgentChatViewModel.IsAboutOpen):
                MoveKeyboardForAbout(_chat?.IsAboutOpen == true);
                break;
        }
    }

    private void ApplySettingsContent()
    {
        SettingsHost.Content = SettingsContent;
        ShareTheme();
        UpdateSettingsMenu();
    }

    /// <summary>The settings always show (About is their last item); a line separates the language, the host's items and About.</summary>
    private void UpdateSettingsMenu()
    {
        var languages = _chat?.HasLanguageSettings == true;
        var own = SettingsContent is not null;
        SettingsSeparator.Visibility = languages && own ? Visibility.Visible : Visibility.Collapsed;
        AboutSeparator.Visibility = languages || own ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Under the composer: the AI notice (a host's empty text hides it), and below it, on a small line of its own, the
    /// muted "Fadenstack" link, unless the host turned <see cref="AgentChatOptions.Branding"/> off. Screen readers read
    /// the notice as before, and the link as a link that opens in the browser.
    /// </summary>
    private void UpdateFooter()
    {
        var notice = (_chat?.Strings ?? ChatText.Fallback).AiNotice;
        var link = _chat?.BrandingLink;
        AiNotice.Text = notice;
        AiNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notice.Length > 0)
        {
            AutomationProperties.SetName(AiNotice, notice);
        }
        else
        {
            AiNotice.ClearValue(AutomationProperties.NameProperty);
        }
        BrandingLine.Inlines.Clear();
        if (link is not null && _chat is { } chat)
        {
            var hyperlink = new Hyperlink(new Run(link.Label))
            {
                Command = chat.OpenLinkCommand,
                CommandParameter = link,
                ToolTip = link.Url.AbsoluteUri,
            };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Fadenstack.ForegroundMuted");
            hyperlink.SetResourceReference(FrameworkContentElement.FocusVisualStyleProperty, "Fadenstack.LinkFocusVisual");
            AutomationProperties.SetName(hyperlink, link.Name);
            BrandingLine.Inlines.Add(hyperlink);
        }
        BrandingLine.Visibility = BrandingLine.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Just enough space to tell the link's line from a notice that wraps.
        BrandingLine.Margin = new Thickness(0, notice.Length > 0 ? 2 : 0, 0, 0);
        Footer.Visibility = AiNotice.Visibility == Visibility.Visible || BrandingLine.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// A menu under its button that opens over the panel: its end edge (the right one, or the left one right to left)
    /// under the button's, not its start edge as <see cref="PlacementMode.Bottom"/> puts it, which left the settings and
    /// the tool modes hanging past the panel's edge. Kept 8 in from the panel's edges when it fits (a narrow pane; the
    /// history, as wide as the panel, comes out 8 in from both), and above the button when there is no room below. The
    /// menu's own margin (the room for its shadow) is left out, so the menu itself lines up with the button.
    /// </summary>
    private void PlaceAtEnd(Popup popup) => popup.CustomPopupPlacementCallback = (_, targetSize, _) =>
    {
        if (popup.PlacementTarget is not FrameworkElement { ActualWidth: > 0 } target || popup.Child is not FrameworkElement menu
            || PresentationSource.FromVisual(target) is null || PresentationSource.FromVisual(this) is null)
        {
            return new[] { new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal) };
        }
        // On screen, in pixels, where right to left does not mirror anything.
        var rtl = target.FlowDirection == FlowDirection.RightToLeft;
        var (targetLeft, targetRight) = ScreenSpan(target);
        var (panelLeft, panelRight) = ScreenSpan(this);
        var pixels = (targetRight - targetLeft) / target.ActualWidth;
        var units = targetSize.Width / (targetRight - targetLeft);
        var width = menu.ActualWidth * pixels;
        var inset = 8 * pixels;
        var left = rtl ? targetLeft : targetRight - width;
        left = Math.Max(panelLeft + inset, Math.Min(left, panelRight - inset - width));
        // The popup's window starts at the menu's margin on the left; right to left, the offset counts from the target's right.
        var windowLeft = left - (rtl ? menu.Margin.Right : menu.Margin.Left) * pixels;
        var x = (windowLeft - (rtl ? targetRight : targetLeft)) * units;
        var above = -(menu.ActualHeight + menu.Margin.Top + menu.Margin.Bottom) * pixels * units;
        return new[]
        {
            new CustomPopupPlacement(new Point(x, targetSize.Height), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(x, above), PopupPrimaryAxis.Horizontal),
        };
    };

    /// <summary>An element's left and right edges on screen.</summary>
    private static (double Left, double Right) ScreenSpan(FrameworkElement element)
    {
        var a = element.PointToScreen(new Point(0, 0)).X;
        var b = element.PointToScreen(new Point(element.ActualWidth, 0)).X;
        return (Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>A link of the panel's (the footer's, About's) opens through the link hook a host can replace.</summary>
    private static void OpenLink(object? sender, Uri url)
    {
        try
        {
            MarkdownText.OpenLink(url);
        }
        catch (Win32Exception)
        {
            // No app opens web links on this computer: nothing to do.
        }
    }

    /// <summary>A press anywhere outside About closes it, and does what it does (a press on the composer types there).</summary>
    private void CloseAboutOnPressOutside(object sender, MouseButtonEventArgs e)
    {
        if (_chat is { IsAboutOpen: true } chat && !AboutCard.IsMouseOver)
        {
            _aboutClosedByPress = true;
            chat.IsAboutOpen = false;
        }
    }

    /// <summary>
    /// About takes the keyboard when it opens (Escape closes it; Tab stays inside), and gives it back to the settings
    /// button when it closes from the keyboard. Closed by a press elsewhere, the keyboard goes where the press went.
    /// </summary>
    private void MoveKeyboardForAbout(bool open)
    {
        if (open)
        {
            _aboutHadKeyboard = false;
            _aboutClosedByPress = false;
            Dispatcher.BeginInvoke(new Action(() => AboutClose.Focus()), DispatcherPriority.Input);
            return;
        }
        var giveBack = _aboutHadKeyboard && !_aboutClosedByPress;
        _aboutHadKeyboard = false;
        _aboutClosedByPress = false;
        if (giveBack)
        {
            Dispatcher.BeginInvoke(new Action(() => SettingsButton.Focus()), DispatcherPriority.Input);
        }
    }

    private void ApplyTheme()
    {
        var colors = new ResourceDictionary { Source = PaletteUri(Theme) };
        var merged = Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Insert(0, colors);
        }
        _colors = colors;
        ShareTheme();
    }

    /// <summary>
    /// A host's toolbar content is defined in the host's own tree, so its DynamicResource lookups never reach this
    /// view's colours: Office's "Context" button was drawn black on the dark composer. It gets the same colours.
    /// </summary>
    private void ShareTheme()
    {
        if (_shared is not null)
        {
            foreach (var element in _sharedWith)
            {
                element.Resources.MergedDictionaries.Remove(_shared);
            }
        }
        _sharedWith.Clear();
        _sharedWith.AddRange(new[] { ComposerToolbar, SettingsContent }.OfType<FrameworkElement>());
        _shared = _colors;
        if (_shared is not null)
        {
            foreach (var element in _sharedWith)
            {
                element.Resources.MergedDictionaries.Insert(0, _shared);
            }
        }
    }

    /// <summary>The colours of a theme, for hosts that draw around the panel (System: Windows' app mode).</summary>
    public static Uri PaletteUri(ChatTheme theme)
    {
        var name = theme switch
        {
            ChatTheme.Light => "Light",
            ChatTheme.Dark => "Dark",
            ChatTheme.DarkGray => "DarkGray",
            _ => SystemUsesDarkMode() ? "Dark" : "Light",
        };
        return new Uri($"/Fadenstack.UI.Wpf;component/Themes/Colors.{name}.xaml", UriKind.Relative);
    }

    /// <summary>Whether Windows is in dark app mode.</summary>
    public static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Fadenstack.UI.ViewModels;
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
/// picks the colours and gives the panel the texts' language and direction.
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

    public ChatView()
    {
        InitializeComponent();
        ApplyTheme();
        DataContextChanged += (_, _) => ApplyStrings();
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
            }
            _chat = DataContext as AgentChatViewModel;
            if (_chat is not null)
            {
                _chat.PropertyChanged += OnChatChanged;
            }
        }
        UpdateSettingsButton();
        var strings = _chat?.Strings;
        ChatText.SetStrings(this, strings);
        if (strings is null)
        {
            ClearValue(LanguageProperty);
            ClearValue(FlowDirectionProperty);
            return;
        }
        Language = XmlLanguage.GetLanguage(strings.Language);
        FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    /// <summary>The user picked another language: the panel's texts, language and direction follow.</summary>
    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentChatViewModel.Strings) or null or "")
        {
            ApplyStrings();
        }
    }

    private void ApplySettingsContent()
    {
        SettingsHost.Content = SettingsContent;
        ShareTheme();
        UpdateSettingsButton();
    }

    /// <summary>The settings button shows when there is something to set: a language, or the host's items.</summary>
    private void UpdateSettingsButton()
    {
        var languages = _chat?.HasLanguageSettings == true;
        SettingsButton.Visibility = languages || SettingsContent is not null ? Visibility.Visible : Visibility.Collapsed;
        SettingsSeparator.Visibility = languages && SettingsContent is not null ? Visibility.Visible : Visibility.Collapsed;
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

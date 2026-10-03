using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Faden.UI.Wpf.Views;

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
/// shows comes from the view model; the code here only picks the colours.
/// </summary>
public partial class ChatView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(ChatView), new PropertyMetadata(ChatTheme.System, (d, _) => ((ChatView)d).ApplyTheme()));

    /// <summary>Extra content at the composer's bottom left, for the host's own buttons (e.g. "Add context").</summary>
    public static readonly DependencyProperty ComposerToolbarProperty = DependencyProperty.Register(
        nameof(ComposerToolbar), typeof(object), typeof(ChatView), new PropertyMetadata(null, (d, _) => ((ChatView)d).ShareTheme()));

    private ResourceDictionary? _colors;
    private FrameworkElement? _sharedWith;
    private ResourceDictionary? _shared;

    public ChatView()
    {
        InitializeComponent();
        ApplyTheme();
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
        if (_sharedWith is not null && _shared is not null)
        {
            _sharedWith.Resources.MergedDictionaries.Remove(_shared);
        }
        _sharedWith = ComposerToolbar as FrameworkElement;
        _shared = _colors;
        if (_sharedWith is not null && _shared is not null)
        {
            _sharedWith.Resources.MergedDictionaries.Insert(0, _shared);
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
        return new Uri($"/Faden.UI.Wpf;component/Themes/Colors.{name}.xaml", UriKind.Relative);
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

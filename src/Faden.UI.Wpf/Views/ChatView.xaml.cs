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
        nameof(ComposerToolbar), typeof(object), typeof(ChatView), new PropertyMetadata(null));

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
        var dark = Theme == ChatTheme.Dark || (Theme == ChatTheme.System && SystemUsesDarkMode());
        var colors = new ResourceDictionary
        {
            Source = new Uri($"/Faden.UI.Wpf;component/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        var merged = Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Insert(0, colors);
        }
    }

    private static bool SystemUsesDarkMode()
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

using System.Windows.Controls;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The setting for content logging for a <see cref="ContentLoggingSettingsViewModel"/> (set it as the DataContext): the time to choose, Start and
/// Stop, and the end time while it is on. It shows only where the app offers content logging and the policy allows it. The code here is only what
/// a view does: it picks the colours and gives the panel the texts' language and direction (the view model's
/// <see cref="ContentLoggingSettingsViewModel.Strings"/>); the buttons and their state are the view model's.
/// </summary>
public partial class ContentLoggingSettingsView : UserControl
{
    public static readonly System.Windows.DependencyProperty ThemeProperty = System.Windows.DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(ContentLoggingSettingsView),
        new System.Windows.PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((ContentLoggingSettingsView)d, ((ContentLoggingSettingsView)d).Theme)));

    public ContentLoggingSettingsView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        var texts = new SkillTextsFollower(strings => SkillViewSupport.ApplyStrings(this, strings));
        DataContextChanged += (_, e) => texts.Follow(e.NewValue);
        SkillViewSupport.FocusFirstWhenLoaded(this);
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }
}

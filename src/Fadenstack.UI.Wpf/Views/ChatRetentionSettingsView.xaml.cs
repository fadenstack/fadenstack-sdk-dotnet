using System.Windows.Controls;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The setting for old chats for a <see cref="ChatRetentionSettingsViewModel"/> (set it as the DataContext): the switch, the two limits,
/// Save and Cancel, and where the organisation or the machine policy decides, the values read only with the sentence that says so. The
/// code here is only what a view does: it picks the colours and gives the panel the texts' language and direction (the view model's
/// <see cref="ChatRetentionSettingsViewModel.Strings"/>); the checks and the buttons are the view model's.
/// </summary>
public partial class ChatRetentionSettingsView : UserControl
{
    public static readonly System.Windows.DependencyProperty ThemeProperty = System.Windows.DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(ChatRetentionSettingsView),
        new System.Windows.PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((ChatRetentionSettingsView)d, ((ChatRetentionSettingsView)d).Theme)));

    public ChatRetentionSettingsView()
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

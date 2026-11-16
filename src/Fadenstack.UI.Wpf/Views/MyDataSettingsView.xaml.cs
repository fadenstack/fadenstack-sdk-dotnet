using System.Windows.Controls;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// "Your data on this device" for a <see cref="MyDataSettingsViewModel"/> (set it as the DataContext): the buttons that export the person's
/// data and remove this app's data or all of it, the confirmation that says what goes and what stays, and the result. The code here is only
/// what a view does: it picks the colours and gives the panel the texts' language and direction (the view model's
/// <see cref="MyDataSettingsViewModel.Strings"/>); the states and the buttons are the view model's.
/// </summary>
public partial class MyDataSettingsView : UserControl
{
    public static readonly System.Windows.DependencyProperty ThemeProperty = System.Windows.DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(MyDataSettingsView),
        new System.Windows.PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((MyDataSettingsView)d, ((MyDataSettingsView)d).Theme)));

    public MyDataSettingsView()
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

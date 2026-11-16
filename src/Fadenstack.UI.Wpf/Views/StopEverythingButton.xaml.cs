using System.Windows.Controls;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// "Stop everything" for a <see cref="StopEverythingViewModel"/> (set it as the DataContext): an icon button named "Stop everything" with a
/// tooltip, and the line that says what the stop did. The code here is only what a view does: it picks the colours and gives the control the
/// texts' language and direction; the stop and its result are the view model's.
/// </summary>
public partial class StopEverythingButton : UserControl
{
    public static readonly System.Windows.DependencyProperty ThemeProperty = System.Windows.DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(StopEverythingButton),
        new System.Windows.PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((StopEverythingButton)d, ((StopEverythingButton)d).Theme)));

    public StopEverythingButton()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        var texts = new SkillTextsFollower(strings => SkillViewSupport.ApplyStrings(this, strings));
        DataContextChanged += (_, e) => texts.Follow(e.NewValue);
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }
}

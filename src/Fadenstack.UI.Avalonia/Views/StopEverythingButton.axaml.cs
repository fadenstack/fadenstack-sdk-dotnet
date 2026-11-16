using Avalonia;
using Avalonia.Controls;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// "Stop everything" for a <see cref="StopEverythingViewModel"/> (set it as the DataContext): an icon button named "Stop everything" with a
/// tooltip, and the line that says what the stop did. The code here is only what a view does: it gives the control the texts' language and
/// direction (the view model's <see cref="StopEverythingViewModel.Strings"/>); the stop and its result are the view model's.
/// </summary>
public partial class StopEverythingButton : UserControl
{
    private readonly SkillTextsFollower _texts;

    public StopEverythingButton()
    {
        FadenstackControls.EnsureLoaded();
        InitializeComponent();
        _texts = new SkillTextsFollower(strings =>
        {
            ChatText.SetStrings(this, strings);
            FlowDirection = strings is { IsRightToLeft: true } ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            _texts.Follow(DataContext);
        }
    }
}

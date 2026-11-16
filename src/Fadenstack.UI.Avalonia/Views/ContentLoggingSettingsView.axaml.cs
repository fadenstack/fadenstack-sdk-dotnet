using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The setting for content logging for a <see cref="ContentLoggingSettingsViewModel"/> (set it as the DataContext): the time to choose, Start and Stop,
/// and the end time while it is on. It shows only where the app offers content logging and the policy allows it. The
/// code here is only what a view does: it gives the panel the texts' language and direction (the view model's
/// <see cref="ContentLoggingSettingsViewModel.Strings"/>); the buttons and their state are the view model's.
/// </summary>
public partial class ContentLoggingSettingsView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public ContentLoggingSettingsView()
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => WizardFocus.FocusFirst(this), DispatcherPriority.Loaded);
    }
}

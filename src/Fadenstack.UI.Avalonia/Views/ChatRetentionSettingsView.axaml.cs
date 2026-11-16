using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The setting for old chats for a <see cref="ChatRetentionSettingsViewModel"/> (set it as the DataContext): the switch, the two limits,
/// Save and Cancel, and where the organisation or the machine policy decides, the values read only with the sentence that says so. The
/// code here is only what a view does: it gives the panel the texts' language and direction (the view model's
/// <see cref="ChatRetentionSettingsViewModel.Strings"/>); the checks and the buttons are the view model's.
/// </summary>
public partial class ChatRetentionSettingsView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public ChatRetentionSettingsView()
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

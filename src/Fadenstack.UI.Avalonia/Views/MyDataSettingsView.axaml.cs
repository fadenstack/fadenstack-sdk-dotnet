using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// "Your data on this device" for a <see cref="MyDataSettingsViewModel"/> (set it as the DataContext): the buttons that export the person's
/// data and remove this app's data or all of it, the confirmation that says what goes and what stays, and the result. The code here is only
/// what a view does: it gives the panel the texts' language and direction (the view model's <see cref="MyDataSettingsViewModel.Strings"/>);
/// the states and the buttons are the view model's.
/// </summary>
public partial class MyDataSettingsView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public MyDataSettingsView()
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
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            RefreshWhenShown();
        }
    }

    // The record of tool calls grows as the agents work: read it each time the control is shown.
    private void RefreshWhenShown()
    {
        if (DataContext is MyDataSettingsViewModel model && IsEffectivelyVisible)
        {
            _ = model.RefreshAsync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshWhenShown();
        Dispatcher.UIThread.Post(() => WizardFocus.FocusFirst(this), DispatcherPriority.Loaded);
    }
}

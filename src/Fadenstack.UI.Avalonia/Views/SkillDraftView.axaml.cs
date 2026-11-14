using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// "Draft from this chat" for a <see cref="SkillDraftViewModel"/> (set it as the DataContext): what the draft should be for, the button
/// that asks the model, the state, and the statement that nothing is saved until Save. The code here is only what a view does: it gives
/// the panel the texts' language and direction (the view model's <see cref="SkillDraftViewModel.Strings"/>) and sends Enter to the primary button;
/// "Open the proposal" is the view model's command (<see cref="SkillDraftViewModel.OpenProposalCommand"/>).
/// </summary>
public partial class SkillDraftView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public SkillDraftView()
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

    /// <summary>Enter presses the primary button (Esc has nothing to stop here).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && DataContext is SkillDraftViewModel && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

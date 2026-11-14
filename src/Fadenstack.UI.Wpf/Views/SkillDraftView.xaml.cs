using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// "Draft from this chat" for a <see cref="SkillDraftViewModel"/> (set it as the DataContext): what the draft should be for, the button
/// that asks the model, the state, and the statement that nothing is saved until Save. The code here is only what a view does: it picks
/// the colours, gives the panel the texts' language and direction (the view model's <see cref="SkillDraftViewModel.Strings"/>) and sends Enter to the primary button;
/// "Open the proposal" is the view model's command (<see cref="SkillDraftViewModel.OpenProposalCommand"/>).
/// </summary>
public partial class SkillDraftView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(SkillDraftView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((SkillDraftView)d, ((SkillDraftView)d).Theme)));

    public SkillDraftView()
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

    /// <summary>Enter presses the primary button (Esc has nothing to stop here).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && DataContext is SkillDraftViewModel && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

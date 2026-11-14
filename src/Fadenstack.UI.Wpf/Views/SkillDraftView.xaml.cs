using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// "Draft from this chat" for a <see cref="SkillDraftViewModel"/> (set it as the DataContext): what the draft should be for, the button
/// that asks the model, the state, and the statement that nothing is saved until Save. The code here is only what a view does: it picks
/// the colours, gives the panel the texts' language and direction (<see cref="Strings"/>), sends Enter to the primary button, and raises
/// <see cref="EditorRequested"/> when the person asks to see the proposal in the editor (the host shows it; the library does so itself).
/// </summary>
public partial class SkillDraftView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(SkillDraftView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((SkillDraftView)d, ((SkillDraftView)d).Theme)));

    /// <summary>The texts of the panel; the current UI language's unless the host sets them (the same ones it gave the view model).</summary>
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings), typeof(UiStrings), typeof(SkillDraftView), new PropertyMetadata(null, (d, e) => SkillViewSupport.ApplyStrings((SkillDraftView)d, (UiStrings?)e.NewValue)));

    public SkillDraftView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        Strings = UiStrings.Current;
        SkillViewSupport.FocusFirstWhenLoaded(this);
        OpenButton.Click += OnOpenClick;
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public UiStrings? Strings
    {
        get => (UiStrings?)GetValue(StringsProperty);
        set => SetValue(StringsProperty, value);
    }

    /// <summary>The person asked to open the proposal in the editor; the argument is the proposal.</summary>
    public event EventHandler<SkillEditorViewModel>? EditorRequested;

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if ((DataContext as SkillDraftViewModel)?.Proposal is { } proposal)
        {
            EditorRequested?.Invoke(this, proposal);
        }
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

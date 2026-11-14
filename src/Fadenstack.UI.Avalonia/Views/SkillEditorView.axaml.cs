using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The skill editor for a <see cref="SkillEditorViewModel"/> (set it as the DataContext): the fields, the preview, the files, the checks,
/// Save and Cancel, and the Try panel. The code here is only what a view does: it gives the panel the texts' language and direction
/// (the view model's <see cref="SkillEditorViewModel.Strings"/>) and sends Enter to the primary button (Save); Esc is two key
/// bindings of the XAML, and the focus goes to the name when the editor opens (the XAML).
/// </summary>
public partial class SkillEditorView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public SkillEditorView()
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

    /// <summary>Enter, where no field takes it for itself, presses Save (Esc is two key bindings of the view's XAML).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && DataContext is SkillEditorViewModel && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

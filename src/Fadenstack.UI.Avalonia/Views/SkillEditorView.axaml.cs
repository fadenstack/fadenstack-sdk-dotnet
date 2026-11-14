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
/// (<see cref="Strings"/>, the same texts the view model was given) and sends Enter to the primary button (Save); Esc is two key
/// bindings of the XAML, and the focus goes to the name when the editor opens (the XAML).
/// </summary>
public partial class SkillEditorView : UserControl
{
    /// <summary>The texts of the panel; the current UI language's unless the host sets them (the same ones it gave the view model).</summary>
    public static readonly StyledProperty<UiStrings?> StringsProperty = AvaloniaProperty.Register<SkillEditorView, UiStrings?>(nameof(Strings));

    public SkillEditorView()
    {
        FadenstackControls.EnsureLoaded();
        InitializeComponent();
        Strings = UiStrings.Current;
    }

    public UiStrings? Strings
    {
        get => GetValue(StringsProperty);
        set => SetValue(StringsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StringsProperty)
        {
            ChatText.SetStrings(this, Strings);
            FlowDirection = Strings is { IsRightToLeft: true } ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
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

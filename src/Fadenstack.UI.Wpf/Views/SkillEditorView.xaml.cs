using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The skill editor for a <see cref="SkillEditorViewModel"/> (set it as the DataContext): the fields, the preview, the files, the checks,
/// Save and Cancel, and the Try panel. The code here is only what a view does: it picks the colours, gives the panel the texts' language
/// and direction (<see cref="Strings"/>, the same texts the view model was given), sends Esc to the replace question or to Cancel, and Enter
/// to the primary button (Save) or, in the Try box, to the test; the focus goes to the name when the editor opens (the XAML).
/// </summary>
public partial class SkillEditorView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(SkillEditorView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((SkillEditorView)d, ((SkillEditorView)d).Theme)));

    /// <summary>The texts of the panel; the current UI language's unless the host sets them (the same ones it gave the view model).</summary>
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings), typeof(UiStrings), typeof(SkillEditorView), new PropertyMetadata(null, (d, e) => SkillViewSupport.ApplyStrings((SkillEditorView)d, (UiStrings?)e.NewValue)));

    public SkillEditorView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        Strings = UiStrings.Current;
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

    /// <summary>The key came from the Try box (its text host, not the box itself, is the event's source).</summary>
    private bool IsInTestBox(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, TestBox))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Esc answers the replace question first, else it is Cancel; Enter runs the test in the Try box and presses Save elsewhere (not in a
    /// field that takes Enter for itself). (WPF runs only the first of two input bindings for one key, so these are not key bindings of the XAML.)
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not SkillEditorViewModel editor || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            var command = editor.DismissReplaceCommand.CanExecute(null) ? editor.DismissReplaceCommand : editor.CancelCommand;
            command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && IsInTestBox(e.OriginalSource))
        {
            editor.RunTestCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

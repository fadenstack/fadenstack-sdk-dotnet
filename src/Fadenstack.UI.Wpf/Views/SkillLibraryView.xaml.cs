using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Skills;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The skill library for a <see cref="SkillLibraryViewModel"/> (set it as the DataContext): the list with its buttons, the delete and
/// import questions, and, while the view model has an editor open, that editor in place of the list. The code here is only what a view
/// does: it picks the colours, gives the panel the texts' language and direction (<see cref="Strings"/>, the same texts the view model
/// was given), puts the focus on the first button when it appears, sends Esc to the question that is open (the delete or the import) and
/// Enter to the import question's primary button.
/// </summary>
public partial class SkillLibraryView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(SkillLibraryView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((SkillLibraryView)d, ((SkillLibraryView)d).Theme)));

    /// <summary>The texts of the panel; the current UI language's unless the host sets them (the same ones it gave the view model).</summary>
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings), typeof(UiStrings), typeof(SkillLibraryView), new PropertyMetadata(null, (d, e) => SkillViewSupport.ApplyStrings((SkillLibraryView)d, (UiStrings?)e.NewValue)));

    public SkillLibraryView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        Strings = UiStrings.Current;
        SkillViewSupport.FocusFirstWhenLoaded(this);
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

    /// <summary>
    /// Esc says no to the question that is open; Enter presses the import question's primary button. (WPF runs only the first of two input
    /// bindings for one key, so Esc cannot be two key bindings of the XAML as it is in the Avalonia view.)
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not SkillLibraryViewModel library || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (library.CancelDeleteCommand.CanExecute(null))
            {
                library.CancelDeleteCommand.Execute(null);
                e.Handled = true;
            }
            else if (library.CancelImportCommand.CanExecute(null))
            {
                library.CancelImportCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

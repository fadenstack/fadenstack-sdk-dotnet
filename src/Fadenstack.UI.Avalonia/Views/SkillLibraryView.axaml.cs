using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The skill library for a <see cref="SkillLibraryViewModel"/> (set it as the DataContext): the list with its buttons, the delete and
/// import questions, and, while the view model has an editor open, that editor in place of the list. The code here is only what a view
/// does: it gives the panel the texts' language and direction (the view model's <see cref="SkillLibraryViewModel.Strings"/>), puts the
/// focus on the first button when it appears, and sends Enter to the import question's primary button (Esc is two key bindings of the XAML).
/// </summary>
public partial class SkillLibraryView : UserControl
{
    private readonly SkillTextsFollower _texts;

    public SkillLibraryView()
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

    /// <summary>Enter presses the import question's primary button (Esc is a key binding of the view's XAML).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && DataContext is SkillLibraryViewModel && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

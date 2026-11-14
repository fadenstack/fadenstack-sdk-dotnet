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
/// the panel the texts' language and direction (<see cref="Strings"/>), sends Enter to the primary button, and raises
/// <see cref="EditorRequested"/> when the person asks to see the proposal in the editor (the host shows it; the library does so itself).
/// </summary>
public partial class SkillDraftView : UserControl
{
    /// <summary>The texts of the panel; the current UI language's unless the host sets them (the same ones it gave the view model).</summary>
    public static readonly StyledProperty<UiStrings?> StringsProperty = AvaloniaProperty.Register<SkillDraftView, UiStrings?>(nameof(Strings));

    public SkillDraftView()
    {
        FadenstackControls.EnsureLoaded();
        InitializeComponent();
        Strings = UiStrings.Current;
        OpenButton.Click += OnOpenClick;
    }

    public UiStrings? Strings
    {
        get => GetValue(StringsProperty);
        set => SetValue(StringsProperty, value);
    }

    /// <summary>The person asked to open the proposal in the editor; the argument is the proposal.</summary>
    public event EventHandler<SkillEditorViewModel>? EditorRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StringsProperty)
        {
            ChatText.SetStrings(this, Strings);
            FlowDirection = Strings is { IsRightToLeft: true } ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => WizardFocus.FocusFirst(this), DispatcherPriority.Loaded);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
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
        if (!e.Handled && DataContext is SkillDraftViewModel && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The web search settings for a <see cref="WebSearchSettingsViewModel"/> (set it as the DataContext): the sheet the Tools view opens,
/// and a view of its own for an app with a Tools page of its own (the desktop). The code here is only what a view does: it gives the
/// panel the texts' language and direction (the view model's <see cref="IToolsTexts.Strings"/>, said again when the app's language
/// changes), moves the focus to the first control when it is shown and sends Esc to Cancel and Enter to Save. The key is typed into a
/// masked box bound to the view model's key, which forgets it.
/// </summary>
public partial class WebSearchSettingsView : UserControl
{
    private IToolsTexts? _texts;

    public WebSearchSettingsView()
    {
        FadenstackControls.EnsureLoaded();
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            Follow(DataContext as IToolsTexts);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => WizardFocus.FocusFirst(this), DispatcherPriority.Loaded);
    }

    private void Follow(IToolsTexts? texts)
    {
        if (_texts is not null)
        {
            _texts.PropertyChanged -= OnTextsChanged;
        }
        _texts = texts;
        if (_texts is not null)
        {
            _texts.PropertyChanged += OnTextsChanged;
        }
        ApplyStrings();
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IToolsTexts.Strings) or null or "")
        {
            ApplyStrings();
        }
    }

    /// <summary>The view model's texts for everything in the panel (<see cref="ChatText"/>), with their language and direction.</summary>
    private void ApplyStrings()
    {
        var strings = _texts?.Strings;
        ChatText.SetStrings(this, strings);
        FlowDirection = strings is { IsRightToLeft: true } ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
    }

    /// <summary>Esc cancels; Enter saves (unless a button has the focus: it presses itself).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not WebSearchSettingsViewModel sheet || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        if (e.Key == Key.Escape && sheet.CancelCommand.CanExecute(null))
        {
            sheet.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

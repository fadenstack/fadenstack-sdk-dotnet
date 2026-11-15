using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The Tools view for a <see cref="ToolsViewModel"/> (set it as the DataContext): the page, the details drawer and the sheets, drawn by
/// bindings and commands. The code here is only what a view does: it gives the panel the texts' language and direction (the view model's
/// <see cref="IToolsTexts.Strings"/>, said again when the app's language changes). Esc is two key bindings of the XAML (the plain-http
/// warning first, then the sheet or the drawer), the focus of a drawer, a sheet and the warning is the XAML's too (<c>DrawerFocus</c>,
/// <c>WizardFocus</c>), and a secret value is typed into a masked box bound to its row, which forgets it. The host calls
/// <see cref="ToolsViewModel.OpenAsync"/> when it shows the page.
/// </summary>
public partial class ToolsView : UserControl
{
    private IToolsTexts? _texts;

    public ToolsView()
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
}

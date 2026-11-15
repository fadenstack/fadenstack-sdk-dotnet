using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Tools;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The Tools view for a <see cref="ToolsViewModel"/> (set it as the DataContext): the page, the details drawer and the sheets, drawn by
/// bindings and commands. The code here is only what a view does: it picks the colours, gives the panel the texts' language and direction
/// (the view model's <see cref="IToolsTexts.Strings"/>, said again when the app's language changes), follows its own width (the cards'
/// columns, the drawer's width: WPF has no container queries), and sends Esc to the plain-http warning, the sheet or the drawer. A secret
/// value is typed into a password box that carries it to its row (<c>SecretBox</c>), which forgets it. The host calls
/// <see cref="ToolsViewModel.OpenAsync"/> when it shows the page.
/// </summary>
public partial class ToolsView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(ToolsView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((ToolsView)d, ((ToolsView)d).Theme)));

    /// <summary>How many cards go side by side: as many of at least 300 as fit.</summary>
    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns), typeof(int), typeof(ToolsView), new PropertyMetadata(1));

    /// <summary>The drawer's width: 400, or the whole view where it is narrower (Office's task pane).</summary>
    public static readonly DependencyProperty DrawerWidthProperty = DependencyProperty.Register(
        nameof(DrawerWidth), typeof(double), typeof(ToolsView), new PropertyMetadata(400.0));

    private IToolsTexts? _texts;

    public ToolsView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        DataContextChanged += (_, e) => Follow(e.NewValue as IToolsTexts);
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public double DrawerWidth
    {
        get => (double)GetValue(DrawerWidthProperty);
        set => SetValue(DrawerWidthProperty, value);
    }

    private void Fit(double width)
    {
        // The page keeps 12 at each side and the cards 12 apart: as many columns of at least 300 as fit.
        CardColumns = Math.Max(1, Math.Min(4, (int)Math.Floor((width - 24 + 12) / (300 + 12))));
        DrawerWidth = Math.Min(400, Math.Max(0, width));
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
        SkillViewSupport.ApplyStrings(this, _texts?.Strings);
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IToolsTexts.Strings) or null or "")
        {
            SkillViewSupport.ApplyStrings(this, _texts?.Strings);
        }
    }

    /// <summary>
    /// Esc says no to the plain-http warning first, then closes the sheet over the page or the drawer. (WPF runs only the first of two
    /// input bindings for one key, so these are not key bindings of the XAML as they are in the Avalonia view.)
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || DataContext is not ToolsViewModel page || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (page.Details is { } details && details.CancelPlainHttpCommand.CanExecute(null))
        {
            details.CancelPlainHttpCommand.Execute(null);
            e.Handled = true;
        }
        else if (page.CloseTopCommand.CanExecute(null) && (page.HasSheet || page.IsDetailsOpen))
        {
            page.CloseTopCommand.Execute(null);
            e.Handled = true;
        }
    }
}

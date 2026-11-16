using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fadenstack.UI.Tools;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The web search settings for a <see cref="WebSearchSettingsViewModel"/> (set it as the DataContext), as a view of its own: for an app
/// with a Tools page of its own (the desktop) that opens the same sheet the Tools view opens. The code here is only what a view does: it
/// picks the colours, gives the panel the texts' language and direction (the view model's <see cref="IToolsTexts.Strings"/>, said again
/// when the app's language changes), moves the focus to the first control when it is shown and sends Esc to Cancel and Enter to Save. The
/// key is typed into a password box that carries it to the view model (<c>SecretBox</c>), which forgets it.
/// </summary>
public partial class WebSearchSettingsView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(WebSearchSettingsView), new PropertyMetadata(ChatTheme.System, (d, _) => SkillViewSupport.ApplyTheme((WebSearchSettingsView)d, ((WebSearchSettingsView)d).Theme)));

    private IToolsTexts? _texts;

    public WebSearchSettingsView()
    {
        InitializeComponent();
        SkillViewSupport.ApplyTheme(this, Theme);
        DataContextChanged += (_, e) => Follow(e.NewValue as IToolsTexts);
        SkillViewSupport.FocusFirstWhenLoaded(this);
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
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

    /// <summary>Esc cancels; Enter saves (unless a button has the focus: it presses itself).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not WebSearchSettingsViewModel sheet || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (e.Key == Key.Escape && sheet.CancelCommand.CanExecute(null))
        {
            sheet.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

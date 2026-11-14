using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Fadenstack.UI.Wizard;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The connection wizard for a <see cref="ConnectionWizardViewModel"/> (set it as the DataContext): one panel for each group
/// of its steps, the commands on its buttons, its texts. The code here is only what a view does: it picks the colours, gives
/// the panel the texts' language and direction, writes the three lines that take the app's name or the server's address,
/// sends Enter to the step's primary button and Esc to Cancel or Back, and opens the full terms through
/// <see cref="LinkOpener"/>. A password or a key is read by the button that sends it (<see cref="WizardSecret"/>), never bound.
/// </summary>
public partial class WizardView : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ChatTheme), typeof(WizardView), new PropertyMetadata(ChatTheme.System, (d, _) => ((WizardView)d).ApplyTheme()));

    /// <summary>The app's name for the titles ("How do you want to use {app}?"); the view model has none.</summary>
    public static readonly DependencyProperty AppNameProperty = DependencyProperty.Register(
        nameof(AppName), typeof(string), typeof(WizardView), new PropertyMetadata("", (d, _) => ((WizardView)d).WriteLines()));

    /// <summary>The page with the full own-AI terms; the "Read the full terms" button shows when the host sets it.</summary>
    public static readonly DependencyProperty FullTermsLinkProperty = DependencyProperty.Register(
        nameof(FullTermsLink), typeof(Uri), typeof(WizardView),
        new PropertyMetadata(null, (d, e) => ((WizardView)d).FullTermsButton.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible));

    private ConnectionWizardViewModel? _wizard;

    public WizardView()
    {
        InitializeComponent();
        ApplyTheme();
        FullTermsButton.Click += OnFullTermsClick;
        FullTermsButton.Visibility = Visibility.Collapsed;
        DataContextChanged += (_, _) => Follow(DataContext as ConnectionWizardViewModel);
    }

    public ChatTheme Theme
    {
        get => (ChatTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public string AppName
    {
        get => (string)GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    /// <inheritdoc cref="FullTermsLinkProperty"/>
    public Uri? FullTermsLink
    {
        get => (Uri?)GetValue(FullTermsLinkProperty);
        set => SetValue(FullTermsLinkProperty, value);
    }

    /// <summary>Opens the full terms; the host's own way (its browser), else the skin's link opener.</summary>
    public Action<Uri>? LinkOpener { get; set; }

    private void ApplyTheme()
    {
        var colors = new ResourceDictionary { Source = ChatView.PaletteUri(Theme) };
        var merged = Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Insert(0, colors);
        }
    }

    private void Follow(ConnectionWizardViewModel? wizard)
    {
        if (_wizard is not null)
        {
            _wizard.PropertyChanged -= OnWizardChanged;
        }
        _wizard = wizard;
        if (_wizard is not null)
        {
            _wizard.PropertyChanged += OnWizardChanged;
        }
        ApplyStrings();
    }

    private void OnWizardChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConnectionWizardViewModel.Strings) or null or "":
                ApplyStrings();
                break;
            case nameof(ConnectionWizardViewModel.ServerUrl):
                WriteLines();
                break;
        }
    }

    /// <summary>The view model's texts for everything in the panel (<see cref="ChatText"/>), with their language and direction.</summary>
    private void ApplyStrings()
    {
        var strings = _wizard?.Strings;
        ChatText.SetStrings(this, strings);
        if (strings is null)
        {
            ClearValue(LanguageProperty);
            ClearValue(FlowDirectionProperty);
        }
        else
        {
            Language = XmlLanguage.GetLanguage(strings.Language);
            FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        WriteLines();
    }

    /// <summary>The texts that take a value of the view's own (the app's name) or one the view model gives as a property (the server).</summary>
    private void WriteLines()
    {
        var strings = _wizard?.Strings;
        ChooseTitle.Text = strings?.WizardChooseTitle(AppName) ?? "";
        CodeTitle.Text = strings?.WizardCodeTitle(AppName) ?? "";
        ServerAtText.Text = strings?.WizardServerAt(_wizard?.ServerUrl?.AbsoluteUri ?? "") ?? "";
    }

    private void OnFullTermsClick(object sender, RoutedEventArgs e)
    {
        if (FullTermsLink is { } link)
        {
            (LinkOpener ?? MarkdownText.OpenLink)(link);
        }
    }

    /// <summary>
    /// Esc stops what runs, or goes back where there is a Back; Enter presses the step's primary button. (WPF runs only the
    /// first of two input bindings for one key, so Esc cannot be two key bindings of the XAML as it is in the Avalonia view.)
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _wizard is null || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_wizard.CancelCommand.CanExecute(null))
            {
                _wizard.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (_wizard.BackCommand.CanExecute(null))
            {
                _wizard.BackCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

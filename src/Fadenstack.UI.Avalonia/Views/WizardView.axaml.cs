using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.Wizard;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The connection wizard for a <see cref="ConnectionWizardViewModel"/> (set it as the DataContext): one panel for each group
/// of its steps, the commands on its buttons, its texts. The code here is only what a view does: it gives the panel the texts'
/// language and direction, writes the three lines that take the app's name or the server's address, sends Enter to the
/// step's primary button (Esc is two key bindings of the XAML), and opens the full terms through <see cref="LinkOpener"/>. A password or
/// a key is read by the button that sends it (<see cref="WizardSecret"/>), never bound.
/// </summary>
public partial class WizardView : UserControl
{
    /// <summary>The app's name for the titles ("How do you want to use {app}?"); the view model has none.</summary>
    public static readonly StyledProperty<string> AppNameProperty = AvaloniaProperty.Register<WizardView, string>(nameof(AppName), "");

    /// <summary>The page with the full own-AI terms; the "Read the full terms" button shows when the host sets it.</summary>
    public static readonly StyledProperty<Uri?> FullTermsLinkProperty = AvaloniaProperty.Register<WizardView, Uri?>(nameof(FullTermsLink));

    private ConnectionWizardViewModel? _wizard;

    public WizardView()
    {
        FadenstackControls.EnsureLoaded();
        InitializeComponent();
        FullTermsButton.Click += OnFullTermsClick;
        FullTermsButton.IsVisible = false;
    }

    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    /// <inheritdoc cref="FullTermsLinkProperty"/>
    public Uri? FullTermsLink
    {
        get => GetValue(FullTermsLinkProperty);
        set => SetValue(FullTermsLinkProperty, value);
    }

    /// <summary>Opens the full terms; the host's own way (its browser), else the skin's link opener.</summary>
    public Action<Uri>? LinkOpener { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            Follow(DataContext as ConnectionWizardViewModel);
        }
        else if (change.Property == AppNameProperty)
        {
            WriteLines();
        }
        else if (change.Property == FullTermsLinkProperty)
        {
            FullTermsButton.IsVisible = FullTermsLink is not null;
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
        FlowDirection = strings is { IsRightToLeft: true } ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
        WriteLines();
    }

    /// <summary>The texts that take a value of the view's own (the app's name) or one the view model gives as a property (the server).</summary>
    private void WriteLines()
    {
        var strings = _wizard?.Strings;
        ChooseTitle.Text = strings?.WizardChooseTitle(AppName);
        CodeTitle.Text = strings?.WizardCodeTitle(AppName);
        ServerAtText.Text = strings?.WizardServerAt(_wizard?.ServerUrl?.AbsoluteUri ?? "");
    }

    private void OnFullTermsClick(object? sender, RoutedEventArgs e)
    {
        if (FullTermsLink is { } link)
        {
            (LinkOpener ?? MarkdownText.OpenLink)(link);
        }
    }

    /// <summary>Enter presses the step's primary button (Esc is a key binding of the view's XAML).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && _wizard is not null && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            e.Handled = WizardFocus.PressPrimary(this);
        }
    }
}

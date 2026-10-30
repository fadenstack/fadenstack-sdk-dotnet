using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Views;

public enum ChatTheme
{
    /// <summary>Follows the host application's theme variant, which follows the system by default.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// The chat panel for an <see cref="AgentChatViewModel"/> (set it as the DataContext). Everything it shows comes from
/// the view model, its texts too (<see cref="AgentChatViewModel.Strings"/>); the code here only gives the panel the
/// texts' language and direction, sends on Enter, moves the focus to the composer when the chat asks, and opens the
/// panel's links through <see cref="MarkdownText.OpenLink"/>.
/// </summary>
public partial class ChatView : UserControl
{
    /// <summary>Extra content at the composer's bottom left, for the host's own buttons (e.g. "Add context").</summary>
    public static readonly StyledProperty<object?> ComposerToolbarProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(ComposerToolbar));

    /// <summary>The host's own items in the panel's settings, below the language (sign out, its own options).</summary>
    public static readonly StyledProperty<object?> SettingsContentProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(SettingsContent));

    public static readonly StyledProperty<ChatTheme> AppearanceProperty = AvaloniaProperty.Register<ChatView, ChatTheme>(nameof(Appearance));

    private AgentChatViewModel? _chat;

    public ChatView()
    {
        FadenstackTheme.EnsureDefaults();
        InitializeComponent();
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    public object? ComposerToolbar
    {
        get => GetValue(ComposerToolbarProperty);
        set => SetValue(ComposerToolbarProperty, value);
    }

    public object? SettingsContent
    {
        get => GetValue(SettingsContentProperty);
        set => SetValue(SettingsContentProperty, value);
    }

    public ChatTheme Appearance
    {
        get => GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            Follow(DataContext as AgentChatViewModel);
        }
        else if (change.Property == AppearanceProperty)
        {
            Scope.RequestedThemeVariant = Appearance switch
            {
                ChatTheme.Light => ThemeVariant.Light,
                ChatTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void Follow(AgentChatViewModel? chat)
    {
        if (_chat is not null)
        {
            _chat.PropertyChanged -= OnChatChanged;
            _chat.LinkRequested -= OnLinkRequested;
            _chat.ComposerFocusRequested -= OnComposerFocusRequested;
        }
        _chat = chat;
        if (_chat is not null)
        {
            _chat.PropertyChanged += OnChatChanged;
            _chat.LinkRequested += OnLinkRequested;
            _chat.ComposerFocusRequested += OnComposerFocusRequested;
        }
        ApplyStrings();
    }

    /// <summary>The user picked another language: the panel's texts, language and direction follow.</summary>
    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentChatViewModel.Strings) or null or "")
        {
            ApplyStrings();
        }
    }

    /// <summary>The view model's texts for everything in the panel (<see cref="ChatText"/>), with their language and direction.</summary>
    private void ApplyStrings()
    {
        var strings = _chat?.Strings;
        ChatText.SetStrings(this, strings);
        if (strings is null)
        {
            ClearValue(FlowDirectionProperty);
            return;
        }
        FlowDirection = strings.IsRightToLeft ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
    }

    private static void OnLinkRequested(object? sender, Uri url) => MarkdownText.OpenLink(url);

    private void OnComposerFocusRequested(object? sender, EventArgs e)
    {
        Composer.Focus();
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
    }

    /// <summary>Enter sends, Shift+Enter is a new line; the skill picker's keys (arrows, Tab) are the host's to add.</summary>
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _chat is { SendCommand: var send } && send.CanExecute(null))
        {
            send.Execute(null);
            e.Handled = true;
        }
    }
}
